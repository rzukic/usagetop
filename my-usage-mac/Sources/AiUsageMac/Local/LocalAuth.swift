import CryptoKit
import Foundation
import Network

/// A provider's own OAuth sign-in, as the Windows app runs it
/// (`my-usage-win/src/AiUsage.Core/Auth/OAuthConfig.cs`): authorization code
/// with PKCE, the browser sent back to a port on this Mac that catches the code.
struct OAuthClient: Sendable {
    let provider: String
    let authorizeURL: String
    let tokenURL: String
    let clientId: String
    let scopes: String
    let callbackPath: String
    /// Nil takes any free port; OpenAI only accepts 1455.
    let fixedPort: UInt16?
    let extra: [(String, String)]
    /// Claude's token endpoint takes JSON; the others take a form.
    let jsonTokenRequest: Bool
    /// Google's installed-app client has one; it ships in every Antigravity install.
    let secret: String?

    static let claude = OAuthClient(
        provider: "claude", authorizeURL: "https://claude.ai/oauth/authorize",
        tokenURL: "https://console.anthropic.com/v1/oauth/token", clientId: "9d1c250a-e61b-44d9-88ed-5944d1962f5e",
        // All the usage and profile endpoints need: the token cannot run models or mint keys.
        scopes: "user:profile", callbackPath: "/callback", fixedPort: nil, extra: [("code", "true")],
        jsonTokenRequest: true, secret: nil)

    static let chatGPT = OAuthClient(
        provider: "chatgpt", authorizeURL: "https://auth.openai.com/oauth/authorize",
        tokenURL: "https://auth.openai.com/oauth/token", clientId: "app_EMoamEEZ73f0CkXaXp7hrann",
        scopes: "openid profile email offline_access", callbackPath: "/auth/callback", fixedPort: 1455,
        extra: [("id_token_add_organizations", "true"), ("codex_cli_simplified_flow", "true")],
        jsonTokenRequest: false, secret: nil)

    static let antigravity = OAuthClient(
        provider: "antigravity", authorizeURL: "https://accounts.google.com/o/oauth2/v2/auth",
        tokenURL: "https://oauth2.googleapis.com/token",
        clientId: "1071006060591-tmhssin2h21lcre235vtolojh4g403ep.apps.googleusercontent.com",
        scopes: "https://www.googleapis.com/auth/cloud-platform", callbackPath: "/oauth-callback", fixedPort: nil,
        extra: [("access_type", "offline"), ("prompt", "consent")], jsonTokenRequest: false,
        secret: "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") // gitleaks:allow (public: it ships in every Antigravity install)

    static func of(_ provider: String) -> OAuthClient? {
        switch provider {
        case "claude": claude
        case "chatgpt": chatGPT
        case "antigravity": antigravity
        default: nil
        }
    }
}

/// What a token request gave back, ready to store.
struct Session: Sendable {
    let access: String
    let refresh: String?
    let expiresAt: Date?
    let accountId: String?
}

enum OAuthError: LocalizedError {
    case failed(String)
    var errorDescription: String? { if case .failed(let why) = self { why } else { nil } }
}

enum LocalAuth {
    // MARK: PKCE

    static func verifier() -> String { random(32) }

    static func challenge(_ verifier: String) -> String {
        base64URL(Data(SHA256.hash(data: Data(verifier.utf8))))
    }

    static func random(_ bytes: Int) -> String {
        var data = Data(count: bytes)
        _ = data.withUnsafeMutableBytes { SecRandomCopyBytes(kSecRandomDefault, bytes, $0.baseAddress!) }
        return base64URL(data)
    }

    private static func base64URL(_ data: Data) -> String {
        data.base64EncodedString().replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
    }

    static func authorizeURL(_ client: OAuthClient, redirect: String, challenge: String, state: String) -> String {
        let fields = [("client_id", client.clientId), ("response_type", "code"), ("redirect_uri", redirect),
                      ("scope", client.scopes), ("state", state), ("code_challenge", challenge),
                      ("code_challenge_method", "S256")] + client.extra
        return client.authorizeURL + "?" + Net.form(fields)
    }

    // MARK: Tokens

    static func exchange(_ client: OAuthClient, code: String, verifier: String, redirect: String, state: String) async throws -> Session {
        let reply: Net.Reply
        if client.jsonTokenRequest {
            reply = try await Net.postJSON(client.tokenURL, headers: ["Accept": "application/json"], object: [
                "grant_type": "authorization_code", "code": code, "redirect_uri": redirect,
                "client_id": client.clientId, "code_verifier": verifier, "state": state,
            ])
        } else {
            var fields = [("grant_type", "authorization_code"), ("code", code), ("redirect_uri", redirect),
                          ("client_id", client.clientId), ("code_verifier", verifier)]
            if let secret = client.secret { fields.append(("client_secret", secret)) }
            reply = try await Net.postForm(client.tokenURL, headers: ["Accept": "application/json"], fields: fields)
        }
        guard reply.code == 200 else {
            throw OAuthError.failed("Token exchange failed (HTTP \(reply.code)): \(reply.text.prefix(200))")
        }
        return try session(reply, keeping: nil)
    }

    /// A new access token for a stored refresh token. A reply without a new
    /// refresh token keeps the old one.
    static func refresh(_ client: OAuthClient, refreshToken: String) async throws -> Session {
        let reply: Net.Reply
        if client.jsonTokenRequest {
            reply = try await Net.postJSON(client.tokenURL, headers: ["Accept": "application/json"], object: [
                "grant_type": "refresh_token", "refresh_token": refreshToken, "client_id": client.clientId,
            ])
        } else {
            var fields = [("grant_type", "refresh_token"), ("refresh_token", refreshToken),
                          ("client_id", client.clientId), ("scope", client.scopes)]
            if let secret = client.secret { fields.append(("client_secret", secret)) }
            reply = try await Net.postForm(client.tokenURL, headers: ["Accept": "application/json"], fields: fields)
        }
        guard reply.code == 200 else { throw OAuthError.failed("Renewal refused (HTTP \(reply.code))") }
        return try session(reply, keeping: refreshToken)
    }

    private static func session(_ reply: Net.Reply, keeping refresh: String?) throws -> Session {
        guard let json = reply.json, let access = JSON.text(json, "access_token") else {
            throw OAuthError.failed("No access token in the reply")
        }
        let expires = JSON.number(json, "expires_in").map { Date().addingTimeInterval($0) }
        return Session(access: access, refresh: JSON.text(json, "refresh_token") ?? refresh, expiresAt: expires,
                       accountId: JSON.text(json, "id_token").flatMap(chatGPTAccount))
    }

    /// `chatgpt_account_id`, wherever it sits in the id_token's claims.
    private static func chatGPTAccount(_ idToken: String) -> String? {
        let parts = idToken.split(separator: ".")
        guard parts.count >= 2 else { return nil }
        var payload = parts[1].replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        payload += String(repeating: "=", count: (4 - payload.count % 4) % 4)
        guard let data = Data(base64Encoded: payload), let claims = try? JSONSerialization.jsonObject(with: data) else { return nil }
        func find(_ value: Any) -> String? {
            if let object = value as? [String: Any] {
                if let id = object["chatgpt_account_id"] as? String { return id }
                for nested in object.values { if let id = find(nested) { return id } }
            }
            if let array = value as? [Any] { for item in array { if let id = find(item) { return id } } }
            return nil
        }
        return find(claims)
    }

    // MARK: Muse

    /// Meta's device sign-in, the one the Muse Code CLI uses: a code to approve
    /// in the browser, then polling until a token appears. It does not expire,
    /// so there is nothing to renew.
    enum Muse {
        private static let auth = "https://auth.meta.com"
        private static let clientId = "1031625952748946"

        struct Started: Sendable {
            let deviceCode: String
            let userCode: String
            let url: String
            let interval: Double
            let expiresIn: Double
        }

        static func start() async throws -> Started {
            let reply = try await Net.postForm("\(auth)/oidc/device/authorization/", headers: ["Accept": "application/json"],
                                               fields: [("client_id", clientId)])
            guard (200..<300).contains(reply.code), let json = reply.json else {
                throw OAuthError.failed("Sign-in could not be started (HTTP \(reply.code))")
            }
            guard let device = JSON.text(json, "device_code"), let user = JSON.text(json, "user_code"),
                  let url = JSON.text(json, "verification_uri_complete") ?? JSON.text(json, "verification_uri") else {
                throw OAuthError.failed("The sign-in reply was incomplete")
            }
            return Started(deviceCode: device, userCode: user, url: url,
                           interval: max(1, JSON.number(json, "interval") ?? 5),
                           expiresIn: min(max(JSON.number(json, "expires_in") ?? 900, 60), 1800))
        }

        static func awaitToken(_ started: Started) async throws -> String {
            var interval = started.interval
            let deadline = Date().addingTimeInterval(started.expiresIn)
            while Date() < deadline {
                try await Task.sleep(for: .seconds(interval))
                let reply = try await Net.postForm("\(auth)/oidc/device/token/", headers: ["Accept": "application/json"], fields: [
                    ("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                    ("device_code", started.deviceCode), ("client_id", clientId),
                ])
                if (200..<300).contains(reply.code) {
                    guard let token = JSON.text(reply.json, "access_token") else { throw OAuthError.failed("Sign-in returned no token") }
                    return token
                }
                switch JSON.text(reply.json, "error") {
                case "authorization_pending": continue
                case "slow_down": interval += 5
                case "access_denied": throw OAuthError.failed("Sign-in was denied")
                case "expired_token": throw OAuthError.failed("The code expired")
                default: throw OAuthError.failed("Sign-in failed (HTTP \(reply.code))")
                }
            }
            throw OAuthError.failed("Sign-in timed out")
        }
    }
}

/// Catches the browser's redirect on this Mac: one GET to the callback path,
/// answered with a page that says the window can be closed. It listens on the
/// loopback interface only, both 127.0.0.1 and ::1, since "localhost" can be
/// either.
final class LoopbackCatcher: @unchecked Sendable {
    struct Result: Sendable {
        let code: String?
        let state: String?
        let error: String?
    }

    private let listener: NWListener
    private let path: String
    private let queue = DispatchQueue(label: "usagetop.loopback")
    private var continuation: CheckedContinuation<Result, Error>?
    private var finished = false

    init(port: UInt16?, path: String) throws {
        let parameters = NWParameters.tcp
        parameters.requiredInterfaceType = .loopback
        parameters.allowLocalEndpointReuse = true
        let endpoint = port.flatMap(NWEndpoint.Port.init(rawValue:)) ?? .any
        do {
            listener = try NWListener(using: parameters, on: endpoint)
        } catch {
            throw OAuthError.failed(port.map { "Port \($0) is busy; is another sign-in open?" } ?? "No free port to sign in on")
        }
        self.path = path
    }

    /// Starts listening and returns the port it got.
    func start() async throws -> UInt16 {
        try await withCheckedThrowingContinuation { (ready: CheckedContinuation<UInt16, Error>) in
            // Every state change arrives on `queue`, so this needs no lock.
            final class Once: @unchecked Sendable { var done = false }
            let once = Once()
            listener.stateUpdateHandler = { [weak self] state in
                guard let self else { return }
                switch state {
                case .ready:
                    if !once.done, let port = self.listener.port?.rawValue { once.done = true; ready.resume(returning: port) }
                case .failed(let error):
                    if !once.done { once.done = true; ready.resume(throwing: OAuthError.failed("Could not listen: \(error.localizedDescription)")) }
                    self.finish(.failure(error))
                default:
                    break
                }
            }
            listener.newConnectionHandler = { [weak self] connection in self?.handle(connection) }
            listener.start(queue: queue)
        }
    }

    /// Waits for the redirect, at most `timeout`.
    func wait(timeout: TimeInterval) async throws -> Result {
        queue.asyncAfter(deadline: .now() + timeout) { [weak self] in
            self?.finish(.failure(OAuthError.failed("Sign-in timed out")))
        }
        return try await withCheckedThrowingContinuation { continuation in
            queue.async {
                if self.finished {
                    continuation.resume(throwing: OAuthError.failed("Sign-in was cancelled"))
                } else {
                    self.continuation = continuation
                }
            }
        }
    }

    func cancel() {
        queue.async { self.finish(.failure(OAuthError.failed("Sign-in was cancelled"))) }
    }

    private func handle(_ connection: NWConnection) {
        connection.start(queue: queue)
        connection.receive(minimumIncompleteLength: 1, maximumLength: 16_384) { [weak self] data, _, _, _ in
            guard let self else { return }
            let request = data.map { String(decoding: $0, as: UTF8.self) } ?? ""
            let target = request.split(separator: " ", maxSplits: 2).dropFirst().first.map(String.init) ?? ""
            let components = URLComponents(string: "http://localhost" + target)
            guard components?.path == self.path else {
                self.respond(connection, status: "404 Not Found", body: "Not here.")
                return
            }
            let items = components?.queryItems ?? []
            func value(_ name: String) -> String? { items.first { $0.name == name }?.value }
            let result = Result(code: value("code"), state: value("state"), error: value("error"))
            self.respond(connection, status: "200 OK", body: result.code != nil
                         ? "Signed in. You can close this tab and go back to usagetop."
                         : "Sign-in did not finish: \(result.error ?? "no code"). You can close this tab.")
            self.finish(.success(result))
        }
    }

    private func respond(_ connection: NWConnection, status: String, body: String) {
        let html = "<!doctype html><meta charset=utf-8><title>usagetop</title>"
            + "<body style=\"font:15px -apple-system,sans-serif;margin:3em\">\(body)</body>"
        let response = "HTTP/1.1 \(status)\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: \(html.utf8.count)\r\nConnection: close\r\n\r\n\(html)"
        connection.send(content: Data(response.utf8), completion: .contentProcessed { _ in connection.cancel() })
    }

    /// Runs on `queue`.
    private func finish(_ outcome: Swift.Result<Result, Error>) {
        guard !finished else { return }
        finished = true
        listener.cancel()
        guard let continuation else { return }
        self.continuation = nil
        switch outcome {
        case .success(let result): continuation.resume(returning: result)
        case .failure(let error): continuation.resume(throwing: error)
        }
    }
}

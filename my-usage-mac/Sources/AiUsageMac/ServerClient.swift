import Foundation

enum ServerError: LocalizedError {
    case notConfigured
    case unauthorized
    case status(Int)
    /// The server said why, in its `{"error": "..."}`.
    case refused(String)
    case transport(String)
    case decoding(String)

    var errorDescription: String? {
        switch self {
        case .notConfigured: "No server set"
        case .unauthorized: "Token rejected"
        case .status(let code): "Server said \(code)"
        case .refused(let message): message
        case .transport(let detail): detail
        case .decoding(let detail): "Unreadable reply: \(detail)"
        }
    }
}

/// An account the server follows, as `GET /api/credentials` describes it:
/// whether something is stored, never what.
struct ServerAccount: Decodable, Identifiable, Hashable {
    let key: String
    let provider: String
    let name: String?
    let displayName: String
    let removable: Bool
    let configured: Bool
    let renews: Bool

    var id: String { key }

    /// "Claude · Work", or "Claude #2" for an added account with no name, as
    /// the rest of the app names accounts. The server's own `displayName`
    /// would call both of two unnamed accounts "Claude".
    var title: String {
        let base = Provider(key: provider).displayName
        if let name, !name.isEmpty { return "\(base) · \(name)" }
        guard key != provider else { return base }
        let suffix = key.hasPrefix(provider + "-") ? String(key.dropFirst(provider.count + 1)) : key
        return "\(base) #\(suffix)"
    }

    /// Mistral and Kimi take an API key; the others sign in.
    var takesKey: Bool { ["mistral", "kimi"].contains(provider) }
}

/// The first step of a sign-in the server runs: a page to open and either a
/// code to type there ("device") or something to paste back ("code", "redirect").
struct SignInStep: Decodable, Hashable {
    let kind: String
    let url: String
    let userCode: String?
    let instructions: String?
}

/// The app's side of the server's API. One round trip draws every screen.
struct ServerClient: Sendable {
    let baseURL: URL
    let token: String

    private var session: URLSession {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 20
        config.waitsForConnectivity = false
        return URLSession(configuration: config)
    }

    /// The reading and its history. `since` asks only for what happened after
    /// that moment.
    func state(since: Date? = nil, points: Int = 1500) async throws -> StateResponse {
        var components = URLComponents(url: baseURL.appendingPathComponent("api/state"),
                                       resolvingAgainstBaseURL: false)
        var items = [
            URLQueryItem(name: "days", value: "35"),
            URLQueryItem(name: "points", value: String(points)),
        ]
        if let since {
            items.append(URLQueryItem(name: "since", value: String(Int(since.timeIntervalSince1970 * 1000))))
        }
        components?.queryItems = items
        guard let url = components?.url else { throw ServerError.notConfigured }

        return try decode(StateResponse.self, from: await request(url, method: "GET"))
    }

    /// Asks the server to poll every provider now. It will not do that more than
    /// twice a minute, so a failure here is not worth surfacing.
    func refresh() async {
        _ = try? await call("refresh", method: "POST", body: [:])
    }

    // MARK: Accounts

    func accounts() async throws -> [ServerAccount] {
        try decode([ServerAccount].self, from: await call("credentials", method: "GET"))
    }

    /// Another account of a provider; it starts empty.
    func addAccount(provider: String) async throws -> ServerAccount {
        try decode(ServerAccount.self, from: await call("accounts", method: "POST", body: ["provider": provider]))
    }

    func rename(_ key: String, to name: String) async throws {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        _ = try await call("accounts/\(escape(key))", method: "PATCH",
                           body: ["name": trimmed.isEmpty ? NSNull() : trimmed])
    }

    /// Only an added account can go; the first of each provider is signed out instead.
    func remove(_ key: String) async throws {
        _ = try await call("accounts/\(escape(key))", method: "DELETE")
    }

    func signOut(_ key: String) async throws {
        _ = try await call("credentials/\(escape(key))", method: "DELETE")
    }

    /// An API key, with the settings that travel with it: Kimi's region and
    /// Mistral's fallback budget.
    func setKey(_ key: String, value: String, kimiChina: Bool? = nil,
                mistralBudget: Double? = nil, mistralSpent: Double? = nil) async throws {
        var body: [String: Any] = ["token": value]
        if let kimiChina { body["kimiChina"] = kimiChina }
        if let mistralBudget { body["mistralBudget"] = mistralBudget }
        if let mistralSpent { body["mistralSpent"] = mistralSpent }
        _ = try await call("credentials/\(escape(key))", method: "PUT", body: body)
    }

    func startSignIn(_ key: String) async throws -> SignInStep {
        try decode(SignInStep.self, from: await call("signin/\(escape(key))/start", method: "POST", body: [:]))
    }

    /// Finishes a "code" or "redirect" sign-in with what the page showed.
    func completeSignIn(_ key: String, input: String) async throws {
        _ = try await call("signin/\(escape(key))/complete", method: "POST", body: ["input": input])
    }

    /// "pending", "done", "none", or "failed: why", for a device sign-in.
    func signInStatus(_ key: String) async throws -> String {
        struct Status: Decodable { let status: String }
        return try decode(Status.self, from: await call("signin/\(escape(key))/status", method: "GET")).status
    }

    // MARK: Plumbing

    private func escape(_ key: String) -> String {
        key.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? key
    }

    private func call(_ path: String, method: String, body: [String: Any]? = nil) async throws -> Data {
        guard let url = URL(string: baseURL.absoluteString + "/api/" + path) else { throw ServerError.notConfigured }
        let data = try body.map { try JSONSerialization.data(withJSONObject: $0) }
        return try await request(url, method: method, body: data)
    }

    private func decode<T: Decodable>(_ type: T.Type, from data: Data) throws -> T {
        do {
            return try JSONDecoder().decode(type, from: data)
        } catch {
            throw ServerError.decoding(error.localizedDescription)
        }
    }

    private func request(_ url: URL, method: String, body: Data? = nil) async throws -> Data {
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if !token.isEmpty {
            request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        }
        if let body {
            request.httpBody = body
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }

        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await session.data(for: request)
        } catch {
            throw ServerError.transport((error as NSError).localizedDescription)
        }

        guard let http = response as? HTTPURLResponse else {
            throw ServerError.transport("No reply")
        }
        if http.statusCode == 401 { throw ServerError.unauthorized }
        guard (200..<300).contains(http.statusCode) else {
            struct Refusal: Decodable { let error: String }
            if let refusal = try? JSONDecoder().decode(Refusal.self, from: data), !refusal.error.isEmpty {
                throw ServerError.refused(refusal.error)
            }
            throw ServerError.status(http.statusCode)
        }
        return data
    }
}

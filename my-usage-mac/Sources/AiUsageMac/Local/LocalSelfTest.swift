import Foundation

/// `usagetop.app/Contents/MacOS/AiUsageMac --local-selftest`: the no-server
/// mode, end to end, against a separate account store and history that it
/// deletes again. Providers are really asked, with keys that are not keys, so
/// what comes back is each provider refusing them, not a reading.
enum LocalSelfTest {
    static func run() async -> Int {
        var failures = 0
        func check(_ ok: Bool, _ what: String) {
            print(ok ? "ok    " : "FAIL  ", what)
            if !ok { failures += 1 }
        }

        let scratch = LocalSource(profile: "selftest")
        await scratch.eraseForTest()
        defer { Task { await scratch.eraseForTest() } }

        do {
            let source = LocalSource(profile: "selftest")
            check(try await source.accounts().isEmpty, "a new store follows nothing")
            let empty = try await source.state(since: nil, points: 100)
            check(empty.snapshot?.usages.isEmpty == true, "and reads as an empty reading, not none")

            _ = try await source.addAccount(provider: "kimi")
            let first = try await source.addAccount(provider: "claude")
            let second = try await source.addAccount(provider: "claude")
            check((first.key, second.key) == ("claude", "claude-2"), "a provider's first account has the plain key, the next is numbered")
            try await source.rename("claude-2", to: " Work ")
            try await source.setKey("kimi", value: "not-a-real-key", kimiChina: nil, mistralBudget: nil, mistralSpent: nil)
            try await source.setKey("claude-2", value: "not-a-real-token", kimiChina: nil, mistralBudget: nil, mistralSpent: nil)
            let listed = try await source.accounts()
            check(listed.map(\.key) == ["claude", "claude-2", "kimi"], "accounts are listed by provider: \(listed.map(\.key))")
            check(listed.map(\.configured) == [false, true, true], "a stored key or token marks the account set up")
            check(listed.first { $0.key == "claude-2" }?.title == "Claude · Work", "a name is trimmed and shown")

            let state = try await source.state(since: nil, points: 100)
            let usages = state.snapshot?.usages ?? []
            for usage in usages { print("        \(usage.account): \(usage.state) \(usage.message ?? "")") }
            check(usages.map(\.account) == ["claude", "claude-2", "kimi"], "one reading per account, in order")
            check(usages.first?.state == "NOT_CONFIGURED", "an account with nothing stored is not asked")
            check(usages.dropFirst().allSatisfy { $0.state == "ERROR" && $0.message != nil }, "a refused key says why")
            check(usages.first { $0.account == "claude-2" }?.accountName == "Work", "a reading carries its account's name")

            let reopened = LocalSource(profile: "selftest")
            check(try await reopened.accounts().map(\.key) == ["claude", "claude-2", "kimi"], "the accounts survive a restart (Keychain)")

            try await reopened.remove("claude")
            let readded = try await reopened.addAccount(provider: "claude")
            let third = try await reopened.addAccount(provider: "claude")
            check(readded.key == "claude", "a removed first account comes back under its own key")
            check(third.key == "claude-3", "a numbered slot is never handed out twice: \(third.key)")

            // A browser sign-in, caught on this Mac: a redirect from another
            // attempt is refused, and a fresh one gets as far as the token
            // exchange, which refuses the made-up code.
            let step = try await reopened.startSignIn("claude")
            check(step.kind == "browser", "Claude signs in through the browser, caught on this Mac")
            let query = URLComponents(string: step.url)?.queryItems ?? []
            let redirect = query.first { $0.name == "redirect_uri" }?.value ?? ""
            let state1 = query.first { $0.name == "state" }?.value ?? ""
            check(redirect.hasPrefix("http://localhost:") && redirect.hasSuffix("/callback"), "it redirects to a local port: \(redirect)")
            _ = try? await URLSession.shared.data(from: URL(string: "\(redirect)?code=made-up&state=not-\(state1)")!)
            let refused = await finished(reopened, "claude")
            check(refused.contains("another attempt"), "a code from another attempt is refused: \(refused)")

            let again = try await reopened.startSignIn("claude")
            let query2 = URLComponents(string: again.url)?.queryItems ?? []
            let redirect2 = query2.first { $0.name == "redirect_uri" }?.value ?? ""
            let state2 = query2.first { $0.name == "state" }?.value ?? ""
            _ = try? await URLSession.shared.data(from: URL(string: "\(redirect2)?code=made-up&state=\(state2)")!)
            let exchanged = await finished(reopened, "claude")
            check(exchanged.contains("Token exchange failed"), "the right attempt goes on to the token exchange: \(exchanged.prefix(70))")

            let chatGPT = try await reopened.addAccount(provider: "chatgpt")
            let openAI = try await reopened.startSignIn(chatGPT.key)
            check(openAI.url.contains("redirect_uri=http%3A%2F%2Flocalhost%3A1455%2Fauth%2Fcallback"), "ChatGPT gets OpenAI's fixed port 1455")
            try await reopened.remove(chatGPT.key)

            let google = try await reopened.addAccount(provider: "antigravity")
            let googleStep = try await reopened.startSignIn(google.key)
            check(googleStep.url.hasPrefix("https://accounts.google.com/") && googleStep.url.contains("access_type=offline"),
                  "Antigravity signs in with Google, asking for a refresh token")
            try await reopened.remove(google.key)

            do {
                _ = try await reopened.startSignIn("kimi")
                check(false, "a key provider has no sign-in")
            } catch {
                check(true, "a key provider has no sign-in")
            }
        } catch {
            check(false, "unexpected error: \(error)")
        }

        print(failures == 0 ? "all passed" : "\(failures) failed")
        await scratch.eraseForTest()
        return failures
    }

    /// Waits for a sign-in to stop being "pending".
    private static func finished(_ source: LocalSource, _ key: String) async -> String {
        for _ in 0..<40 {
            if let status = try? await source.signInStatus(key), status != "pending" { return status }
            try? await Task.sleep(for: .milliseconds(250))
        }
        return "still pending"
    }
}

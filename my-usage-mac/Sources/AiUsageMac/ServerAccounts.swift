import AppKit
import Foundation
import Observation

/// The server's accounts, managed from Settings, as the Windows and Android
/// apps manage them (`my-usage-win/src/AiUsage/Views/MainWindow.Server.cs`):
/// add an account, set or replace its key, sign in again, rename it, sign it
/// out, remove it. The server never hands a token back, so nothing here can
/// read a key or a session, only replace one.
///
/// It lives on the app model, not in the view: a sign-in sends you to the
/// browser, the panel closes behind you, and the step has to be there when you
/// come back to paste the code.
@MainActor
@Observable
final class ServerAccounts {
    /// What an account's row is in the middle of.
    enum Step: Equatable {
        case key
        case signIn(SignInStep)
        case rename
        case confirmSignOut
        case confirmRemove
    }

    private(set) var accounts: [ServerAccount] = []
    private(set) var loaded = false
    /// A problem reading the list, shown in place of it.
    private(set) var listProblem: String?
    private(set) var steps: [String: Step] = [:]
    private(set) var problems: [String: String] = [:]
    private(set) var busy: Set<String> = []

    /// The providers an account can be added for, in the rail's order.
    static let providers = ["claude", "chatgpt", "mistral", "kimi", "muse", "antigravity"]

    private weak var app: AppModel?
    private var polls: [String: Task<Void, Never>] = [:]
    private var loading = false

    init(app: AppModel) {
        self.app = app
    }

    /// The server's accounts, or this Mac's own when no server is set.
    private var client: (any UsageBackend)? {
        guard let app, !app.isDemo else { return nil }
        return app.backend
    }

    // MARK: The list

    func load() async {
        guard let client else {
            accounts = []
            loaded = false
            return
        }
        guard !loading else { return }
        loading = true
        defer { loading = false }
        do {
            let list = try await client.accounts()
            accounts = list
            listProblem = nil
            // A row that went away takes its step with it.
            let keys = Set(list.map(\.key))
            for key in steps.keys where !keys.contains(key) { close(key) }
        } catch {
            listProblem = message(error)
        }
        loaded = true
    }

    func add(provider: String) async {
        guard let client else { return }
        do {
            let account = try await client.addAccount(provider: provider)
            await load()
            await act(account)
        } catch {
            listProblem = message(error)
        }
    }

    // MARK: A row

    func status(_ account: ServerAccount) -> String {
        if !account.configured { return "Not set up" }
        if account.takesKey { return "Key stored" }
        return account.renews ? "Signed in · renews itself" : "Token stored"
    }

    func actionTitle(_ account: ServerAccount) -> String {
        account.takesKey
            ? (account.configured ? "Replace key" : "Set key")
            : (account.configured ? "Sign in again" : "Sign in")
    }

    /// The row's main button: a key form, or a sign-in on the server.
    func act(_ account: ServerAccount) async {
        if account.takesKey {
            show(.key, for: account.key)
        } else {
            await startSignIn(account)
        }
    }

    func show(_ step: Step, for key: String) {
        close(key)
        steps[key] = step
    }

    func close(_ key: String) {
        polls[key]?.cancel()
        polls[key] = nil
        steps[key] = nil
        problems[key] = nil
    }

    func saveKey(_ account: ServerAccount, value: String, kimiChina: Bool?, budget: String, spent: String) async {
        let key = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !key.isEmpty else {
            problems[account.key] = "Paste a key first."
            return
        }
        await run(account) { client in
            try await client.setKey(account.key, value: key, kimiChina: kimiChina,
                                    mistralBudget: Self.number(budget), mistralSpent: Self.number(spent))
        }
    }

    func finishSignIn(_ account: ServerAccount, input: String) async {
        let pasted = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !pasted.isEmpty else {
            problems[account.key] = "Paste what the page showed first."
            return
        }
        await run(account) { client in try await client.completeSignIn(account.key, input: pasted) }
    }

    func rename(_ account: ServerAccount, to name: String) async {
        await run(account) { client in try await client.rename(account.key, to: name) }
    }

    func signOut(_ account: ServerAccount) async {
        await run(account) { client in try await client.signOut(account.key) }
    }

    func remove(_ account: ServerAccount) async {
        await run(account) { client in try await client.remove(account.key) }
    }

    // MARK: Sign-in

    private func startSignIn(_ account: ServerAccount) async {
        guard let client else { return }
        close(account.key)
        busy.insert(account.key)
        defer { busy.remove(account.key) }
        do {
            let step = try await client.startSignIn(account.key)
            steps[account.key] = .signIn(step)
            // A sign-in this Mac catches itself goes straight to the browser.
            if step.kind == "browser", let url = URL(string: step.url) { NSWorkspace.shared.open(url) }
            if step.kind == "device" || step.kind == "browser" { watch(account.key) }
        } catch {
            problems[account.key] = message(error)
        }
    }

    /// A device sign-in finishes on the provider's page; the server says when.
    private func watch(_ key: String) {
        polls[key]?.cancel()
        polls[key] = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(3))
                guard !Task.isCancelled, let self, let client = self.client else { return }
                do {
                    let status = try await client.signInStatus(key)
                    if status == "pending" { continue }
                    self.close(key)
                    if status != "done" { self.problems[key] = status }
                    await self.afterChange()
                } catch {
                    self.close(key)
                    self.problems[key] = self.message(error)
                }
                return
            }
        }
    }

    // MARK: Plumbing

    /// One change to an account: the call, then the list again, then a fresh
    /// reading once the server has had a moment to poll the account.
    private func run(_ account: ServerAccount, _ change: (any UsageBackend) async throws -> Void) async {
        guard let client else { return }
        busy.insert(account.key)
        defer { busy.remove(account.key) }
        do {
            try await change(client)
            close(account.key)
            await afterChange()
        } catch {
            problems[account.key] = message(error)
        }
    }

    private func afterChange() async {
        await load()
        Task { [weak app] in
            try? await Task.sleep(for: .seconds(4))
            await app?.refresh()
        }
    }

    private func message(_ error: Error) -> String {
        (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
    }

    /// "12", "12.50" or "12,50"; nil when empty or not a number.
    private static func number(_ text: String) -> Double? {
        let trimmed = text.trimmingCharacters(in: .whitespaces).replacingOccurrences(of: ",", with: ".")
        return trimmed.isEmpty ? nil : Double(trimmed)
    }
}

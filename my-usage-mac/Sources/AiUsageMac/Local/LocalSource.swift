import AppKit
import Foundation

/// Where the panel's readings and the accounts in Settings come from: a server,
/// or this Mac itself. Both answer the same calls, so every screen works the
/// same either way.
protocol UsageBackend: Sendable {
    func state(since: Date?, points: Int) async throws -> StateResponse
    func refresh() async
    func accounts() async throws -> [ServerAccount]
    func addAccount(provider: String) async throws -> ServerAccount
    func rename(_ key: String, to name: String) async throws
    func remove(_ key: String) async throws
    func signOut(_ key: String) async throws
    func setKey(_ key: String, value: String, kimiChina: Bool?, mistralBudget: Double?, mistralSpent: Double?) async throws
    func startSignIn(_ key: String) async throws -> SignInStep
    func completeSignIn(_ key: String, input: String) async throws
    func signInStatus(_ key: String) async throws -> String
}

extension ServerClient: UsageBackend {}

/// The app without a server: it asks the providers itself, keeps their history
/// on this Mac, and holds the sign-ins and keys in the Keychain. What the server
/// does for a household, done for one Mac.
///
/// Accounts start empty, as on the server: one is added in Settings, or by
/// storing a sign-in or key for it, and any can be removed. A provider's first
/// account has the plain key ("claude"), and gets its history back when added
/// again; any after it are numbered and never reused.
actor LocalSource: UsageBackend {
    static let shared = LocalSource(profile: "")

    /// Everything stored for one account. Kept as one Keychain item, never on disk.
    private struct Account: Codable {
        var key: String
        var provider: String
        var name: String?
        var token: String?
        var refresh: String?
        var expiresAt: Double?
        var accountId: String?
        var kimiChina: Bool?
        var mistralBudget: Double?
        var mistralSpent: Double?

        var slot: String { key == provider ? "" : String(key.dropFirst(provider.count + 1)) }
    }

    private struct Vault: Codable {
        var next = 2
        var accounts: [Account] = []
    }

    private static let order = ["claude", "chatgpt", "mistral", "kimi", "muse", "antigravity"]
    /// How often the providers are asked: as the server does while nothing moves,
    /// and more often while a window is being spent.
    private static let idle: TimeInterval = 300
    private static let active: TimeInterval = 120
    private static let forcedGap: TimeInterval = 30
    /// A refused token is renewed at most this often, so one that keeps being
    /// refused for some other reason does not spend a renewal every round.
    private static let rejectedCooldown: TimeInterval = 1800

    private var vault: Vault
    private var history: LocalHistory
    /// The Keychain item the accounts live in. A profile other than the empty
    /// one keeps a separate set, for the self-test.
    private let vaultItem: String
    private var current: [ProviderUsage] = []
    private var fetchedAt: Date?
    private var lastRound: Date?
    private var lastForced: Date?
    private var lastMove: Date?
    private var round: Task<Void, Never>?
    private var renewed: [String: Date] = [:]
    private var signIns: [String: String] = [:]
    private var catchers: [String: LoopbackCatcher] = [:]

    init(profile: String) {
        vaultItem = profile.isEmpty ? "localAccounts" : "localAccounts-\(profile)"
        history = LocalHistory(profile: profile)
        if let stored = Keychain.read(vaultItem), let data = stored.data(using: .utf8),
           let decoded = try? JSONDecoder().decode(Vault.self, from: data) {
            vault = decoded
        } else {
            vault = Vault()
        }
    }

    // MARK: Readings

    func state(since: Date?, points: Int) async throws -> StateResponse {
        let interval = (lastMove.map { Date().timeIntervalSince($0) < 15 * 60 } ?? false) ? Self.active : Self.idle
        if lastRound.map({ Date().timeIntervalSince($0) >= interval }) ?? true {
            await poll()
        } else if let round {
            await round.value
        }

        let from = since ?? Date().addingTimeInterval(-35 * 86_400)
        let stored = history.state(since: from, points: points)
        let snapshot = Snapshot(fetchedAt: WireDate.format(fetchedAt ?? Date()), combinedPercent: Self.combined(current),
                                combinedProviders: current.filter { $0.isOK && !Forecast.longWindows($0).isEmpty }.count,
                                usages: current)
        return StateResponse(serverTime: Int64(Date().timeIntervalSince1970 * 1000), snapshot: snapshot,
                             combined: stored.combined, series: stored.series)
    }

    /// Asks every provider now, at most twice a minute.
    func refresh() async {
        if let lastForced, Date().timeIntervalSince(lastForced) < Self.forcedGap { return }
        lastForced = Date()
        await poll()
    }

    private func poll() async {
        if let round { return await round.value }
        let task = Task { await self.runRound() }
        round = task
        await task.value
        round = nil
    }

    private func runRound() async {
        let now = Date()
        lastRound = now
        let previous = Dictionary(current.map { ($0.account, $0) }, uniquingKeysWith: { a, _ in a })

        // Tokens due to expire are renewed first, one account at a time; then
        // every account is asked at once.
        var asks: [(provider: String, credential: (key: String, value: LocalCredential))] = []
        for account in sortedAccounts() {
            asks.append((account.provider, await ensureFresh(account.key)))
        }
        let usages = await withTaskGroup(of: ProviderUsage.self, returning: [ProviderUsage].self) { group in
            for ask in asks {
                group.addTask { await Self.fetch(ask.provider, ask.credential) }
            }
            var collected: [ProviderUsage] = []
            for await usage in group { collected.append(usage) }
            return collected
        }

        // A refused token is renewed and the account asked once more.
        var settled: [ProviderUsage] = []
        for usage in usages {
            var reading = usage
            if usage.message == "Token rejected", let key = usage.accountHint, await renewRejected(key) {
                reading = await Self.fetch(usage.providerKey, credential(key))
            }
            settled.append(reading)
        }

        current = sortedAccounts().compactMap { account in
            guard let usage = settled.first(where: { $0.accountHint == account.key }) else { return nil }
            let labelled = Self.label(usage, account: account)
            return Self.rollOver(Self.keepLastGood(labelled, previous[account.key]), now: now)
        }
        if current.contains(where: { usage in
            previous[usage.account].map { Self.moved($0, usage) } ?? false
        }) {
            lastMove = now
        }
        fetchedAt = now
        history.record(current, combined: Self.combined(current), at: now)
    }

    private static func fetch(_ provider: String, _ credential: (key: String, value: LocalCredential)) async -> ProviderUsage {
        let fetched = await LocalProviders.fetch(provider, credential.value)
        // The account it belongs to rides along until the round labels it.
        let usage = fetched.usage
        return ProviderUsage(providerKey: usage.providerKey, account: credential.key, state: usage.state,
                             message: fetched.rejected ? "Token rejected" : usage.message, plan: usage.plan,
                             fetchedAt: usage.fetchedAt, transient: usage.transient, windows: usage.windows)
    }

    private static func label(_ usage: ProviderUsage, account: Account) -> ProviderUsage {
        ProviderUsage(providerKey: account.provider, account: account.key, accountName: account.name, state: usage.state,
                      message: usage.message, plan: usage.plan, fetchedAt: usage.fetchedAt, stale: usage.stale,
                      transient: usage.transient, windows: usage.windows)
    }

    /// A rate limit or a dropped connection says nothing about the quota, so the
    /// last reading that got through stands in, marked stale, with today's name.
    private static func keepLastGood(_ usage: ProviderUsage, _ previous: ProviderUsage?) -> ProviderUsage {
        guard usage.state == "ERROR", usage.transient, let previous, previous.isOK, !previous.windows.isEmpty else { return usage }
        return ProviderUsage(providerKey: previous.providerKey, account: previous.account, accountName: usage.accountName,
                             state: "OK", message: usage.message, plan: previous.plan, fetchedAt: previous.fetchedAt,
                             stale: true, transient: true, windows: previous.windows)
    }

    /// Meta goes quiet once a window has reset, so a stale Muse window whose
    /// reset has passed is known to be full again.
    private static func rollOver(_ usage: ProviderUsage, now: Date) -> ProviderUsage {
        guard usage.providerKey == "muse", usage.stale else { return usage }
        let windows = usage.windows.map { window -> UsageWindow in
            guard let reset = window.resetDate, reset <= now else { return window }
            return UsageWindow(label: window.label, remainingPercent: 100, resetAt: nil,
                               countsTowardCombined: window.countsTowardCombined)
        }
        return ProviderUsage(providerKey: usage.providerKey, account: usage.account, accountName: usage.accountName,
                             state: usage.state, message: usage.message, plan: usage.plan, fetchedAt: usage.fetchedAt,
                             stale: usage.stale, transient: usage.transient, windows: windows)
    }

    private static func moved(_ before: ProviderUsage, _ after: ProviderUsage) -> Bool {
        after.windows.contains { window in
            guard let now = window.remainingPercent,
                  let then = before.windows.first(where: { $0.label == window.label })?.remainingPercent else { return false }
            return abs(now - then) >= 0.05
        }
    }

    /// Each subscription's long windows averaged, then the subscriptions averaged:
    /// what the server's combined figure is.
    private static func combined(_ usages: [ProviderUsage]) -> Double? {
        let levels = usages.filter(\.isOK).compactMap { usage -> Double? in
            let values = Forecast.longWindows(usage).compactMap(\.remainingPercent)
            return values.isEmpty ? nil : values.reduce(0, +) / Double(values.count)
        }
        return levels.isEmpty ? nil : levels.reduce(0, +) / Double(levels.count)
    }

    // MARK: Tokens

    private func credential(_ key: String) -> (key: String, value: LocalCredential) {
        let account = vault.accounts.first { $0.key == key }
        return (key, LocalCredential(token: account?.token, accountId: account?.accountId,
                                     kimiChina: account?.kimiChina ?? false,
                                     mistralBudget: account?.mistralBudget, mistralSpent: account?.mistralSpent))
    }

    /// Renews a token due to expire within five minutes, before it is used.
    private func ensureFresh(_ key: String) async -> (key: String, value: LocalCredential) {
        if let account = vault.accounts.first(where: { $0.key == key }), let client = OAuthClient.of(account.provider),
           let refresh = account.refresh,
           account.token == nil || (account.expiresAt.map { $0 - Date().timeIntervalSince1970 < 300 } ?? false),
           let session = try? await LocalAuth.refresh(client, refreshToken: refresh) {
            store(session, for: key)
        }
        return credential(key)
    }

    private func renewRejected(_ key: String) async -> Bool {
        guard let account = vault.accounts.first(where: { $0.key == key }), let client = OAuthClient.of(account.provider),
              let refresh = account.refresh else { return false }
        if let last = renewed[key], Date().timeIntervalSince(last) < Self.rejectedCooldown { return false }
        renewed[key] = Date()
        guard let session = try? await LocalAuth.refresh(client, refreshToken: refresh) else { return false }
        store(session, for: key)
        return true
    }

    private func store(_ session: Session, for key: String) {
        update(key) { account in
            account.token = session.access
            account.refresh = session.refresh ?? account.refresh
            account.expiresAt = session.expiresAt?.timeIntervalSince1970
            if let id = session.accountId { account.accountId = id }
        }
    }

    // MARK: Accounts

    func accounts() async throws -> [ServerAccount] {
        sortedAccounts().map(describe)
    }

    func addAccount(provider: String) async throws -> ServerAccount {
        guard Self.order.contains(provider) else { throw OAuthError.failed("Unknown provider") }
        let key: String
        if !vault.accounts.contains(where: { $0.key == provider }) {
            key = provider
        } else {
            key = "\(provider)-\(vault.next)"
            vault.next += 1
        }
        vault.accounts.append(Account(key: key, provider: provider))
        save()
        return describe(vault.accounts.last!)
    }

    func rename(_ key: String, to name: String) async throws {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        update(key) { $0.name = trimmed.isEmpty ? nil : String(trimmed.prefix(40)) }
        relabel()
    }

    /// The account leaves the list with its sign-in or key; its history stays.
    func remove(_ key: String) async throws {
        catchers.removeValue(forKey: key)?.cancel()
        vault.accounts.removeAll { $0.key == key }
        save()
        current.removeAll { $0.account == key }
    }

    func signOut(_ key: String) async throws {
        update(key) { account in
            account.token = nil
            account.refresh = nil
            account.expiresAt = nil
            account.accountId = nil
        }
        lastRound = nil
    }

    func setKey(_ key: String, value: String, kimiChina: Bool?, mistralBudget: Double?, mistralSpent: Double?) async throws {
        update(key) { account in
            account.token = value.trimmingCharacters(in: .whitespacesAndNewlines)
            account.refresh = nil
            account.expiresAt = nil
            if let kimiChina { account.kimiChina = kimiChina }
            if let mistralBudget { account.mistralBudget = mistralBudget }
            if let mistralSpent { account.mistralSpent = mistralSpent }
        }
        lastRound = nil
    }

    /// A browser sign-in caught on this Mac ("browser"), or Muse's device code
    /// ("device"). Either finishes by itself; `signInStatus` says when.
    func startSignIn(_ key: String) async throws -> SignInStep {
        guard let account = vault.accounts.first(where: { $0.key == key }) else { throw OAuthError.failed("No such account") }
        catchers.removeValue(forKey: key)?.cancel()

        if account.provider == "muse" {
            let started = try await LocalAuth.Muse.start()
            signIns[key] = "pending"
            Task { await self.finishDevice(key, started) }
            return SignInStep(kind: "device", url: started.url, userCode: started.userCode,
                              instructions: "Open the page and enter the code. This finishes by itself once you approve.")
        }

        guard let client = OAuthClient.of(account.provider) else {
            throw OAuthError.failed("\(Provider(key: account.provider).displayName) takes a key, not a sign-in.")
        }
        let catcher = try LoopbackCatcher(port: client.fixedPort, path: client.callbackPath)
        let port = try await catcher.start()
        let redirect = "http://localhost:\(port)\(client.callbackPath)"
        let verifier = LocalAuth.verifier()
        let state = LocalAuth.random(24)
        let url = LocalAuth.authorizeURL(client, redirect: redirect, challenge: LocalAuth.challenge(verifier), state: state)
        catchers[key] = catcher
        signIns[key] = "pending"
        Task { await self.finishBrowser(key, client, catcher, verifier: verifier, redirect: redirect, state: state) }

        let who = account.provider == "antigravity" ? "the Google account that has the plan" : "the account to follow"
        return SignInStep(kind: "browser", url: url, userCode: nil,
                          instructions: "Sign in with \(who) in the browser. This finishes by itself once you approve.")
    }

    func completeSignIn(_ key: String, input: String) async throws {
        throw OAuthError.failed("This sign-in finishes by itself.")
    }

    func signInStatus(_ key: String) async throws -> String {
        signIns[key] ?? "none"
    }

    private func finishBrowser(_ key: String, _ client: OAuthClient, _ catcher: LoopbackCatcher,
                               verifier: String, redirect: String, state: String) async {
        do {
            let result = try await catcher.wait(timeout: 300)
            if let error = result.error { throw OAuthError.failed("Sign-in failed: \(error)") }
            guard let code = result.code else { throw OAuthError.failed("Sign-in returned no code") }
            guard result.state == state else { throw OAuthError.failed("That sign-in belongs to another attempt; start again.") }
            let session = try await LocalAuth.exchange(client, code: code, verifier: verifier, redirect: redirect, state: state)
            guard catchers[key] === catcher else { return }
            store(session, for: key)
            renewed[key] = nil
            signIns[key] = "done"
            lastRound = nil
        } catch {
            guard catchers[key] === catcher else { return }
            signIns[key] = "failed: \((error as? LocalizedError)?.errorDescription ?? error.localizedDescription)"
        }
        catchers[key] = nil
        await MainActor.run { NSApp.activate() }
    }

    private func finishDevice(_ key: String, _ started: LocalAuth.Muse.Started) async {
        do {
            let token = try await LocalAuth.Muse.awaitToken(started)
            update(key) { account in
                account.token = token
                account.refresh = nil
                account.expiresAt = nil
            }
            signIns[key] = "done"
            lastRound = nil
        } catch {
            signIns[key] = "failed: \((error as? LocalizedError)?.errorDescription ?? error.localizedDescription)"
        }
    }

    // MARK: Plumbing

    /// Forgets this profile's accounts and history; for the self-test only.
    func eraseForTest() {
        Keychain.write("", account: vaultItem)
        history.erase()
        vault = Vault()
    }

    /// Grouped by provider in the catalogue's order, a provider's first account first.
    private func sortedAccounts() -> [Account] {
        vault.accounts.sorted { a, b in
            let (pa, pb) = (Self.order.firstIndex(of: a.provider) ?? 99, Self.order.firstIndex(of: b.provider) ?? 99)
            if pa != pb { return pa < pb }
            if a.slot.isEmpty != b.slot.isEmpty { return a.slot.isEmpty }
            return (Int(a.slot) ?? 0) < (Int(b.slot) ?? 0)
        }
    }

    private func describe(_ account: Account) -> ServerAccount {
        let base = Provider(key: account.provider).displayName
        return ServerAccount(key: account.key, provider: account.provider, name: account.name,
                             displayName: account.name.map { "\(base) · \($0)" } ?? base, removable: true,
                             configured: account.token != nil, renews: account.refresh != nil)
    }

    private func update(_ key: String, _ change: (inout Account) -> Void) {
        guard let index = vault.accounts.firstIndex(where: { $0.key == key }) else { return }
        change(&vault.accounts[index])
        save()
    }

    /// A renamed account's reading takes the new name at once.
    private func relabel() {
        current = current.compactMap { usage in
            guard let account = vault.accounts.first(where: { $0.key == usage.account }) else { return nil }
            return Self.label(usage, account: account)
        }
    }

    private func save() {
        guard let data = try? JSONEncoder().encode(vault) else { return }
        Keychain.write(String(decoding: data, as: UTF8.self), account: vaultItem)
    }
}

private extension ProviderUsage {
    /// The account a reading was taken for, before the round labels it.
    var accountHint: String? { account }
}

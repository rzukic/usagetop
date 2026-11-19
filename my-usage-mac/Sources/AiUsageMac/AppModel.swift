import Foundation
import Observation

/// What the panel draws: the last reading and its history, why it might be
/// missing, and whether a fetch is in flight.
@MainActor
@Observable
final class AppModel {
    let settings = Settings()
    let updater = Updater()

    var snapshot: Snapshot?
    var series: [String: [SeriesPoint]] = [:]
    var combinedHistory: [SeriesPoint] = []
    /// The last week in full, for burn rates. The long series are thinned for
    /// drawing, and a thinned series loses the small drains a rate is made of.
    var recentSeries: [String: [SeriesPoint]] = [:]
    var problem: String?
    var isLoading = false
    var lastAttempt: Date?

    /// Sample readings instead of the server's: for a first look before a
    /// server is set, and for screenshots (`--demo`).
    private(set) var isDemo = false

    /// Ticks once a minute so "resets in" and "updated ago" stay true while the
    /// panel is open without a fetch.
    var now = Date()

    /// The page the panel shows; kept here so it survives the panel closing.
    var pane: Pane = .overview

    private var loop: Task<Void, Never>?
    /// Bumped whenever what a fetch is for changes (sample data on or off, a
    /// new server), so a reply that lands afterwards is thrown away.
    private var epoch = 0
    /// A forced refresh asked for while another fetch was in flight.
    private var forceQueued = false
    private var clock: Task<Void, Never>?

    /// The server's accounts, for Settings. Made in `init`, once `self` exists.
    @ObservationIgnored private var madeAccounts: ServerAccounts?
    var accounts: ServerAccounts { madeAccounts! }

    init() {
        madeAccounts = ServerAccounts(app: self)
        if CommandLine.arguments.contains("--demo") || ProcessInfo.processInfo.environment["AIUSAGE_DEMO"] != nil {
            loadDemo()
        }
    }

    /// The figure in the menu bar: the combined level, or the tightest window
    /// when the server did not send one.
    var combinedPercent: Double? {
        if let combined = snapshot?.combinedPercent { return combined }
        return snapshot?.usages.compactMap { $0.tightest?.remainingPercent }.min()
    }

    /// The limits closest to running out, across every working subscription:
    /// least left first, the sooner reset breaking a tie. Every window counts,
    /// so one subscription can take more than one place. A window already
    /// empty is left out: nothing can be done about it but wait, and it would
    /// only push out one that can still be saved.
    func mostAtRisk(_ count: Int) -> [(usage: ProviderUsage, window: UsageWindow)] {
        guard count > 0 else { return [] }
        let all = (snapshot?.usages ?? []).filter(\.isOK).flatMap { usage in
            usage.windows
                .filter { window in window.remainingPercent.map { Forecast.pace(window, now: now) != .empty && $0 >= 0.5 } ?? false }
                .map { (usage: usage, window: $0) }
        }
        return Array(all.sorted { lhs, rhs in
            let l = lhs.window.remainingPercent ?? 100, r = rhs.window.remainingPercent ?? 100
            if l != r { return l < r }
            return (lhs.window.resetDate ?? .distantFuture) < (rhs.window.resetDate ?? .distantFuture)
        }.prefix(count))
    }

    /// The figure the menu bar's ring shows.
    var menuBarPercent: Double? { combinedPercent }

    /// Subscriptions in the order they are listed: working ones first, the
    /// one about to run out at the top.
    var ordered: [ProviderUsage] {
        guard settings.overviewByRisk else { return railOrder }
        return (snapshot?.usages ?? []).sorted { lhs, rhs in
            if lhs.isOK != rhs.isOK { return lhs.isOK }
            let left = lhs.tightest?.remainingPercent ?? 101
            let right = rhs.tightest?.remainingPercent ?? 101
            if left == right { return lhs.displayName < rhs.displayName }
            return left < right
        }
    }

    /// Subscriptions in a stable order for the rail, so icons do not jump
    /// about as levels change.
    /// The order is the one set in Settings; accounts it does not mention
    /// follow, by provider.
    var railOrder: [ProviderUsage] {
        let rank = ["claude", "chatgpt", "mistral", "kimi", "muse", "antigravity"]
        let chosen = settings.accountOrder
        return (snapshot?.usages ?? []).sorted { lhs, rhs in
            let lc = chosen.firstIndex(of: lhs.account) ?? Int.max
            let rc = chosen.firstIndex(of: rhs.account) ?? Int.max
            if lc != rc { return lc < rc }
            let l = rank.firstIndex(of: lhs.providerKey) ?? rank.count
            let r = rank.firstIndex(of: rhs.providerKey) ?? rank.count
            return l == r ? lhs.account < rhs.account : l < r
        }
    }

    /// While ⌘ is held with the panel open, the sidebar shows its shortcuts.
    var commandHeld = false

    var statusLine: String {
        if isDemo { return "Sample data" }
        return Format.status(snapshot: snapshot, problem: problem)
    }

    func points(for usage: ProviderUsage, _ window: UsageWindow) -> [SeriesPoint] {
        series[usage.seriesKey(window)] ?? []
    }

    func outlook(for usage: ProviderUsage, _ window: UsageWindow) -> Forecast.Outlook? {
        Forecast.outlook(window, points: burnPoints(usage.seriesKey(window)), now: now)
    }

    var combinedOutlook: Forecast.Outlook? {
        guard let snapshot else { return nil }
        var merged: [String: [SeriesPoint]] = [:]
        for key in series.keys { merged[key] = burnPoints(key) }
        return Forecast.combined(snapshot, series: merged, now: now)
    }

    /// The readings to take a burn rate from: the thinned history up to the
    /// last week, then the week in full (as the server page does).
    private func burnPoints(_ key: String) -> [SeriesPoint] {
        let long = series[key] ?? []
        guard let short = recentSeries[key], let first = short.first else { return long }
        return long.filter { $0.date < first.date } + short
    }

    // MARK: Fetching

    func start() {
        if clock == nil {
            clock = Task { [weak self] in
                while !Task.isCancelled {
                    try? await Task.sleep(for: .seconds(30))
                    self?.now = Date()
                }
            }
        }
        guard loop == nil else { return }
        loop = Task { [weak self] in
            while !Task.isCancelled {
                await self?.refresh()
                try? await Task.sleep(for: .seconds(60))
            }
        }
    }

    func stop() {
        loop?.cancel()
        loop = nil
        clock?.cancel()
        clock = nil
    }

    func refresh(force: Bool = false) async {
        now = Date()
        if isDemo {
            if force { loadDemo() }
            return
        }
        guard let baseURL = settings.baseURL else {
            problem = nil
            snapshot = nil
            series = [:]
            recentSeries = [:]
            combinedHistory = []
            return
        }
        guard !isLoading else {
            // A refresh asked for by hand is never dropped: it runs once this one lands.
            if force { forceQueued = true }
            return
        }

        isLoading = true
        lastAttempt = Date()
        let started = epoch
        defer {
            isLoading = false
            // The server changed while this was in flight, or a refresh was asked
            // for meanwhile: fetch again.
            if !isDemo, started != epoch || forceQueued {
                let again = forceQueued
                forceQueued = false
                Task { await refresh(force: again) }
            }
        }

        let client = ServerClient(baseURL: baseURL, token: settings.token)
        if force { await client.refresh() }

        do {
            async let long = client.state()
            async let short = client.state(since: Date().addingTimeInterval(-Forecast.usualHours * Forecast.hour), points: 5000)
            let state = try await long
            let recent = (try? await short)?.series
            guard started == epoch, !isDemo else { return }
            apply(state)
            recentSeries = (recent ?? [:]).mapValues(SeriesPoint.decode)
            problem = state.snapshot == nil ? "No reading yet" : nil
        } catch {
            guard started == epoch, !isDemo else { return }
            problem = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        }
        now = Date()
    }

    private func apply(_ state: StateResponse) {
        snapshot = state.snapshot
        series = (state.series ?? [:]).mapValues(SeriesPoint.decode)
        combinedHistory = SeriesPoint.decode(state.combined)
    }

    // MARK: Sample data

    func showDemo() {
        loadDemo()
    }

    /// The server's address or token changed: whatever is in flight is for the old one.
    func serverChanged() {
        epoch += 1
        Task { await refresh() }
        Task { await accounts.load() }
    }

    func leaveDemo() {
        epoch += 1
        isDemo = false
        snapshot = nil
        series = [:]
        recentSeries = [:]
        combinedHistory = []
        Task { await refresh() }
    }

    private func loadDemo() {
        epoch += 1
        isDemo = true
        problem = nil
        let demo = SampleData.make(now: Date())
        snapshot = demo.snapshot
        series = demo.series
        recentSeries = [:]
        combinedHistory = demo.combined
        now = Date()
    }
}

import Foundation

/// Believable readings and history, for a first look before a server is set and
/// for screenshots (`usagetop.app --demo`). Nothing here reaches the server.
enum SampleData {
    struct Result {
        let snapshot: Snapshot
        let series: [String: [SeriesPoint]]
        let combined: [SeriesPoint]
    }

    private struct Spec {
        let label: String
        let level: Double
        /// How far off the reset is; nil for a window with none.
        let resetIn: TimeInterval?
        let period: TimeInterval?
        var counts = true
        var busyNow = false
    }

    static func make(now: Date) -> Result {
        let h = Forecast.hour, d = Forecast.day
        let accounts: [(key: String, account: String, name: String?, plan: String?, state: String, message: String?, specs: [Spec])] = [
            ("claude", "claude", nil, "Max 20x", "OK", nil, [
                Spec(label: "5h", level: 38, resetIn: 2 * h + 14 * 60, period: 5 * h, busyNow: true),
                Spec(label: "7d", level: 31, resetIn: 2 * d + 5 * h, period: 7 * d),
                Spec(label: "Fable 7d", level: 57, resetIn: 2 * d + 5 * h, period: 7 * d, counts: false),
            ]),
            ("chatgpt", "chatgpt", nil, "Pro", "OK", nil, [
                Spec(label: "5h", level: 91, resetIn: 3 * h + 40 * 60, period: 5 * h),
                Spec(label: "7d", level: 72, resetIn: 4 * d + 11 * h, period: 7 * d),
            ]),
            ("kimi", "kimi", nil, "Allegretto", "OK", nil, [
                Spec(label: "Weekly", level: 11, resetIn: 1 * d + 7 * h, period: 7 * d),
            ]),
            ("mistral", "mistral", nil, "Pro", "OK", nil, [
                Spec(label: "Vibe", level: 76, resetIn: 12 * d, period: nil),
            ]),
            ("antigravity", "antigravity", nil, "Google AI Pro", "OK", nil, [
                Spec(label: "7d", level: 64, resetIn: 5 * d + 3 * h, period: 7 * d),
                Spec(label: "5h", level: 82, resetIn: 1 * h + 50 * 60, period: 5 * h),
                Spec(label: "Claude & GPT 7d", level: 100, resetIn: 5 * d + 4 * h, period: 7 * d, counts: false),
                Spec(label: "Gemini 4 Argon 7d", level: 88, resetIn: 5 * d + 4 * h, period: 7 * d, counts: false),
            ]),
            ("claude", "claude-2", "Work", "Team", "ERROR", "Signed out — sign in again on the server", []),
        ]

        var generator = SeededGenerator(seed: 7)
        var usages: [ProviderUsage] = []
        var series: [String: [SeriesPoint]] = [:]
        let fetched = now.addingTimeInterval(-90)

        for account in accounts {
            var windows: [UsageWindow] = []
            for spec in account.specs {
                let reset = spec.resetIn.map { now.addingTimeInterval($0) }
                windows.append(UsageWindow(label: spec.label, remainingPercent: spec.level, resetAt: reset,
                                           countsTowardCombined: spec.counts))
                series["\(account.account)|\(spec.label)"] = history(spec, now: now, reset: reset, using: &generator)
            }
            usages.append(ProviderUsage(
                providerKey: account.key, account: account.account, accountName: account.name,
                state: account.state, message: account.message, plan: account.plan,
                fetchedAt: WireDate.format(fetched), stale: account.state != "OK", windows: windows
            ))
        }

        var snapshot = Snapshot(fetchedAt: WireDate.format(fetched), combinedPercent: nil, combinedProviders: nil, usages: usages)
        let longKeys = usages.filter(\.isOK).map { usage in
            Forecast.longWindows(usage).map { usage.seriesKey($0) }
        }
        let combined = stride(from: now.addingTimeInterval(-35 * d), through: now, by: h).map { t in
            let parts = longKeys.map { keys in average(keys.map { level(series[$0] ?? [], at: t) }) }
            return SeriesPoint(date: t, value: average(parts))
        }
        snapshot = Snapshot(fetchedAt: snapshot.fetchedAt, combinedPercent: combined.last?.value,
                            combinedProviders: longKeys.count, usages: usages)
        return Result(snapshot: snapshot, series: series, combined: combined)
    }

    /// The sample as the server would send it from `/api/state`, for the other
    /// apps' screenshots (`--export-sample <file>`): they draw the same accounts
    /// from it, shifting its times forward to their own now.
    static func wire(now: Date) -> Data {
        let sample = make(now: now)
        func ms(_ date: Date) -> Double { (date.timeIntervalSince1970 * 1000).rounded() }
        func points(_ list: [SeriesPoint]) -> [[Double]] { list.map { [ms($0.date), $0.value] } }
        let usages: [[String: Any]] = sample.snapshot.usages.map { usage in
            var entry: [String: Any] = [
                "id": usage.providerKey, "account": usage.account, "state": usage.state,
                "fetchedAt": usage.fetchedAt ?? WireDate.format(now), "stale": usage.stale, "transient": false,
                "windows": usage.windows.map { window -> [String: Any] in
                    var w: [String: Any] = ["label": window.label, "countsTowardCombined": window.countsTowardCombined]
                    if let percent = window.remainingPercent { w["remainingPercent"] = percent }
                    if let reset = window.resetAt { w["resetAt"] = reset }
                    if let detail = window.detail { w["detail"] = detail }
                    return w
                },
            ]
            if let name = usage.accountName { entry["accountName"] = name }
            if let plan = usage.plan { entry["plan"] = plan }
            if let message = usage.message { entry["message"] = message }
            return entry
        }
        var snapshot: [String: Any] = ["fetchedAt": sample.snapshot.fetchedAt ?? WireDate.format(now), "usages": usages]
        if let combined = sample.snapshot.combinedPercent { snapshot["combinedPercent"] = combined }
        if let providers = sample.snapshot.combinedProviders { snapshot["combinedProviders"] = providers }
        let state: [String: Any] = [
            "serverTime": ms(now), "snapshot": snapshot,
            "combined": points(sample.combined), "series": sample.series.mapValues(points),
        ]
        return (try? JSONSerialization.data(withJSONObject: state, options: [.sortedKeys])) ?? Data()
    }

    /// Walks each cycle from its refill to its end, spending more in working
    /// hours than at night, so the line is a believable sawtooth.
    private static func history(_ spec: Spec, now: Date, reset: Date?, using generator: inout SeededGenerator) -> [SeriesPoint] {
        let step: TimeInterval = (spec.period ?? Forecast.day) <= 6 * Forecast.hour ? 10 * 60 : 30 * 60
        let start = now.addingTimeInterval(-35 * Forecast.day)
        var cycles: [(from: Date, to: Date, endLevel: Double)] = []

        if let reset, let period = spec.period {
            var end = reset
            var from = end.addingTimeInterval(-period)
            cycles.append((from, now, spec.level))
            while from > start {
                end = from
                from = end.addingTimeInterval(-period)
                cycles.append((from, end, Double.random(in: 4...55, using: &generator)))
            }
        } else {
            cycles.append((start, now, spec.level))
        }

        var points: [SeriesPoint] = []
        for cycle in cycles.reversed() {
            var times: [Date] = []
            var weights: [Double] = []
            var t = max(cycle.from, start)
            while t <= cycle.to {
                times.append(t)
                weights.append(weight(at: t, now: now, busyNow: spec.busyNow) * Double.random(in: 0.4...1.6, using: &generator))
                t = t.addingTimeInterval(step)
            }
            let total = max(weights.reduce(0, +), 0.0001)
            let drop = 100 - cycle.endLevel
            var spent = 0.0
            for (index, time) in times.enumerated() {
                spent += weights[index]
                let value = index == times.count - 1 && cycle.to == now ? cycle.endLevel : 100 - drop * spent / total
                if let last = points.last, abs(last.value - value) < 0.25, index != times.count - 1 { continue }
                points.append(SeriesPoint(date: time, value: (value * 100).rounded() / 100))
            }
        }
        return points
    }

    private static func weight(at t: Date, now: Date, busyNow: Bool) -> Double {
        let hour = Calendar.current.component(.hour, from: t)
        let weekday = Calendar.current.component(.weekday, from: t)
        var w: Double
        switch hour {
        case 9..<13, 14..<19: w = 1
        case 19..<23: w = 0.55
        case 7..<9, 13..<14: w = 0.35
        default: w = 0.02
        }
        if weekday == 1 || weekday == 7 { w *= 0.4 }
        if busyNow, now.timeIntervalSince(t) < 2 * Forecast.hour { w = max(w, 1) * 1.8 }
        return w
    }

    private static func level(_ points: [SeriesPoint], at t: Date) -> Double {
        points.last { $0.date <= t }?.value ?? points.first?.value ?? 100
    }

    private static func average(_ values: [Double]) -> Double {
        values.isEmpty ? 0 : values.reduce(0, +) / Double(values.count)
    }
}

/// A small, repeatable random source, so the sample looks the same every time.
struct SeededGenerator: RandomNumberGenerator {
    private var state: UInt64
    init(seed: UInt64) { state = seed &* 0x9E3779B97F4A7C15 }
    mutating func next() -> UInt64 {
        state &+= 0x9E3779B97F4A7C15
        var z = state
        z = (z ^ (z >> 30)) &* 0xBF58476D1CE4E5B9
        z = (z ^ (z >> 27)) &* 0x94D049BB133111EB
        return z ^ (z >> 31)
    }
}

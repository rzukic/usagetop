import Foundation

/// Pace, burn rate and forecast, ported from the server page
/// (`my-usage-server/src/AiUsage.Server/Web/app.js`) so the Mac app reads a
/// window the same way: the same even-pace tick, the same two burn rates, the
/// same curve from one to the other, the same wording.
enum Forecast {
    static let hour: TimeInterval = 3600
    static let day: TimeInterval = 86_400

    /// How far back the recent rate looks.
    static let burnHours: Double = 3
    /// How far back the usual rate looks: long enough to take in the nights.
    static let usualHours: Double = 7 * 24
    /// Below this, in points an hour, a window counts as not draining.
    static let idle = 0.05
    /// A jump this large is a reset, not a reading drifting.
    static let refillJump = 1.5
    /// How far ahead a window is looked at for when it runs dry.
    static let horizon: TimeInterval = 35 * day

    // MARK: Pace

    /// How long one cycle of the window lasts: from its label ("5h", "Fable 7d",
    /// "Weekly"), or for "Monthly" the calendar month before its reset.
    /// Mistral's two budgets, "Vibe" and "API", name no duration but run on its
    /// monthly billing cycle. Nil when nothing says.
    static func period(of window: UsageWindow) -> TimeInterval? {
        let text = window.label.trimmingCharacters(in: .whitespaces).lowercased()
        if text == "weekly" { return 7 * day }
        if ["monthly", "vibe", "api"].contains(text) {
            guard let end = window.resetDate,
                  let start = Calendar.current.date(byAdding: .month, value: -1, to: end) else { return 30 * day }
            return end.timeIntervalSince(start)
        }
        return duration(in: text)
    }

    /// "5h" → 5 hours; the number and unit at the end of a label.
    static func duration(in label: String) -> TimeInterval? {
        guard let match = label.lowercased().firstMatch(of: #/(?:^|\s)(\d+)\s*([mhd])$/#),
              let amount = Double(match.1) else { return nil }
        switch match.2 {
        case "m": return amount * 60
        case "h": return amount * hour
        default: return amount * day
        }
    }

    /// "5h" and other windows shorter than a day.
    static func isShort(_ window: UsageWindow) -> Bool {
        guard let match = window.label.trimmingCharacters(in: .whitespaces).lowercased()
            .wholeMatch(of: #/(\d+)\s*([mhd])/#), let amount = Double(match.1) else { return false }
        let hours = match.2 == "m" ? amount / 60 : match.2 == "h" ? amount : amount * 24
        return hours < 24
    }

    /// What would be left now had the window been spent evenly since it
    /// refilled: the share of the cycle still to run.
    static func evenPercent(_ window: UsageWindow, now: Date = .now) -> Double? {
        guard let length = period(of: window), let reset = window.resetDate else { return nil }
        let left = reset.timeIntervalSince(now)
        guard left > 0, left <= length else { return nil }
        return left / length * 100
    }

    enum Pace {
        /// Nothing left: past pace, it just waits for the refill.
        case empty
        case ahead, on, under

        var text: String {
            switch self {
            case .empty: "Empty"
            case .ahead: "Ahead of pace"
            case .on: "On pace"
            case .under: "Under pace"
            }
        }

        var help: String {
            switch self {
            case .empty: "Nothing left until it refills."
            case .ahead: "Used faster than an even spread: at this rate it runs out before it resets."
            case .on: "About what an even spread over the cycle would leave."
            case .under: "Used slower than an even spread: room to spare before it resets."
            }
        }

        var symbol: String {
            switch self {
            case .empty: "circle.slash"
            case .ahead: "flame.fill"
            case .on: "equal.circle.fill"
            case .under: "leaf.fill"
            }
        }
    }

    /// Ahead of pace runs out before the reset; under pace has room to spare.
    /// Five points either way is on pace. A window with nothing left is empty,
    /// whatever its pace was.
    static func pace(_ window: UsageWindow, now: Date = .now) -> Pace? {
        if let level = window.remainingPercent, level < 0.5 { return .empty }
        guard let even = evenPercent(window, now: now), let level = window.remainingPercent else { return nil }
        let gap = level - even
        return gap < -5 ? .ahead : gap > 5 ? .under : .on
    }

    // MARK: Burn rate

    /// Points of remaining-percent drained per hour over the last `hours`,
    /// refills left out. Nil with under half an hour (or half the span) to go on.
    static func rate(_ points: [SeriesPoint], hours: Double, now: Date = .now) -> Double? {
        guard let first = points.first else { return nil }
        let cutoff = now.addingTimeInterval(-hours * hour)
        let start = max(first.date, cutoff)
        let elapsed = now.timeIntervalSince(start) / hour
        guard elapsed >= min(0.5, hours / 2) else { return nil }

        var drained = 0.0
        for i in points.indices.dropFirst() {
            let point = points[i]
            if point.date <= cutoff || point.date > now { continue }
            let step = points[i - 1].value - point.value
            if -step >= refillJump { continue }
            drained += step
        }
        return max(drained / elapsed, 0)
    }

    /// One window and how fast it is being spent. The forecast leaves now at
    /// the recent rate and eases into the usual one:
    ///
    ///     rate(t) = usual + (recent - usual) * e^(-t / ease)
    struct Track {
        var name = ""
        let recent: Double?
        let usual: Double?
        let level: Double
        let at: Date
        let reset: Date?
        let period: TimeInterval?
        let ease: Double

        var measured: Bool { recent != nil || usual != nil }
        var draining: Bool { (recent ?? 0) > Forecast.idle || (usual ?? 0) > Forecast.idle }

        /// Points drained from now until `hours` later.
        func drained(_ hours: Double) -> Double {
            guard hours > 0 else { return 0 }
            let r = recent ?? usual ?? 0
            let u = usual ?? recent ?? 0
            return u * hours + (r - u) * ease * (1 - exp(-hours / ease))
        }

        /// The last refill at or before t, or nil if none has come yet.
        func cycleStart(_ t: Date) -> Date? {
            guard let reset, t >= reset else { return nil }
            guard let period else { return reset }
            let cycles = floor(t.timeIntervalSince(reset) / period)
            return reset.addingTimeInterval(cycles * period)
        }

        /// Where the window stands at t, refilling at every reset on the way.
        func level(at t: Date) -> Double {
            if t <= at { return level }
            let spent = drained(t.timeIntervalSince(at) / Forecast.hour)
            guard let start = cycleStart(t) else { return max(level - spent, 0) }
            return max(100 - (spent - drained(start.timeIntervalSince(at) / Forecast.hour)), 0)
        }

        /// When the window runs dry before its next refill.
        func emptyAt(until: Date) -> Date? {
            if level <= 0 { return at }
            guard draining else { return nil }
            let end = reset.map { min($0, until) } ?? until
            guard end > at, level - drained(end.timeIntervalSince(at) / Forecast.hour) <= 0 else { return nil }
            var low = 0.0, high = end.timeIntervalSince(at) / Forecast.hour
            for _ in 0..<48 {
                let middle = (low + high) / 2
                if level - drained(middle) > 0 { low = middle } else { high = middle }
            }
            return at.addingTimeInterval(high * Forecast.hour)
        }

        /// Every refill after `from`, up to `to`.
        func resets(from: Date, to: Date) -> [Date] {
            var list: [Date] = []
            var at = reset
            var n = 0
            while let moment = at, moment <= to, n < 1000 {
                if moment > from { list.append(moment) }
                at = period.map { moment.addingTimeInterval($0) }
                n += 1
            }
            return list
        }
    }

    static func track(_ window: UsageWindow, points: [SeriesPoint], now: Date = .now) -> Track? {
        guard let level = window.remainingPercent else { return nil }
        let reset = window.resetDate.flatMap { $0 > now ? $0 : nil }
        return Track(
            recent: rate(points, hours: burnHours, now: now),
            usual: rate(points, hours: usualHours, now: now),
            level: level,
            at: now,
            reset: reset,
            period: period(of: window),
            ease: burnHours
        )
    }

    /// The forecast line from `from` to `to`: evenly spaced samples, plus both
    /// sides of every refill so it steps straight up.
    static func sampled(_ tracks: [Track], from: Date, to: Date, value: (Date) -> Double) -> [SeriesPoint] {
        guard to > from else { return [] }
        var times = Set<Date>()
        let steps = 120
        for i in 0...steps {
            times.insert(from.addingTimeInterval(to.timeIntervalSince(from) * Double(i) / Double(steps)))
        }
        for track in tracks {
            for at in track.resets(from: from, to: to) {
                let before = at.addingTimeInterval(-1)
                if before > from { times.insert(before) }
                times.insert(at)
            }
        }
        return times.sorted().map { SeriesPoint(date: $0, value: value($0)) }
    }

    struct Outlook {
        let recent: Double?
        let usual: Double?
        let level: Double
        let emptyAt: Date?
        let reset: Date?
        /// The soonest refill among the windows behind a combined figure.
        var next: (name: String, reset: Date)?
        let steps: (Date, Date) -> [SeriesPoint]

        var draining: Bool { (recent ?? 0) > Forecast.idle || (usual ?? 0) > Forecast.idle }
    }

    /// The forecast for one window, or nil with no readings to go on.
    static func outlook(_ window: UsageWindow, points: [SeriesPoint], now: Date = .now) -> Outlook? {
        guard let track = track(window, points: points, now: now), track.measured else { return nil }
        return Outlook(
            recent: track.recent,
            usual: track.usual,
            level: track.level,
            emptyAt: track.emptyAt(until: now.addingTimeInterval(horizon)),
            reset: track.reset,
            steps: { from, to in sampled([track], from: from, to: to) { track.level(at: $0) } }
        )
    }

    // MARK: Combined

    /// The windows that make up a subscription's share of the combined figure,
    /// chosen as the server's CombinedUsage.LongWindows chooses them.
    static func longWindows(_ usage: ProviderUsage) -> [UsageWindow] {
        let counted = usage.windows.filter { $0.remainingPercent != nil && $0.countsTowardCombined }
        let long = counted.filter { !isShort($0) }
        let rated = long.isEmpty ? counted : long
        let resets = rated.compactMap(\.resetDate)
        guard let furthest = resets.max() else { return rated }
        return rated.filter { w in
            guard let at = w.resetDate else { return true }
            return furthest.timeIntervalSince(at) <= day
        }
    }

    /// The combined figure carried forward: every window in it drains on its
    /// own forecast and refills at its own reset, then they are averaged as the
    /// server averages them.
    static func combined(_ snapshot: Snapshot, series: [String: [SeriesPoint]], now: Date = .now) -> Outlook? {
        let parts: [[Track]] = snapshot.usages
            .filter(\.isOK)
            .map { usage in
                longWindows(usage).compactMap { window in
                    guard var t = track(window, points: series[usage.seriesKey(window)] ?? [], now: now) else { return nil }
                    t.name = "\(usage.displayName) \(window.displayLabel)"
                    return t
                }
            }
            .filter { !$0.isEmpty }
        guard !parts.isEmpty else { return nil }
        let all = parts.flatMap { $0 }
        guard all.contains(where: \.measured) else { return nil }

        func average(_ values: [Double]) -> Double { values.reduce(0, +) / Double(max(values.count, 1)) }
        let valueAt: (Date) -> Double = { t in average(parts.map { w in average(w.map { $0.level(at: t) }) }) }
        let steps: (Date, Date) -> [SeriesPoint] = { from, to in sampled(all, from: from, to: to, value: valueAt) }

        var dry: Date?
        let ahead = steps(now, now.addingTimeInterval(horizon))
        if let index = ahead.firstIndex(where: { $0.value <= idle }) {
            if index == 0 {
                dry = now
            } else {
                var low = ahead[index - 1].date, high = ahead[index].date
                for _ in 0..<40 {
                    let middle = Date(timeIntervalSince1970: (low.timeIntervalSince1970 + high.timeIntervalSince1970) / 2)
                    if valueAt(middle) <= idle { high = middle } else { low = middle }
                }
                dry = high
            }
        }

        func rate(_ key: KeyPath<Track, Double?>) -> Double? {
            guard parts.contains(where: { $0.contains { $0[keyPath: key] != nil } }) else { return nil }
            return average(parts.map { w in average(w.map { $0.level > 0 ? ($0[keyPath: key] ?? 0) : 0 }) })
        }

        let next = all.filter { $0.reset != nil }.min { $0.reset! < $1.reset! }
        return Outlook(
            recent: rate(\.recent),
            usual: rate(\.usual),
            level: valueAt(now),
            emptyAt: dry,
            reset: nil,
            next: next.map { ($0.name, $0.reset!) },
            steps: steps
        )
    }

    // MARK: Wording

    static func rateText(_ burn: Double) -> String {
        burn < 10 ? String(format: "%.1f%%/h", burn) : "\(Int(burn.rounded()))%/h"
    }

    /// "Burning 2.1%/h · usually 0.6%/h": the two rates the forecast is drawn from.
    static func rates(_ outlook: Outlook) -> String {
        guard outlook.draining else { return "Not draining lately" }
        var parts: [String] = []
        if let recent = outlook.recent {
            parts.append(recent > idle ? "Burning \(rateText(recent))" : "Idle for the last \(Int(burnHours))h")
        }
        if let usual = outlook.usual {
            parts.append("\(parts.isEmpty ? "Usually" : "usually") \(rateText(usual))")
        }
        return parts.joined(separator: " · ")
    }
}

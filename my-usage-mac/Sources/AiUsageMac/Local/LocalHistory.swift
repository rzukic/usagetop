import Foundation

/// Every reading this Mac took without a server, kept the way the server keeps
/// its own (`my-usage-server/src/AiUsage.Server/Store.cs`): a window's reading
/// is stored when its level or its reset moved, or once every 15 minutes while
/// flat, and the combined level beside them. Kept for 40 days, in
/// ~/Library/Application Support/usagetop/history.json.
struct LocalHistory {
    private struct Point: Codable {
        var at: Double
        var percent: Double
        var reset: Double?
        var stale: Bool?
    }

    private struct File: Codable {
        var series: [String: [Point]] = [:]
        var combined: [Point] = []
    }

    private static let noise = 0.05
    private static let heartbeat: TimeInterval = 15 * 60
    private static let keep: TimeInterval = 40 * 86_400

    private var file = File()
    private let url: URL

    init(profile: String = "") {
        let folder = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("usagetop", isDirectory: true)
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        url = folder.appendingPathComponent(profile.isEmpty ? "history.json" : "history-\(profile).json")
        if let data = try? Data(contentsOf: url), let stored = try? JSONDecoder().decode(File.self, from: data) {
            file = stored
        }
    }

    /// Files a round of readings. Only a reading that got through counts; one
    /// standing in for a failed fetch is still filed, marked stale.
    mutating func record(_ usages: [ProviderUsage], combined: Double?, at now: Date) {
        let at = now.timeIntervalSince1970
        for usage in usages where usage.isOK {
            for window in usage.windows {
                guard let percent = window.remainingPercent else { continue }
                let point = Point(at: at, percent: percent, reset: window.resetDate?.timeIntervalSince1970,
                                  stale: usage.stale ? true : nil)
                let key = usage.seriesKey(window)
                if let last = file.series[key]?.last, !Self.worthKeeping(last, point) { continue }
                file.series[key, default: []].append(point)
            }
        }
        if let combined {
            let point = Point(at: at, percent: combined)
            if file.combined.last.map({ abs($0.percent - combined) >= Self.noise || at - $0.at >= Self.heartbeat }) ?? true {
                file.combined.append(point)
            }
        }

        let cutoff = at - Self.keep
        for key in file.series.keys { file.series[key]?.removeAll { $0.at < cutoff } }
        file.series = file.series.filter { !$0.value.isEmpty }
        file.combined.removeAll { $0.at < cutoff }
        save()
    }

    /// Every series since `since`, thinned to at most `points` each, as the
    /// server sends them: `[[millis, percent], ...]`.
    func state(since: Date, points: Int) -> (series: [String: [[Double]]], combined: [[Double]]) {
        let from = since.timeIntervalSince1970
        func wire(_ list: [Point]) -> [[Double]] {
            Self.thin(list.filter { $0.at >= from }, max: points).map { [$0.at * 1000, $0.percent] }
        }
        return (file.series.mapValues(wire).filter { !$0.value.isEmpty }, wire(file.combined))
    }

    private static func worthKeeping(_ last: Point, _ next: Point) -> Bool {
        if abs(last.percent - next.percent) >= noise { return true }
        if (last.stale ?? false) != (next.stale ?? false) { return true }
        if next.at - last.at >= heartbeat { return true }
        // A reset given as "seconds from now" lands a few seconds apart each time;
        // only a real move counts.
        switch (last.reset, next.reset) {
        case (nil, nil): return false
        case let (a?, b?): return abs(a - b) > 120
        default: return true
        }
    }

    /// Each time slice's lowest and highest reading, so a refill stays a cliff
    /// rather than a slope.
    private static func thin(_ points: [Point], max: Int) -> [Point] {
        guard max >= 4, points.count > max, let first = points.first?.at, let last = points.last?.at else { return points }
        let span = last - first
        guard span > 0 else { return [points[points.count - 1]] }
        let buckets = max / 2
        var kept: [Point] = []
        var start = 0
        for bucket in 0..<buckets where start < points.count {
            let edge = first + span * Double(bucket + 1) / Double(buckets)
            var end = start
            while end < points.count && (points[end].at < edge || bucket == buckets - 1) { end += 1 }
            guard end > start else { continue }
            let slice = points[start..<end]
            let low = slice.min { $0.percent < $1.percent }!
            let high = slice.max { $0.percent < $1.percent }!
            kept.append(contentsOf: low.at <= high.at ? [low, high] : [high, low])
            start = end
        }
        // The two picks of a slice can be the same reading.
        var seen = Set<Double>()
        return kept.filter { seen.insert($0.at).inserted }
    }

    /// Deletes this profile's file; for the self-test only.
    func erase() { try? FileManager.default.removeItem(at: url) }

    private func save() {
        guard let data = try? JSONEncoder().encode(file) else { return }
        try? data.write(to: url, options: [.atomic])
    }
}

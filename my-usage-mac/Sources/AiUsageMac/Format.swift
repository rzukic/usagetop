import Foundation

/// The relative-time strings every screen needs, ported from
/// `my-usage-win/src/AiUsage/Ui/Format.cs` so the Mac app words things the same way.
enum Format {
    /// A percentage with no decimals, e.g. "84%".
    static func percent(_ value: Double) -> String {
        "\(Int(value.rounded()))%"
    }

    /// A length of time, e.g. "3d 23h" or "12m".
    static func span(_ duration: TimeInterval) -> String {
        let totalMinutes = max(0, Int(duration / 60))
        let hours = totalMinutes / 60
        let minutes = totalMinutes % 60

        if hours >= 24 {
            let days = hours / 24
            let rest = hours % 24
            return rest == 0 ? "\(days)d" : "\(days)d \(rest)h"
        }
        return hours >= 1 ? "\(hours)h \(minutes)m" : "\(minutes)m"
    }

    /// How long until a window resets, e.g. "3d 23h".
    static func reset(_ at: Date?) -> String? {
        guard let at else { return nil }
        let remaining = at.timeIntervalSinceNow
        return remaining <= 0 ? "now" : span(remaining)
    }

    /// How long ago a reading was taken, e.g. "12m ago".
    static func ago(_ at: Date) -> String {
        let minutes = Int(Date().timeIntervalSince(at) / 60)
        if minutes < 1 { return "just now" }
        if minutes < 60 { return "\(minutes)m ago" }
        if minutes < 48 * 60 { return "\(minutes / 60)h ago" }
        return "\(minutes / 1440)d ago"
    }

    private static let weekFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = .current
        formatter.setLocalizedDateFormatFromTemplate("EEE HH:mm")
        return formatter
    }()

    private static let dateFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = .current
        formatter.setLocalizedDateFormatFromTemplate("d MMM HH:mm")
        return formatter
    }()

    /// A moment ahead: "Thu 21:54" within the week, "12 Nov 21:54" past it.
    static func when(_ at: Date) -> String {
        let withinWeek = abs(at.timeIntervalSinceNow) < 6 * 24 * 3600
        return (withinWeek ? weekFormatter : dateFormatter).string(from: at)
    }

    /// The line under the title. With a server that could not be reached, the
    /// reading on screen is an old one and has to say so.
    static func status(snapshot: Snapshot?, problem: String?) -> String {
        if let problem {
            guard let snapshot, !snapshot.usages.isEmpty, let at = snapshot.fetchedDate else {
                return problem
            }
            return "\(problem) · reading from \(ago(at))"
        }
        guard let snapshot, let at = snapshot.fetchedDate else { return "Never updated" }
        return "Updated \(ago(at))"
    }
}

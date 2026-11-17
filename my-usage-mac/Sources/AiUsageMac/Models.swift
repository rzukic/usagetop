import Foundation

/// The JSON the server sends, mirroring `my-usage-server/src/AiUsage.Server/Wire.cs`.
/// A series is `[[millis, percent], ...]`; the snapshot is the same shape the
/// Android app caches, so every client reads one format.
struct StateResponse: Decodable {
    let serverTime: Int64
    let snapshot: Snapshot?
    let combined: [[Double]]?
    let series: [String: [[Double]]]?
}

struct Snapshot: Decodable {
    let fetchedAt: String?
    let combinedPercent: Double?
    let combinedProviders: Int?
    let usages: [ProviderUsage]

    var fetchedDate: Date? { WireDate.parse(fetchedAt) }
}

struct ProviderUsage: Decodable, Identifiable, Hashable {
    /// The provider: "claude", "chatgpt", ...
    let providerKey: String
    /// The account: "claude", or "claude-2" for a second one. Series are filed
    /// under this, and it is what tells two accounts of one provider apart.
    let account: String
    let accountName: String?
    let state: String
    let message: String?
    let plan: String?
    let fetchedAt: String?
    let stale: Bool
    let transient: Bool
    let windows: [UsageWindow]

    private enum CodingKeys: String, CodingKey {
        case providerKey = "id"
        case account, accountName, state, message, plan, fetchedAt, stale, transient, windows
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        providerKey = try c.decode(String.self, forKey: .providerKey)
        account = try c.decodeIfPresent(String.self, forKey: .account) ?? providerKey
        accountName = try c.decodeIfPresent(String.self, forKey: .accountName)
        state = try c.decodeIfPresent(String.self, forKey: .state) ?? "ERROR"
        message = try c.decodeIfPresent(String.self, forKey: .message)
        plan = try c.decodeIfPresent(String.self, forKey: .plan)
        fetchedAt = try c.decodeIfPresent(String.self, forKey: .fetchedAt)
        stale = try c.decodeIfPresent(Bool.self, forKey: .stale) ?? false
        transient = try c.decodeIfPresent(Bool.self, forKey: .transient) ?? false
        windows = try c.decodeIfPresent([UsageWindow].self, forKey: .windows) ?? []
    }

    init(providerKey: String, account: String? = nil, accountName: String? = nil, state: String = "OK",
         message: String? = nil, plan: String? = nil, fetchedAt: String? = nil, stale: Bool = false,
         transient: Bool = false, windows: [UsageWindow]) {
        self.providerKey = providerKey
        self.account = account ?? providerKey
        self.accountName = accountName
        self.state = state
        self.message = message
        self.plan = plan
        self.fetchedAt = fetchedAt
        self.stale = stale
        self.transient = transient
        self.windows = windows
    }

    var id: String { account }
    var provider: Provider { Provider(key: providerKey) }

    /// "Claude", "Claude · Work", or "Claude #2" for an added account with no name.
    var displayName: String {
        if let accountName, !accountName.isEmpty { return "\(provider.displayName) · \(accountName)" }
        guard account != providerKey else { return provider.displayName }
        let suffix = account.hasPrefix(providerKey + "-") ? String(account.dropFirst(providerKey.count + 1)) : account
        return "\(provider.displayName) #\(suffix)"
    }

    /// The second half of the name, for places that already show the mark.
    var accountLabel: String? {
        if let accountName, !accountName.isEmpty { return accountName }
        return account == providerKey ? nil : displayName.replacingOccurrences(of: provider.displayName + " ", with: "")
    }

    var isOK: Bool { state == "OK" }
    var isNotConfigured: Bool { state == "NOT_CONFIGURED" }
    var fetchedDate: Date? { WireDate.parse(fetchedAt) }

    /// The window that decides how this subscription is doing: the lowest
    /// remaining level among the ones that count toward the combined figure.
    var tightest: UsageWindow? {
        let counted = windows.filter { $0.countsTowardCombined && $0.remainingPercent != nil }
        let pool = counted.isEmpty ? windows.filter { $0.remainingPercent != nil } : counted
        return pool.min { ($0.remainingPercent ?? 100) < ($1.remainingPercent ?? 100) }
    }

    func seriesKey(_ window: UsageWindow) -> String { "\(account)|\(window.label)" }
}

struct UsageWindow: Decodable, Identifiable, Hashable {
    let label: String
    let remainingPercent: Double?
    let resetAt: String?
    let detail: String?
    let countsTowardCombined: Bool

    private enum CodingKeys: String, CodingKey {
        case label, remainingPercent, resetAt, detail, countsTowardCombined
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        label = try c.decode(String.self, forKey: .label)
        remainingPercent = try c.decodeIfPresent(Double.self, forKey: .remainingPercent)
        resetAt = try c.decodeIfPresent(String.self, forKey: .resetAt)
        detail = try c.decodeIfPresent(String.self, forKey: .detail)
        countsTowardCombined = try c.decodeIfPresent(Bool.self, forKey: .countsTowardCombined) ?? true
    }

    init(label: String, remainingPercent: Double?, resetAt: Date?, detail: String? = nil, countsTowardCombined: Bool = true) {
        self.label = label
        self.remainingPercent = remainingPercent
        self.resetAt = resetAt.map(WireDate.format)
        self.detail = detail
        self.countsTowardCombined = countsTowardCombined
    }

    var id: String { label }
    var resetDate: Date? { WireDate.parse(resetAt) }

    /// The name the app shows. Providers name the same period differently
    /// ("7d" from one, "Weekly" from another); on screen a period has one
    /// name. `label` itself stays as sent: history is filed under it.
    var displayLabel: String { WindowName.display(label) }
}

enum WindowName {
    /// Durations that are a calendar word, by their length in days.
    private static let words = [1: "Daily", 7: "Weekly"]

    /// "7d" → "Weekly", "Fable 7d" → "Fable weekly", "1d" → "Daily". Anything
    /// else ("5h", "Monthly", "Vibe") is left as it is.
    static func display(_ label: String) -> String {
        let trimmed = label.trimmingCharacters(in: .whitespaces)
        guard let match = trimmed.firstMatch(of: #/^(?:(.*\S)\s+)?(\d+)\s*[dD]$/#),
              let days = Int(match.2), let word = words[days] else { return trimmed }
        guard let prefix = match.1 else { return word }
        return "\(prefix) \(word.lowercased())"
    }

    /// The shortest name that still tells a window apart within its provider,
    /// for the menu bar: a window named "<model> <period>" ("Fable 7d") goes by
    /// its model ("Fable"). The rest go by `display`.
    static func compact(_ label: String) -> String {
        let trimmed = label.trimmingCharacters(in: .whitespaces)
        if let match = trimmed.firstMatch(of: #/^(.*\S)\s+(\d+)\s*[mhdMHD]$/#) { return String(match.1) }
        return display(trimmed)
    }
}

/// One reading in a history series.
struct SeriesPoint: Hashable {
    let date: Date
    let value: Double

    static func decode(_ raw: [[Double]]?) -> [SeriesPoint] {
        (raw ?? []).compactMap { pair in
            guard pair.count >= 2 else { return nil }
            return SeriesPoint(date: Date(timeIntervalSince1970: pair[0] / 1000), value: pair[1])
        }
    }
}

/// Everything fixed about a provider: its key and its name. The mark lives in
/// `Marks.swift`.
struct Provider {
    let key: String

    static let names: [String: String] = [
        "claude": "Claude",
        "chatgpt": "ChatGPT",
        "mistral": "Mistral",
        "kimi": "Kimi",
        "muse": "Muse",
    ]

    var displayName: String { Provider.names[key] ?? key.capitalized }
    var monogram: String { String(displayName.prefix(1)).uppercased() }
}

enum WireDate {
    nonisolated(unsafe) private static let withFraction: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()

    nonisolated(unsafe) private static let plain: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f
    }()

    static func parse(_ text: String?) -> Date? {
        guard let text, !text.isEmpty else { return nil }
        return withFraction.date(from: text) ?? plain.date(from: text)
    }

    static func format(_ date: Date) -> String {
        withFraction.string(from: date)
    }
}

import Foundation

/// What one account holds, as a provider needs it.
struct LocalCredential: Sendable {
    var token: String?
    var accountId: String?
    var kimiChina = false
    var mistralBudget: Double?
    var mistralSpent: Double?
}

/// One answer from a provider, and whether it refused the token: a refusal is
/// renewed and tried once more before the account says "Token rejected".
struct Fetched: Sendable {
    var usage: ProviderUsage
    var rejected = false
}

/// The providers, asked from this Mac. Each is the twin of the Windows app's
/// and the server's `my-usage-win/src/AiUsage.Core/Providers/*.cs`, and reads
/// the same endpoints the same way, so a reading here matches one there.
enum LocalProviders {
    static func fetch(_ provider: String, _ credential: LocalCredential) async -> Fetched {
        guard let token = credential.token, !token.isEmpty else {
            return Fetched(usage: notConfigured(provider, takesKey(provider) ? "No key" : "Not signed in"))
        }
        do {
            switch provider {
            case "claude": return try await claude(token)
            case "chatgpt": return try await chatGPT(token, accountId: credential.accountId)
            case "mistral": return try await mistral(token, credential)
            case "kimi": return try await kimi(token, china: credential.kimiChina)
            case "muse": return try await muse(token)
            case "antigravity": return try await antigravity(token)
            default: return Fetched(usage: error(provider, "Unknown provider"))
            }
        } catch {
            return Fetched(usage: networkError(provider, error))
        }
    }

    static func takesKey(_ provider: String) -> Bool { ["mistral", "kimi"].contains(provider) }

    // MARK: Claude

    private static func claude(_ token: String) async throws -> Fetched {
        let headers = ["Authorization": "Bearer \(token)", "anthropic-beta": "oauth-2025-04-20", "Accept": "application/json"]
        let reply = try await Net.get("https://api.anthropic.com/api/oauth/usage", headers: headers)
        guard reply.code == 200 else { return httpError("claude", reply.code) }
        guard let root = reply.json else { return Fetched(usage: error("claude", "Unexpected response")) }

        var windows: [UsageWindow] = []
        if let five = JSON.object(root, "five_hour") { windows.append(claudeWindow("5h", five)) }
        if let seven = JSON.object(root, "seven_day") { windows.append(claudeWindow("7d", seven)) }
        // Per-model weekly limits are a slice of the week, so they stay out of
        // the combined figure.
        for entry in JSON.array(root, "limits") where JSON.text(entry, "kind") == "weekly_scoped" {
            let model = JSON.text(JSON.object(JSON.object(entry, "scope"), "model"), "display_name")
            guard let model, let used = JSON.number(entry, "percent") else { continue }
            windows.append(UsageWindow(label: "\(model) 7d", remainingPercent: clamp(100 - used),
                                       resetAt: JSON.date(JSON.text(entry, "resets_at")), countsTowardCombined: false))
        }
        guard !windows.isEmpty else { return Fetched(usage: error("claude", "No usage in response")) }

        var plan: String?
        if let profile = try? await Net.get("https://api.anthropic.com/api/oauth/profile", headers: headers), profile.code == 200 {
            plan = switch JSON.text(JSON.object(profile.json, "organization"), "organization_type") {
            case "claude_pro": "Pro"
            case "claude_max": "Max"
            case "claude_team": "Team"
            case "claude_enterprise": "Enterprise"
            case "claude_free": "Free"
            default: nil
            }
        }
        return Fetched(usage: ok("claude", windows, plan: plan))
    }

    private static func claudeWindow(_ label: String, _ json: [String: Any]) -> UsageWindow {
        let used = JSON.number(json, "utilization") ?? JSON.number(json, "used_percent") ?? 0
        return UsageWindow(label: label, remainingPercent: clamp(100 - used), resetAt: JSON.date(JSON.text(json, "resets_at")))
    }

    // MARK: ChatGPT

    private static func chatGPT(_ token: String, accountId: String?) async throws -> Fetched {
        var headers = ["Authorization": "Bearer \(token)", "Accept": "application/json",
                       "User-Agent": "Mozilla/5.0 (Macintosh) usagetop"]
        if let accountId { headers["chatgpt-account-id"] = accountId }
        let reply = try await Net.get("https://chatgpt.com/backend-api/wham/usage", headers: headers)
        guard reply.code == 200 else { return httpError("chatgpt", reply.code) }
        guard let root = reply.json else { return Fetched(usage: error("chatgpt", "Unexpected response")) }

        let limits = JSON.object(root, "rate_limit") ?? root
        let windows = ["primary_window", "secondary_window"].compactMap { JSON.object(limits, $0) }.map { json in
            let used = min(max(JSON.number(json, "used_percent") ?? 0, 0), 100)
            let after = JSON.number(json, "reset_after_seconds")
            let seconds = Int(JSON.number(json, "limit_window_seconds") ?? 0)
            let label = switch seconds {
            case ...0: "limit"
            case ..<3600: "\(seconds / 60)m"
            case ..<86_400: "\(seconds / 3600)h"
            default: "\(seconds / 86_400)d"
            }
            return UsageWindow(label: label, remainingPercent: 100 - used,
                               resetAt: after.flatMap { $0 >= 0 ? Date().addingTimeInterval($0) : nil })
        }
        guard !windows.isEmpty else { return Fetched(usage: error("chatgpt", "No limits in response")) }

        let raw = JSON.text(root, "plan_type")?.lowercased()
        let plan: String? = switch raw {
        case nil: nil
        case "free": "Free"
        case "plus": "Plus"
        case "pro": "Pro"
        case "team": "Team"
        case "business": "Business"
        case "enterprise": "Enterprise"
        case let other?: other.prefix(1).uppercased() + other.dropFirst()
        }
        return Fetched(usage: ok("chatgpt", windows, plan: plan))
    }

    // MARK: Mistral

    private static func mistral(_ token: String, _ credential: LocalCredential) async throws -> Fetched {
        let base = "https://api.mistral.ai"
        let headers = ["Authorization": "Bearer \(token)", "Accept": "application/json"]
        let reply = try await Net.get("\(base)/v1/billing/budget", headers: headers)
        guard reply.code == 200 else { return mistralFallback(credential, httpError("mistral", reply.code)) }
        guard let root = reply.json else { return Fetched(usage: error("mistral", "Unexpected response")) }

        // The API credit cannot be spent on coding workflows, so it stays out of
        // the combined figure; the Vibe allowance is what the subscription gives.
        var windows: [UsageWindow] = []
        if let api = JSON.object(root, "api_budget") { windows.append(mistralWindow("API", api, counts: false)) }
        if let vibe = JSON.object(root, "vibe_budget") { windows.append(mistralWindow("Vibe", vibe, counts: true)) }
        guard !windows.isEmpty else {
            return mistralFallback(credential, Fetched(usage: error("mistral", "No budget in response")))
        }

        var plan: String?
        if let sub = try? await Net.get("\(base)/v1/billing/subscription", headers: headers), sub.code == 200,
           let name = JSON.text(sub.json, "plan_name") {
            plan = switch name.uppercased() {
            case "EDU": "Education"
            case "FREE": "Free"
            case "INDIVIDUAL": "Pro"
            case "TEAM": "Team"
            default: name.prefix(1).uppercased() + name.dropFirst()
            }
        }
        return Fetched(usage: ok("mistral", windows, plan: plan))
    }

    private static func mistralWindow(_ label: String, _ json: [String: Any], counts: Bool) -> UsageWindow {
        let used = min(max(JSON.number(json, "usage_percentage") ?? 0, 0), 100)
        let cap = JSON.number(json, "initial_budget") ?? 0
        let symbol = switch JSON.text(json, "currency")?.uppercased() {
        case "EUR": "€"
        case "USD": "$"
        case "GBP": "£"
        default: ""
        }
        let detail = cap > 0
            ? String(format: "%@%.2f of %@%.2f left", symbol, cap * (100 - used) / 100, symbol, cap)
            : nil
        return UsageWindow(label: label, remainingPercent: 100 - used, resetAt: JSON.date(JSON.text(json, "reset_at")),
                           detail: detail, countsTowardCombined: counts)
    }

    /// A budget typed in by hand, for a key that cannot read billing.
    private static func mistralFallback(_ credential: LocalCredential, _ failure: Fetched) -> Fetched {
        guard let budget = credential.mistralBudget, budget > 0 else { return failure }
        let spent = credential.mistralSpent ?? 0
        let window = UsageWindow(label: "month", remainingPercent: 100 - clamp(spent / budget * 100), resetAt: nil,
                                 detail: String(format: "€%.2f of €%.2f left", budget - spent, budget))
        return Fetched(usage: ok("mistral", [window], message: failure.usage.message))
    }

    // MARK: Kimi

    private static func kimi(_ token: String, china: Bool) async throws -> Fetched {
        // Kimi Code, overseas then China; each takes its key one of two ways.
        for host in ["https://api.kimi.ai", "https://api.kimi.com"] {
            for headers in [["Authorization": "Bearer \(token)", "Accept": "application/json"],
                            ["x-api-key": token, "Accept": "application/json"]] {
                guard let reply = try? await Net.get("\(host)/coding/v1/usages", headers: headers) else { break }
                if reply.code == 401 || reply.code == 403 { continue }
                guard reply.code == 200, let windows = kimiPlan(reply.json) else { break }
                var plan: String?
                if let me = try? await Net.get("\(host)/coding/v1/me", headers: headers), me.code == 200 {
                    plan = JSON.text(me.json, "user_level_name")
                }
                return Fetched(usage: ok("kimi", windows, plan: plan))
            }
        }

        // Prepaid Open Platform credit, for a key that is not a Kimi Code key.
        let base = china ? "https://api.moonshot.cn" : "https://api.moonshot.ai"
        let reply = try await Net.get("\(base)/v1/users/me/balance",
                                      headers: ["Authorization": "Bearer \(token)", "Accept": "application/json"])
        guard reply.code == 200 else { return httpError("kimi", reply.code) }
        guard let root = reply.json else { return Fetched(usage: error("kimi", "Unexpected response")) }
        guard let available = JSON.number(JSON.object(root, "data") ?? root, "available_balance") else {
            return Fetched(usage: error("kimi", "No balance in response"))
        }
        // A Kimi Code subscriber who pasted an Open Platform key lands here with
        // an empty balance: say which key is needed instead of showing ¥0.00.
        guard available > 0 else { return Fetched(usage: error("kimi", "Kimi Code key needed")) }
        return Fetched(usage: ok("kimi", [UsageWindow(label: "balance", remainingPercent: nil, resetAt: nil,
                                                      detail: String(format: "¥%.2f available", available))]))
    }

    /// The ratio pools, or the legacy request counts when those are all there is.
    private static func kimiPlan(_ root: [String: Any]?) -> [UsageWindow]? {
        guard let root else { return nil }
        let usages = JSON.object(root, "usages")
        let ratios = [("5h", "limit_5h"), ("Monthly", "limit_month_total")].compactMap { label, key -> UsageWindow? in
            guard let pool = JSON.object(usages, key), let used = JSON.number(pool, "used_ratio") else { return nil }
            return UsageWindow(label: label, remainingPercent: clamp((1 - used) * 100),
                               resetAt: JSON.date(JSON.text(pool, "reset_time")))
        }
        if !ratios.isEmpty { return ratios }

        let entries = JSON.array(root, "limits")
        let counts = entries.enumerated().compactMap { index, entry -> UsageWindow? in
            let detail = JSON.object(entry, "detail") ?? entry
            guard let limit = JSON.number(detail, "limit"), limit > 0 else { return nil }
            let used = min(max(JSON.number(detail, "used") ?? 0, 0), limit)
            let window = JSON.object(entry, "window")
            let duration = Int(JSON.number(window, "duration") ?? 0)
            let unit = (JSON.text(window, "timeUnit") ?? "").uppercased()
            let minutes = unit.contains("MINUTE") ? duration : unit.contains("HOUR") ? duration * 60
                : unit.contains("DAY") ? duration * 1440 : 0
            let label = switch minutes {
            case ...0: entries.count > 1 ? "window \(index + 1)" : "limit"
            case ..<60: "\(minutes)m"
            case ..<1440: "\(minutes / 60)h"
            default: "\(minutes / 1440)d"
            }
            return UsageWindow(label: label, remainingPercent: clamp((limit - used) / limit * 100),
                               resetAt: kimiReset(detail["resetTime"]),
                               detail: String(format: "%.0f of %.0f left", limit - used, limit))
        }
        return counts.isEmpty ? nil : counts
    }

    /// Epoch millis, epoch seconds or an ISO timestamp.
    private static func kimiReset(_ value: Any?) -> Date? {
        func epoch(_ raw: Double) -> Date { Date(timeIntervalSince1970: raw < 100_000_000_000 ? raw : raw / 1000) }
        switch value {
        case let number as NSNumber: return epoch(number.doubleValue)
        case let text as String: return Double(text).map(epoch) ?? JSON.date(text)
        default: return nil
        }
    }

    // MARK: Muse

    private static func muse(_ token: String) async throws -> Fetched {
        let headers = ["Authorization": "Bearer \(token)", "x-api-version": "1.0.0", "Accept": "application/json"]
        // A POST that also onboards; onboard: false keeps it read-only.
        let reply = try await Net.postJSON("https://api.meta.ai/muse-code/key", headers: headers, body: #"{"onboard":false}"#)
        if reply.code == 401 || reply.code == 403 { return Fetched(usage: error("muse", "Sign in again")) }
        guard reply.code == 200 else { return httpError("muse", reply.code) }
        guard let root = reply.json else { return Fetched(usage: error("muse", "Unexpected response")) }
        guard JSON.flag(root, "is_subs_active") else { return Fetched(usage: error("muse", "No subscription")) }

        let subs = JSON.object(root, "subs_usage")
        let windows = [("5h", "window"), ("Weekly", "weekly")].compactMap { label, key -> UsageWindow? in
            guard let json = JSON.object(subs, key), let used = JSON.number(json, "used_percent") else { return nil }
            // Unix seconds here, unlike every other provider.
            let reset = JSON.number(json, "resets_at").flatMap { $0 > 0 ? Date(timeIntervalSince1970: $0) : nil }
                ?? JSON.date(JSON.text(json, "resets_at"))
            return UsageWindow(label: label, remainingPercent: clamp(100 - used), resetAt: reset)
        }
        // Meta leaves usage out while the five-hour window is idle: the last
        // reading stands in, and is rolled over once its reset has passed.
        guard !windows.isEmpty else {
            return Fetched(usage: error("muse", "No usage reported", transient: true))
        }
        let plan = JSON.text(root, "subs_tier_name")?
            .replacingOccurrences(of: "Muse Code ", with: "", options: .caseInsensitive)
            .replacingOccurrences(of: " Usage", with: "", options: .caseInsensitive)
            .trimmingCharacters(in: .whitespaces)
        return Fetched(usage: ok("muse", windows, plan: plan?.isEmpty == false ? plan : nil))
    }

    // MARK: Antigravity

    private static func antigravity(_ token: String) async throws -> Fetched {
        let base = "https://daily-cloudcode-pa.googleapis.com/v1internal"
        // The quota endpoint only answers a client that names itself as Antigravity.
        let headers = ["Authorization": "Bearer \(token)", "Accept": "application/json",
                       "User-Agent": "antigravity/cli/1.5.12 (aidev_client; auth_method=consumer)"]

        var plan: String?
        var project = "aicode-consumers"
        let load = try await Net.postJSON("\(base):loadCodeAssist", headers: headers, body: #"{"metadata":{"ideType":"ANTIGRAVITY"}}"#)
        if load.code == 401 { return httpError("antigravity", 401) }
        if load.code == 200, let root = load.json {
            plan = JSON.text(JSON.object(root, "paidTier"), "name") ?? JSON.text(JSON.object(root, "currentTier"), "name")
            if JSON.flag(root, "gcpManaged"),
               let managed = JSON.text(root, "cloudaicompanionProject") ?? JSON.text(JSON.object(root, "cloudaicompanionProject"), "id") {
                project = managed
            }
        }

        let reply = try await Net.postJSON("\(base):retrieveUserQuotaSummary", headers: headers, object: ["project": project])
        if reply.code == 403 { return Fetched(usage: error("antigravity", "No Antigravity plan", plan: plan)) }
        guard reply.code == 200 else { return httpError("antigravity", reply.code) }
        let windows = antigravityWindows(reply.json)
        guard !windows.isEmpty else { return Fetched(usage: error("antigravity", "No limits in response", plan: plan)) }
        return Fetched(usage: ok("antigravity", windows, plan: plan))
    }

    /// Every group's buckets: the Gemini group's as plain "5h" and "7d", and the
    /// only ones in the combined figure; another group's carry its name.
    static func antigravityWindows(_ root: [String: Any]?) -> [UsageWindow] {
        func short(_ name: String?) -> String {
            var text = (name ?? "").trimmingCharacters(in: .whitespaces)
            if text.lowercased().hasSuffix(" models") { text = String(text.dropLast(" models".count)) }
            return text.replacingOccurrences(of: " and ", with: " & ")
        }
        func span(_ window: String?) -> String? {
            switch window?.trimmingCharacters(in: .whitespaces).lowercased() {
            case nil, "": nil
            case "weekly", "week": "7d"
            case "daily", "day": "1d"
            case "monthly", "month": "Monthly"
            case let other?: other.first?.isNumber == true ? other : nil
            }
        }
        func windows(_ buckets: [[String: Any]], prefix: String, main: Bool) -> [UsageWindow] {
            buckets.compactMap { bucket in
                guard !JSON.flag(bucket, "disabled"), let fraction = JSON.number(bucket, "remainingFraction") else { return nil }
                let name = span(JSON.text(bucket, "window")) ?? JSON.text(bucket, "displayName") ?? "limit"
                return UsageWindow(label: prefix.isEmpty ? name : "\(prefix) \(name)", remainingPercent: clamp(fraction * 100),
                                   resetAt: JSON.date(JSON.text(bucket, "resetTime")), countsTowardCombined: main)
            }
        }

        let groups = JSON.array(root, "groups")
        guard !groups.isEmpty else { return windows(JSON.array(root, "buckets"), prefix: "", main: true) }
        let main = groups.firstIndex { short(JSON.text($0, "displayName")) == "Gemini" } ?? 0
        return groups.enumerated().flatMap { index, group in
            windows(JSON.array(group, "buckets"), prefix: index == main ? "" : short(JSON.text(group, "displayName")),
                    main: index == main)
        }
    }

    // MARK: Results

    private static func clamp(_ value: Double) -> Double { min(max(value, 0), 100) }

    private static func ok(_ provider: String, _ windows: [UsageWindow], plan: String? = nil, message: String? = nil) -> ProviderUsage {
        ProviderUsage(providerKey: provider, state: "OK", message: message, plan: plan,
                      fetchedAt: WireDate.format(Date()), windows: windows)
    }

    private static func error(_ provider: String, _ message: String, transient: Bool = false, plan: String? = nil) -> ProviderUsage {
        ProviderUsage(providerKey: provider, state: "ERROR", message: message, plan: plan,
                      fetchedAt: WireDate.format(Date()), transient: transient, windows: [])
    }

    private static func notConfigured(_ provider: String, _ message: String) -> ProviderUsage {
        ProviderUsage(providerKey: provider, state: "NOT_CONFIGURED", message: message,
                      fetchedAt: WireDate.format(Date()), windows: [])
    }

    /// A rate limit or a server fault is temporary; a refused token is not. A
    /// 403 is what an edge says when it dislikes the caller, and no new token
    /// changes that, so only a 401 is worth renewing for.
    private static func httpError(_ provider: String, _ code: Int) -> Fetched {
        let message = switch code {
        case 401, 403: "Token rejected"
        case 404: "Endpoint moved"
        case 429: "Rate limited"
        default: "HTTP \(code)"
        }
        return Fetched(usage: error(provider, message, transient: code == 429 || code >= 500), rejected: code == 401)
    }

    private static func networkError(_ provider: String, _ failure: Error) -> ProviderUsage {
        let code = (failure as? URLError)?.code
        let message = code == .timedOut ? "Timed out" : (code != nil ? "No connection" : String(failure.localizedDescription.prefix(60)))
        return error(provider, message, transient: true)
    }
}

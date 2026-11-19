import Foundation
import Observation
import Security
import ServiceManagement

/// Where the server is and how to authenticate to it. The address is ordinary
/// preference; the token goes to the Keychain, because it is a credential.
@Observable
final class Settings {
    private static let urlKey = "serverURL"
    private static let tokenAccount = "apiToken"

    var serverURL: String {
        didSet { UserDefaults.standard.set(serverURL, forKey: Self.urlKey) }
    }

    var token: String {
        didSet { Keychain.write(token, account: Self.tokenAccount) }
    }

    /// What the menu bar shows: 0 is the combined figure in a ring; 1 to 4
    /// is that many limits, the ones closest to running out, each as its
    /// provider's mark with the limit's name over what is left.
    var menuBarLimits: Int {
        didSet { UserDefaults.standard.set(menuBarLimits, forKey: Self.limitsKey) }
    }

    static let maxMenuBarLimits = 4

    /// Whether the menu bar item shows the figure beside the ring, or the ring alone.
    var showsPercentInMenuBar: Bool {
        didSet { UserDefaults.standard.set(showsPercentInMenuBar, forKey: Self.percentKey) }
    }

    /// How much of the panel is glass: 0 is a flat, opaque panel in the
    /// window colour; 1 is clear Liquid Glass with only its tint.
    var glass: Double {
        didSet { UserDefaults.standard.set(glass, forKey: Self.glassKey) }
    }
    private static let glassKey = "panelGlass"

    /// How solid the tiles are, apart from the panel: 0 is the faint,
    /// see-through fill; 1 is an opaque tile, for reading over a busy desktop.
    var tiles: Double {
        didSet { UserDefaults.standard.set(tiles, forKey: Self.tilesKey) }
    }
    private static let tilesKey = "tileSolidity"

    /// The order of the accounts in the sidebar (and so of ⌘1–9), by account
    /// key. Accounts not in it follow, in the default order.
    var accountOrder: [String] {
        didSet { UserDefaults.standard.set(accountOrder, forKey: Self.orderKey) }
    }
    private static let orderKey = "accountOrder"

    /// Limits left out of the overview, the menu bar and the rail's rings, as
    /// "account|label" (the key their history is filed under). They stay on
    /// their account's page. On this Mac only.
    var hiddenLimits: [String] {
        didSet { UserDefaults.standard.set(hiddenLimits, forKey: Self.hiddenKey) }
    }
    private static let hiddenKey = "hiddenLimits"

    /// The overview lists the account closest to running out first; off, it
    /// follows the sidebar's order.
    var overviewByRisk: Bool {
        didSet { UserDefaults.standard.set(overviewByRisk, forKey: Self.byRiskKey) }
    }
    private static let byRiskKey = "overviewByRisk"

    private static let figureKey = "menuBarFigure"
    private static let limitsKey = "menuBarLimits"
    private static let percentKey = "menuBarShowsPercent"

    init() {
        serverURL = UserDefaults.standard.string(forKey: Self.urlKey) ?? ""
        token = Keychain.read(Self.tokenAccount) ?? ""
        if let stored = UserDefaults.standard.object(forKey: Self.limitsKey) as? Int {
            menuBarLimits = min(max(stored, 0), Self.maxMenuBarLimits)
        } else {
            // "Most at risk" from before there was a choice of how many: one limit.
            menuBarLimits = UserDefaults.standard.string(forKey: Self.figureKey) == "tightest" ? 1 : 0
        }
        showsPercentInMenuBar = UserDefaults.standard.object(forKey: Self.percentKey) as? Bool ?? true
        glass = min(max(UserDefaults.standard.object(forKey: Self.glassKey) as? Double ?? 1, 0), 1)
        tiles = min(max(UserDefaults.standard.object(forKey: Self.tilesKey) as? Double ?? 0, 0), 1)
        accountOrder = UserDefaults.standard.stringArray(forKey: Self.orderKey) ?? []
        hiddenLimits = UserDefaults.standard.stringArray(forKey: Self.hiddenKey) ?? []
        overviewByRisk = UserDefaults.standard.object(forKey: Self.byRiskKey) as? Bool ?? true
    }

    /// Opening at login is the system's to keep, not ours: ask it each time.
    var launchesAtLogin: Bool {
        get { SMAppService.mainApp.status == .enabled }
        set {
            do {
                if newValue { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            } catch {
                NSLog("Launch at login: \(error.localizedDescription)")
            }
        }
    }

    /// "host:8377" or "http://host:8377/" both become "http://host:8377".
    /// Nil when the text is not an address at all.
    var baseURL: URL? {
        var trimmed = serverURL.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }
        if !trimmed.contains("://") { trimmed = "http://" + trimmed }
        while trimmed.hasSuffix("/") { trimmed.removeLast() }
        guard let url = URL(string: trimmed), url.host != nil else { return nil }
        return url
    }

    var isConfigured: Bool { baseURL != nil }
}

enum Keychain {
    private static let service = "io.github.rzukic.usagetop.mac"

    static func read(_ account: String) -> String? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess,
              let data = item as? Data,
              let text = String(data: data, encoding: .utf8)
        else { return nil }
        return text
    }

    static func write(_ value: String, account: String) {
        let base: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]

        guard !value.isEmpty else {
            SecItemDelete(base as CFDictionary)
            return
        }

        let data = Data(value.utf8)
        let update = [kSecValueData as String: data]
        let status = SecItemUpdate(base as CFDictionary, update as CFDictionary)
        if status == errSecItemNotFound {
            var insert = base
            insert[kSecValueData as String] = data
            SecItemAdd(insert as CFDictionary, nil)
        }
    }
}

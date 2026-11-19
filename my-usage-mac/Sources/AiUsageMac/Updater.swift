import AppKit
import CryptoKit
import Foundation
import Observation
import os
import Security

private let log = Logger(subsystem: "io.github.rzukic.usagetop.mac", category: "update")

/// Keeps a copy in Applications current from the GitHub releases.
///
/// It looks a minute after start and then every six hours, or when asked in
/// Settings; never more often on its own, because GitHub allows an address 60
/// unauthenticated API calls an hour and each look costs one. A newer version
/// is downloaded in the background, checked and then waits beside the app's
/// other files: "Restart to update" puts it in at once, Quit puts it in as the
/// app leaves, and failing both it goes in the next time the app starts.
/// Nothing here ever pops up: a failed look is a line in Settings.
///
/// What is checked before a download may replace the app: its SHA-256 against
/// the one GitHub lists for the file, that it is this app (bundle identifier)
/// in the version the release names, and that its signature is intact. The
/// signature is ad hoc, so it says the bundle is whole, not who made it; that
/// rests on the download coming from GitHub over HTTPS.
///
/// A copy anywhere but an Applications folder (a build folder, Downloads) does
/// nothing at all. USAGETOP_UPDATE_SOURCE, an http(s) address that answers
/// like https://api.github.com/repos/rzukic/usagetop, stands in for GitHub, to
/// try an update end to end without publishing.
@MainActor
@Observable
final class Updater {
    enum Phase { case off, waiting, checking, upToDate, downloading, ready, failed }

    private static let api = "https://api.github.com/repos/rzukic/usagetop"
    private static let asset = "usagetop-mac.zip"
    private static let sourceVariable = "USAGETOP_UPDATE_SOURCE"
    private static let betaKey = "betaUpdates"
    private static let firstLook: Duration = .seconds(60)
    private static let period: Duration = .seconds(6 * 60 * 60)

    private(set) var phase: Phase
    /// The version being downloaded, or downloaded and waiting.
    private(set) var target: String?
    /// When a look last got an answer.
    private(set) var checkedAt: Date?
    /// What a failure was, in a line short enough for the status; why, for its tooltip.
    private(set) var failure: String?
    private(set) var failureDetail: String?
    /// A look or a download is under way.
    private(set) var busy = false

    /// Early builds as well as releases, whichever is newer.
    var beta: Bool {
        didSet {
            guard beta != oldValue else { return }
            UserDefaults.standard.set(beta, forKey: Self.betaKey)
            // A different question, so it is asked again.
            Task { await check() }
        }
    }

    /// The running version, as the release names it ("1.2.1-beta.1").
    let version: String
    /// The testing source in place of GitHub, if one is set.
    let sourceOverride: String?
    /// Nil unless this copy is in an Applications folder; everything else keys off it.
    private let installed: URL?

    @ObservationIgnored private var schedule: Task<Void, Never>?
    @ObservationIgnored private var again = false
    @ObservationIgnored private var handedOver = false
    /// The checked bundle that is waiting to go in.
    @ObservationIgnored private var staged: URL?

    init() {
        version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev"
        beta = UserDefaults.standard.bool(forKey: Self.betaKey)
        let source = ProcessInfo.processInfo.environment[Self.sourceVariable]?.trimmingCharacters(in: .whitespaces)
        sourceOverride = source?.isEmpty == false ? source : nil
        installed = Self.installedBundle()
        phase = installed == nil ? .off : .waiting
    }

    var isInstalled: Bool { installed != nil }

    /// Starts the schedule. Called once, by the app at startup. True when an
    /// update an earlier run left waiting has just gone in and the new version
    /// is starting: the caller should do nothing more but quit.
    func start() -> Bool {
        guard installed != nil, schedule == nil else { return false }

        if let pending = pendingUpdate() {
            staged = pending.bundle
            target = pending.version
            phase = .ready
            if install(relaunch: true) { return true }
        }

        schedule = Task { [weak self] in
            try? await Task.sleep(for: Self.firstLook)
            while !Task.isCancelled {
                await self?.check()
                try? await Task.sleep(for: Self.period)
            }
        }
        return false
    }

    /// One look, and a download if it finds something. A request while one is
    /// running is folded into a second look after it, so the beta switch is
    /// always answered with the setting it ended on.
    func check() async {
        guard installed != nil, !handedOver else { return }
        if busy {
            again = true
            return
        }
        busy = true
        defer { busy = false }
        repeat {
            again = false
            await checkOnce()
        } while again
    }

    /// Puts the waiting version in and starts it; the caller then quits. False
    /// when there was nothing to put in or it could not be done.
    func restartToUpdate() -> Bool {
        install(relaunch: true)
    }

    /// Puts the waiting version in as the app leaves.
    func installOnQuit() {
        _ = install(relaunch: false)
    }

    // MARK: - Looking

    private struct Release: Decodable {
        struct Asset: Decodable {
            let name: String
            let browser_download_url: URL
            let digest: String?
        }
        let tag_name: String
        let draft: Bool?
        let prerelease: Bool?
        let assets: [Asset]
    }

    private struct Failure: Error {
        let line: String
        let detail: String
    }

    private func checkOnce() async {
        // Keep "ready" on screen while looking for something newer still.
        if phase != .ready { phase = .checking }
        do {
            let newest = try await newestRelease()
            checkedAt = Date()
            guard let newest, let asset = newest.release.assets.first(where: { $0.name == Self.asset }),
                  newest.version > SemanticVersion(version) ?? .zero
            else {
                if phase != .ready { phase = .upToDate }
                return
            }
            let name = newest.version.text
            if phase == .ready, target == name { return }

            target = name
            phase = .downloading
            staged = try await download(asset, version: name)
            phase = .ready
            failure = nil
            failureDetail = nil
            log.info("Version \(name, privacy: .public) is ready")
        } catch let problem as Failure {
            fail(problem.line, problem.detail)
        } catch {
            fail("Couldn't check for updates", error.localizedDescription)
        }
    }

    private func fail(_ line: String, _ detail: String) {
        log.error("\(line, privacy: .public): \(detail, privacy: .public)")
        // An older download that is still waiting stays offered.
        if staged != nil, let waiting = pendingUpdate() {
            staged = waiting.bundle
            target = waiting.version
            phase = .ready
            return
        }
        staged = nil
        try? FileManager.default.removeItem(at: updatesFolder)
        failure = line
        failureDetail = detail
        phase = .failed
    }

    /// The newest release that counts: the latest stable one, or with beta
    /// versions on, the highest version of either kind.
    private func newestRelease() async throws -> (release: Release, version: SemanticVersion)? {
        let base = sourceOverride ?? Self.api
        guard let url = URL(string: base + (beta ? "/releases?per_page=30" : "/releases/latest")) else {
            throw Failure(line: "Couldn't check for updates", detail: "\(base) is not an address")
        }
        var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 30)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.setValue("usagetop-mac/\(version)", forHTTPHeaderField: "User-Agent")

        let (data, response) = try await URLSession.shared.data(for: request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        // No release at all yet, or a repository that cannot be seen.
        if status == 404 { return nil }
        if status == 403 || status == 429 {
            throw Failure(line: "Couldn't check for updates", detail: "GitHub is limiting requests from this address; it will be tried again later")
        }
        guard status == 200 else {
            throw Failure(line: "Couldn't check for updates", detail: "GitHub answered \(status)")
        }

        let decoder = JSONDecoder()
        let releases = beta ? try decoder.decode([Release].self, from: data) : [try decoder.decode(Release.self, from: data)]
        return releases
            .filter { $0.draft != true }
            .compactMap { release in SemanticVersion(release.tag_name).map { (release, $0) } }
            .max { $0.1 < $1.1 }
    }

    // MARK: - Downloading

    private var updatesFolder: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("usagetop/Updates", isDirectory: true)
    }

    /// Downloads the archive, checks it and leaves the bundle in
    /// Updates/<version>/usagetop.app. Throws what went wrong otherwise.
    private func download(_ asset: Release.Asset, version name: String) async throws -> URL {
        let files = FileManager.default
        let folder = updatesFolder.appendingPathComponent(name, isDirectory: true)
        try? files.removeItem(at: updatesFolder)
        try files.createDirectory(at: folder, withIntermediateDirectories: true)

        var request = URLRequest(url: asset.browser_download_url, timeoutInterval: 120)
        request.setValue("usagetop-mac/\(version)", forHTTPHeaderField: "User-Agent")
        let (temporary, response) = try await URLSession.shared.download(for: request)
        defer { try? files.removeItem(at: temporary) }
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw Failure(line: "Couldn't download \(name)", detail: "The download answered \((response as? HTTPURLResponse)?.statusCode ?? 0)")
        }

        // GitHub lists each file's SHA-256; the testing source may leave it out.
        if let listed = asset.digest, listed.hasPrefix("sha256:") {
            let data = try Data(contentsOf: temporary, options: .mappedIfSafe)
            let actual = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
            guard actual == listed.dropFirst("sha256:".count).lowercased() else {
                throw Failure(line: "The download of \(name) was damaged", detail: "Its SHA-256 is not the one the release lists")
            }
        }

        let unpack = Process()
        unpack.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
        unpack.arguments = ["-x", "-k", temporary.path, folder.path]
        try unpack.run()
        await withCheckedContinuation { done in
            DispatchQueue.global().async {
                unpack.waitUntilExit()
                done.resume()
            }
        }
        let bundle = folder.appendingPathComponent("usagetop.app", isDirectory: true)
        guard unpack.terminationStatus == 0, files.fileExists(atPath: bundle.path) else {
            try? files.removeItem(at: updatesFolder)
            throw Failure(line: "The download of \(name) was damaged", detail: "It did not unpack to usagetop.app")
        }
        if let reason = Self.whyNot(bundle, version: name) {
            try? files.removeItem(at: updatesFolder)
            throw Failure(line: "The download of \(name) was not usable", detail: reason)
        }
        return bundle
    }

    /// Why a bundle must not replace this app, or nil when it may: it has to
    /// be this app, in that version, with its signature intact.
    private static func whyNot(_ bundle: URL, version name: String) -> String? {
        guard let info = NSDictionary(contentsOf: bundle.appendingPathComponent("Contents/Info.plist")) else {
            return "It has no Info.plist"
        }
        guard info["CFBundleIdentifier"] as? String == Bundle.main.bundleIdentifier else {
            return "It is another app"
        }
        guard info["CFBundleShortVersionString"] as? String == name else {
            return "It is not version \(name)"
        }
        var code: SecStaticCode?
        guard SecStaticCodeCreateWithPath(bundle as CFURL, [], &code) == errSecSuccess, let code else {
            return "Its signature could not be read"
        }
        let flags = SecCSFlags(rawValue: kSecCSCheckAllArchitectures | kSecCSStrictValidate | kSecCSCheckNestedCode)
        let status = SecStaticCodeCheckValidity(code, flags, nil)
        guard status == errSecSuccess else {
            let message = SecCopyErrorMessageString(status, nil) as String? ?? "error \(status)"
            return "Its signature is not intact (\(message))"
        }
        return nil
    }

    /// A download an earlier run (or this one) checked and left waiting, if it
    /// is still newer than what is running and still whole. Anything else in
    /// the folder is cleared away.
    private func pendingUpdate() -> (bundle: URL, version: String)? {
        let files = FileManager.default
        let current = SemanticVersion(version) ?? .zero
        let folders = (try? files.contentsOfDirectory(at: updatesFolder, includingPropertiesForKeys: nil)) ?? []
        let found = folders
            .compactMap { folder in SemanticVersion(folder.lastPathComponent).map { (folder, $0) } }
            .filter { $0.1 > current }
            .max { $0.1 < $1.1 }
        if let found {
            let bundle = found.0.appendingPathComponent("usagetop.app", isDirectory: true)
            if Self.whyNot(bundle, version: found.1.text) == nil { return (bundle, found.1.text) }
        }
        if !folders.isEmpty { try? files.removeItem(at: updatesFolder) }
        return nil
    }

    // MARK: - Installing

    /// Swaps the waiting bundle in for the running one. The running copy keeps
    /// working from the files it has open; the next start is the new version.
    private func install(relaunch: Bool) -> Bool {
        guard let installed, let staged, !handedOver else { return false }
        if let reason = Self.whyNot(staged, version: target ?? "") {
            fail("Couldn't install the update", reason)
            self.staged = nil
            try? FileManager.default.removeItem(at: updatesFolder)
            return false
        }
        do {
            _ = try FileManager.default.replaceItemAt(installed, withItemAt: staged)
        } catch {
            fail("Couldn't install the update", error.localizedDescription)
            return false
        }
        handedOver = true
        self.staged = nil
        try? FileManager.default.removeItem(at: updatesFolder)
        log.info("Installed \(self.target ?? "?", privacy: .public) over \(self.version, privacy: .public)")

        if relaunch {
            // Started once this copy has gone, so the two never run side by side.
            let wait = Process()
            wait.executableURL = URL(fileURLWithPath: "/bin/sh")
            wait.arguments = ["-c", "while kill -0 \"$0\" 2>/dev/null; do sleep 0.1; done; exec /usr/bin/open \"$1\"",
                              "\(ProcessInfo.processInfo.processIdentifier)", installed.path]
            try? wait.run()
        }
        return true
    }

    /// This app's bundle, if it is one that may be replaced: an .app directly
    /// in /Applications or ~/Applications that this user can write to. A copy
    /// macOS runs from a randomised path (opened straight from Downloads)
    /// fails the same test.
    private static func installedBundle() -> URL? {
        let bundle = Bundle.main.bundleURL.resolvingSymlinksInPath()
        guard bundle.pathExtension == "app" else { return nil }
        let parent = bundle.deletingLastPathComponent().path
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        guard parent == "/Applications" || parent == home + "/Applications" else { return nil }
        guard FileManager.default.isWritableFile(atPath: parent), FileManager.default.isWritableFile(atPath: bundle.path) else { return nil }
        return bundle
    }
}

/// A version as releases name them: "1.2.1", "v1.2.1", "1.3.0-beta.2". Ordered
/// as SemVer orders them: a pre-release comes before its release.
struct SemanticVersion: Comparable {
    let numbers: [Int]
    let pre: [String]

    static let zero = SemanticVersion(numbers: [0, 0, 0], pre: [])

    private init(numbers: [Int], pre: [String]) {
        self.numbers = numbers
        self.pre = pre
    }

    init?(_ text: String) {
        var text = text.trimmingCharacters(in: .whitespaces)
        if text.hasPrefix("v") { text.removeFirst() }
        if let plus = text.firstIndex(of: "+") { text = String(text[..<plus]) }
        let halves = text.split(separator: "-", maxSplits: 1, omittingEmptySubsequences: false)
        let parts = halves[0].split(separator: ".", omittingEmptySubsequences: false).map { Int($0) }
        guard parts.count == 3, parts.allSatisfy({ $0 != nil && $0! >= 0 }) else { return nil }
        numbers = parts.map { $0! }
        pre = halves.count > 1 ? halves[1].split(separator: ".").map(String.init) : []
        if halves.count > 1, pre.isEmpty { return nil }
    }

    var text: String {
        numbers.map(String.init).joined(separator: ".") + (pre.isEmpty ? "" : "-" + pre.joined(separator: "."))
    }

    static func < (lhs: SemanticVersion, rhs: SemanticVersion) -> Bool {
        if lhs.numbers != rhs.numbers { return lhs.numbers.lexicographicallyPrecedes(rhs.numbers) }
        if lhs.pre.isEmpty || rhs.pre.isEmpty { return !lhs.pre.isEmpty && rhs.pre.isEmpty }
        for (left, right) in zip(lhs.pre, rhs.pre) where left != right {
            switch (Int(left), Int(right)) {
            case let (l?, r?): return l < r
            case (_?, nil): return true
            case (nil, _?): return false
            default: return left < right
            }
        }
        return lhs.pre.count < rhs.pre.count
    }
}

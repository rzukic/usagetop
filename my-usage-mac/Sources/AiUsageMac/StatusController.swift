import AppKit
import OSLog
import SwiftUI

private let log = Logger(subsystem: "io.github.rzukic.usagetop.mac", category: "panel")

/// A borderless panel that can take the keyboard (for the settings fields)
/// without making the app the active one, the way Spotlight's does.
final class GlassPanel: NSPanel {
    /// Escape closes the panel, even while a text field has the keyboard.
    var onCancel: () -> Void = {}

    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }

    override func cancelOperation(_ sender: Any?) {
        onCancel()
    }

    /// ⌘X, ⌘C, ⌘V, ⌘A, ⌘Z and ⇧⌘Z in a text field. An app that lives in the
    /// menu bar has no Edit menu, and it is the Edit menu that normally turns
    /// those keys into cut, copy and paste; without it they would do nothing,
    /// and a sign-in address could only be pasted from the context menu.
    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        if event.type == .keyDown, let action = Self.editing(event), NSApp.sendAction(action, to: nil, from: self) {
            return true
        }
        return super.performKeyEquivalent(with: event)
    }

    private static func editing(_ event: NSEvent) -> Selector? {
        let flags = event.modifierFlags.intersection([.command, .shift, .option, .control])
        guard flags == .command || flags == [.command, .shift] else { return nil }
        let shift = flags.contains(.shift)
        switch event.charactersIgnoringModifiers?.lowercased() {
        case "x" where !shift: return #selector(NSText.cut(_:))
        case "c" where !shift: return #selector(NSText.copy(_:))
        case "v" where !shift: return #selector(NSText.paste(_:))
        case "a" where !shift: return #selector(NSText.selectAll(_:))
        case "z": return shift ? Selector(("redo:")) : Selector(("undo:"))
        default: return nil
        }
    }
}

/// Owns the status item and the panel. The panel is a sheet of Liquid Glass
/// (`NSGlassEffectView`) hung under the status item; it closes on a click
/// anywhere else, on escape, and on a second click of the item.
@MainActor
final class StatusController: NSObject {
    private let model: AppModel
    private let statusItem: NSStatusItem
    private let panel: GlassPanel
    private let hosting: NSHostingView<AnyView>
    private var outsideClicks: Any?
    private var modifierWatch: Any?
    private var labelTask: Task<Void, Never>?
    private var desiredHeight: CGFloat = 300

    static let cornerRadius: CGFloat = 26
    private static let gap: CGFloat = 6
    /// Taller than this, the page scrolls rather than the panel growing.
    private static let maxHeight: CGFloat = 720

    init(model: AppModel) {
        self.model = model
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)

        panel = GlassPanel(
            contentRect: NSRect(x: 0, y: 0, width: PanelView.width, height: 300),
            styleMask: [.borderless, .nonactivatingPanel, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        hosting = NSHostingView(rootView: AnyView(EmptyView()))
        super.init()

        hosting.rootView = AnyView(
            PanelView(
                onHeightChange: { [weak self] height in self?.resize(to: height) },
                onClose: { [weak self] in self?.close() }
            )
            .environment(model)
        )
        hosting.sizingOptions = []

        // The SwiftUI panel is the window's content itself, with nothing in
        // between: the glass is drawn by the panel (`PanelGlass`). An AppKit
        // glass view between window and content let AppKit's layout shrink the
        // content below the window on resize, and the panel drifted off it.
        // Clipped to the corners, so a borderless window never shows its
        // square edge.
        hosting.wantsLayer = true
        hosting.layer?.backgroundColor = NSColor.clear.cgColor
        hosting.layer?.cornerRadius = Self.cornerRadius
        hosting.layer?.cornerCurve = .continuous
        hosting.layer?.masksToBounds = true
        panel.contentView = hosting
        hosting.postsFrameChangedNotifications = true
        NotificationCenter.default.addObserver(
            self, selector: #selector(contentFrameChanged), name: NSView.frameDidChangeNotification, object: hosting
        )
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.level = .popUpMenu
        panel.isFloatingPanel = true
        panel.hidesOnDeactivate = false
        panel.isMovable = false
        panel.isReleasedWhenClosed = false
        panel.animationBehavior = .none
        panel.onCancel = { [weak self] in self?.close() }
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .transient, .ignoresCycle]

        if let button = statusItem.button {
            button.target = self
            button.action = #selector(toggle)
            button.sendAction(on: [.leftMouseUp, .rightMouseUp])
            button.imagePosition = .imageLeading
            button.setAccessibilityTitle("usagetop")
        }

        NotificationCenter.default.addObserver(
            self, selector: #selector(panelResigned), name: NSWindow.didResignKeyNotification, object: panel
        )
        NSWorkspace.shared.notificationCenter.addObserver(
            self, selector: #selector(spaceChanged), name: NSWorkspace.activeSpaceDidChangeNotification, object: nil
        )

        updateLabel()
    }

    func start() {
        model.start()
        labelTask = Task { [weak self] in
            // Follow the model rather than poll it: redraw whenever anything the
            // label reads changes.
            while !Task.isCancelled {
                guard let self else { return }
                await withCheckedContinuation { continuation in
                    withObservationTracking {
                        self.updateLabel()
                    } onChange: {
                        continuation.resume()
                    }
                }
            }
        }
        if CommandLine.arguments.contains("--open") {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) { [weak self] in self?.open() }
        }
        if let index = CommandLine.arguments.firstIndex(of: "--showcase"), index + 2 < CommandLine.arguments.count {
            let folder = URL(fileURLWithPath: CommandLine.arguments[index + 1])
            let walls = CommandLine.arguments[(index + 2)...].prefix { !$0.hasPrefix("--") }
            Task { await Showcase.render(model: model, to: folder, wallpapers: Array(walls)) }
        }
        if let index = CommandLine.arguments.firstIndex(of: "--live-shot"), index + 3 < CommandLine.arguments.count {
            let args = CommandLine.arguments
            Task { await liveShots(dark: args[index + 1], light: args[index + 2], folder: URL(fileURLWithPath: args[index + 3])) }
        }
        if let index = CommandLine.arguments.firstIndex(of: "--snapshot"), index + 1 < CommandLine.arguments.count {
            let folder = URL(fileURLWithPath: CommandLine.arguments[index + 1])
            Task { await snapshots(to: folder) }
        }
    }

    // MARK: Showing and hiding

    /// Whether the panel is meant to be showing. Not `isVisible`: during the
    /// fade-out the panel is still visible, and a click then means "open".
    private var isOpen = false
    /// Bumped on every open and close, so a fade-out that finishes after the
    /// panel was opened again does not hide it.
    private var generation = 0
    /// Where the status item was when the panel opened; resizing keeps to it.
    private var anchor: NSRect?

    @objc private func toggle() {
        isOpen ? close() : open()
    }

    private func open() {
        guard !isOpen, let button = statusItem.button, let buttonWindow = button.window else { return }
        isOpen = true
        generation += 1
        Task { await model.refresh() }

        let anchor = buttonWindow.convertToScreen(button.convert(button.bounds, to: nil))
        self.anchor = anchor
        panel.setFrame(frame(height: desiredHeight, anchor: anchor), display: false)
        panel.contentView?.layoutSubtreeIfNeeded()
        panel.alphaValue = 0
        panel.makeKeyAndOrderFront(nil)
        button.highlight(true)
        // The shadow follows the drawn shape, so take it once there is one.
        DispatchQueue.main.async { [weak self] in self?.panel.invalidateShadow() }

        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.18
            context.timingFunction = CAMediaTimingFunction(name: .easeOut)
            panel.animator().alphaValue = 1
        }

        outsideClicks = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown]) { [weak self] _ in
            Task { @MainActor in self?.close() }
        }
        // ⌘ held shows the sidebar's shortcuts.
        modifierWatch = NSEvent.addLocalMonitorForEvents(matching: .flagsChanged) { [weak self] event in
            let held = event.modifierFlags.intersection(.deviceIndependentFlagsMask) == .command
            MainActor.assumeIsolated {
                if self?.model.commandHeld != held { self?.model.commandHeld = held }
            }
            return event
        }
    }

    private func close() {
        // While posing for `--live-shot`, focus moving to the backdrop is not
        // the user leaving the panel.
        guard isOpen, shotScreen == nil else { return }
        isOpen = false
        generation += 1
        let closing = generation
        if let outsideClicks { NSEvent.removeMonitor(outsideClicks) }
        outsideClicks = nil
        if let modifierWatch { NSEvent.removeMonitor(modifierWatch) }
        modifierWatch = nil
        model.commandHeld = false
        statusItem.button?.highlight(false)

        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.14
            panel.animator().alphaValue = 0
        } completionHandler: { [weak self] in
            Task { @MainActor in
                guard let self, self.generation == closing else { return }
                self.panel.orderOut(nil)
            }
        }
    }

    /// The content always fills the window. Should anything in AppKit's layout
    /// size it otherwise, it is put back and the event logged
    /// (`log show --predicate 'subsystem == "io.github.rzukic.usagetop.mac"'`).
    @objc private func contentFrameChanged() {
        let wanted = NSRect(origin: .zero, size: panel.frame.size)
        guard hosting.frame != wanted else { return }
        log.error("content \(NSStringFromRect(self.hosting.frame), privacy: .public) drifted from window \(NSStringFromRect(self.panel.frame), privacy: .public); restoring")
        hosting.frame = wanted
    }

    @objc private func panelResigned() {
        // Clicking another app's window takes the key away; the panel goes with it.
        close()
    }

    @objc private func spaceChanged() {
        close()
    }

    /// Every page of the panel drawn to a PNG, for checking the layout without
    /// a screen (`--demo --snapshot <folder>`). Glass is the compositor's, so
    /// it is stood in for by a flat backdrop.
    private func snapshots(to folder: URL) async {
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try? await Task.sleep(for: .seconds(1))
        // The menu bar item, one to four limits, on a dark and a light bar.
        for (name, appearance, bar) in [("dark", NSAppearance.Name.darkAqua, NSColor(white: 0.12, alpha: 1)),
                                        ("light", .aqua, NSColor(white: 0.93, alpha: 1))] {
            let images = (1...Settings.maxMenuBarLimits).map { count in
                MenuBarLimits.image(model.mostAtRisk(count).map { ($0.usage.providerKey, $0.usage.accountLabel, $0.window) }, stale: false)
            }
            let width = (images.map(\.size.width).max() ?? 100) + 24
            let height = CGFloat(images.count) * 30 + 8
            let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(width * 3), pixelsHigh: Int(height * 3),
                                       bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                       colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
            rep.size = NSSize(width: width, height: height)
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
            NSAppearance(named: appearance)!.performAsCurrentDrawingAppearance {
                bar.setFill()
                NSRect(x: 0, y: 0, width: width, height: height).fill()
                // A template takes the bar's colour; stand in for the bar here.
                let ink: NSColor = appearance == .darkAqua ? .white : .black
                for (index, image) in images.enumerated() {
                    let inked = NSImage(size: image.size, flipped: false) { rect in
                        image.draw(in: rect)
                        ink.set()
                        rect.fill(using: .sourceAtop)
                        return true
                    }
                    inked.draw(in: NSRect(x: 12, y: height - 4 - CGFloat(index + 1) * 30 + 4, width: image.size.width, height: image.size.height))
                }
            }
            NSGraphicsContext.restoreGraphicsState()
            try? rep.representation(using: .png, properties: [:])?.write(to: folder.appendingPathComponent("menubar-\(name).png"))
        }

        var pages: [(String, Pane)] = [("overview", .overview), ("settings", .settings)]
        for usage in model.railOrder { pages.append((usage.account, .account(usage.account))) }
        for scheme in [ColorScheme.dark, .light] {
            for (name, pane) in pages {
                model.pane = pane
                try? await Task.sleep(for: .milliseconds(300))
                let backdrop = scheme == .dark
                    ? [Color(hex: 0x1B2735), Color(hex: 0x2A1F33), Color(hex: 0x14161A)]
                    : [Color(hex: 0xDCE6F2), Color(hex: 0xEDE3EE), Color(hex: 0xF4F4F6)]
                let view = PanelView(flat: true)
                    .environment(model)
                    .background {
                        RoundedRectangle(cornerRadius: Self.cornerRadius, style: .continuous)
                            .fill(LinearGradient(colors: backdrop, startPoint: .topLeading, endPoint: .bottomTrailing))
                            .overlay(
                                RoundedRectangle(cornerRadius: Self.cornerRadius, style: .continuous)
                                    .fill(scheme == .dark ? Color.black.opacity(0.35) : Color.white.opacity(0.45))
                            )
                    }
                    .padding(24)
                    .background(scheme == .dark ? Color(hex: 0x0B0D12) : Color(hex: 0xC9D3DE))
                    .environment(\.colorScheme, scheme)
                let renderer = ImageRenderer(content: view)
                renderer.scale = 2
                guard let image = renderer.nsImage, let tiff = image.tiffRepresentation,
                      let rep = NSBitmapImageRep(data: tiff) else { continue }
                let suffix = scheme == .dark ? "dark" : "light"
                try? rep.representation(using: .png, properties: [:])?
                    .write(to: folder.appendingPathComponent("\(name)-\(suffix).png"))
            }
        }
        NSApplication.shared.terminate(nil)
    }

    // MARK: Size and place

    /// Under the status item, top edge fixed, nudged inside the screen.
    private func frame(height: CGFloat, anchor: NSRect) -> NSRect {
        let screen = shotScreen ?? statusItem.button?.window?.screen ?? NSScreen.main
        let visible = screen?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        let width = PanelView.width
        let h = min(height, Self.maxHeight, visible.height - Self.gap * 2)
        var x = anchor.midX - width / 2
        x = min(max(x, visible.minX + 8), visible.maxX - width - 8)
        let top = min(anchor.minY - Self.gap, visible.maxY)
        return NSRect(x: x.rounded(), y: (top - h).rounded(), width: width, height: h.rounded())
    }

    private func resize(to height: CGFloat) {
        guard abs(height - desiredHeight) > 0.5 else { return }
        desiredHeight = height
        guard isOpen, let anchor else { return }
        // Set at once, never animated: an animated frame that another resize
        // lands on mid-way leaves the content out of step with the window.
        // While open the panel only grows: a shorter page (Settings, an
        // account with one window) keeps the height and leaves room below,
        // rather than the whole panel jumping up. It sizes afresh on opening.
        let target = frame(height: max(height, panel.frame.height), anchor: anchor)
        guard target != panel.frame else { return }
        panel.setFrame(target, display: true, animate: false)
        log.notice("resize to \(height, privacy: .public): window \(NSStringFromRect(self.panel.frame), privacy: .public) content \(NSStringFromRect(self.hosting.frame), privacy: .public)")
        contentFrameChanged()
        panel.invalidateShadow()
    }

    // MARK: Pictures of the real panel

    /// The screen the panel is pictured on, in `--live-shot`; nil otherwise.
    private var shotScreen: NSScreen?

    /// The real panel, real Liquid Glass and all, over a wallpaper of our own,
    /// for pictures (`--demo --live-shot <dark wallpaper> <light wallpaper> <folder>`).
    /// The window server draws glass only on screen, so a picture of it has to
    /// be a screen capture, and this app may not take one. So it sets each pose
    /// up on the sharpest screen, writes `<pose>.rect` (the area to capture, in
    /// `screencapture -R` coordinates) into the folder, and waits for
    /// `<pose>.done` from whoever takes the picture.
    private func liveShots(dark: String, light: String, folder: URL) async {
        guard let screen = NSScreen.screens.max(by: { $0.backingScaleFactor < $1.backingScaleFactor }),
              let main = NSScreen.screens.first else { return }
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try? await Task.sleep(for: .seconds(1))
        shotScreen = screen

        // Above the real menu bar, so none of its items get into the picture,
        // and below the panel: the panel's glass is drawn over it for real.
        let backdrop = NSWindow(contentRect: screen.frame, styleMask: [.borderless], backing: .buffered, defer: false)
        backdrop.level = NSWindow.Level(rawValue: NSWindow.Level.statusBar.rawValue + 1)
        backdrop.hasShadow = false
        backdrop.ignoresMouseEvents = true
        let bar = screen.frame.maxY - screen.visibleFrame.maxY
        let wall = ShotBackdrop(frame: NSRect(origin: .zero, size: screen.frame.size), barHeight: bar)
        backdrop.contentView = wall
        backdrop.setFrame(screen.frame, display: true)
        let level = panel.level
        panel.level = NSWindow.Level(rawValue: backdrop.level.rawValue + 1)

        let account = model.ordered.first(where: \.isOK)?.account ?? "claude"
        let poses: [(name: String, look: NSAppearance.Name, wall: String, pane: Pane)] = [
            ("dark-overview", .darkAqua, dark, .overview), ("dark-account", .darkAqua, dark, .account(account)),
            ("light-overview", .aqua, light, .overview), ("light-account", .aqua, light, .account(account)),
        ]
        for pose in poses {
            NSApp.appearance = NSAppearance(named: pose.look)
            wall.wallpaper = NSImage(contentsOfFile: pose.wall)
            wall.dark = pose.look == .darkAqua
            wall.item = statusItem.button?.image
            model.pane = pose.pane
            try? await Task.sleep(for: .milliseconds(400))
            wall.item = statusItem.button?.image
            backdrop.orderFrontRegardless()
            // Drawn now, not later: the panel is placed by where the item lands.
            wall.display()

            let visible = screen.visibleFrame
            // Under the drawn item, as the panel hangs under the real one.
            let item = backdrop.convertToScreen(wall.convert(wall.itemRect, to: nil))
            anchor = NSRect(x: item.minX, y: visible.maxY, width: item.width, height: item.height)
            isOpen = true
            panel.alphaValue = 1
            panel.setFrame(frame(height: desiredHeight, anchor: anchor!), display: true)
            panel.orderFrontRegardless()
            try? await Task.sleep(for: .seconds(1.5))
            panel.setFrame(frame(height: max(desiredHeight, 300), anchor: anchor!), display: true)
            panel.invalidateShadow()
            try? await Task.sleep(for: .seconds(1.5))

            // The drawn menu bar from the item rightwards, and the panel with
            // its shadow under it.
            let left = min(panel.frame.minX, item.minX) - 60
            // Short of the top-right corner, where the system's own privacy
            // indicators sit above every window.
            let area = NSRect(x: left, y: panel.frame.minY - 70, width: screen.frame.maxX - 30 - left,
                              height: screen.frame.maxY - (panel.frame.minY - 70)).intersection(screen.frame)
            let top = main.frame.maxY - area.maxY
            let rect = "\(Int(area.minX)),\(Int(top)),\(Int(area.width)),\(Int(area.height))"
            try? rect.write(to: folder.appendingPathComponent("\(pose.name).rect"), atomically: true, encoding: .utf8)
            let done = folder.appendingPathComponent("\(pose.name).done")
            for _ in 0..<120 where !FileManager.default.fileExists(atPath: done.path) {
                try? await Task.sleep(for: .milliseconds(250))
            }
            panel.orderOut(nil)
            isOpen = false
        }
        backdrop.orderOut(nil)
        panel.level = level
        NSApplication.shared.terminate(nil)
    }

    // MARK: The menu bar item

    private func updateLabel() {
        guard let button = statusItem.button else { return }

        let limits = model.mostAtRisk(model.settings.menuBarLimits)
        if !limits.isEmpty {
            button.image = MenuBarLimits.image(limits.map { ($0.usage.providerKey, $0.usage.accountLabel, $0.window) }, stale: model.problem != nil)
            button.title = ""
            button.imagePosition = .imageOnly
            button.toolTip = "usagetop, closest to running out:\n" + limits.map { entry in
                let reset = Format.reset(entry.window.resetDate).map { ", resets in \($0)" } ?? ""
                return "\(entry.usage.displayName) \(entry.window.displayLabel): \(Format.percent(entry.window.remainingPercent ?? 0)) left\(reset)"
            }.joined(separator: "\n")
        } else if let percent = model.menuBarPercent {
            button.image = StatusRing.image(percent: percent, stale: model.problem != nil)
            if model.settings.showsPercentInMenuBar {
                button.title = Format.percent(percent)
                button.font = .monospacedDigitSystemFont(ofSize: 12, weight: .medium)
                button.imagePosition = .imageLeading
            } else {
                button.title = ""
                button.imagePosition = .imageOnly
            }
            button.toolTip = "usagetop: \(Format.percent(percent)) left combined"
        } else {
            let image = NSImage(systemSymbolName: "gauge.with.dots.needle.33percent", accessibilityDescription: "usagetop")
            image?.isTemplate = true
            button.image = image
            button.title = ""
            button.imagePosition = .imageOnly
            button.toolTip = model.problem ?? "usagetop"
        }
    }
}

/// The ring drawn into an `NSImage`, because a status item button takes an
/// image rather than a view. A template, like the limits: the track faint, the
/// arc solid, both in the menu bar's own colour.
enum StatusRing {
    static func image(percent: Double, stale: Bool = false, size: CGFloat = 16) -> NSImage {
        let image = NSImage(size: NSSize(width: size, height: size), flipped: false) { rect in
            let lineWidth: CGFloat = 2.4
            let inset = lineWidth / 2 + 0.75
            let ring = rect.insetBy(dx: inset, dy: inset)

            let track = NSBezierPath(ovalIn: ring)
            track.lineWidth = lineWidth
            NSColor.black.withAlphaComponent(0.3).setStroke()
            track.stroke()

            let fraction = max(0.001, min(1, percent / 100))
            let arc = NSBezierPath()
            arc.appendArc(
                withCenter: NSPoint(x: rect.midX, y: rect.midY),
                radius: ring.width / 2,
                startAngle: 90,
                endAngle: 90 - 360 * fraction,
                clockwise: true
            )
            arc.lineWidth = lineWidth
            arc.lineCapStyle = .round
            NSColor.black.withAlphaComponent(stale ? 0.5 : 1).setStroke()
            // At 0% only the track: a round cap on a zero-length arc is a dot.
            if percent > 0 { arc.stroke() }
            return true
        }
        image.isTemplate = true
        return image
    }
}

/// The panel on a desktop: a wallpaper, a menu bar, and the panel hung under
/// its item, for judging the look without a screen to capture
/// (`--demo --showcase <folder> <wallpaper>...`). The window server draws the
/// real glass; here it is stood in for by the same wallpaper, blurred and tinted.
@MainActor
enum Showcase {
    static let canvas = CGSize(width: 980, height: 1120)

    static func render(model: AppModel, to folder: URL, wallpapers: [String]) async {
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try? await Task.sleep(for: .seconds(1))
        var pages: [(String, Pane)] = [("overview", .overview)]
        if let first = model.ordered.first(where: \.isOK) { pages.append((first.account, .account(first.account))) }

        for path in wallpapers {
            guard let wall = NSImage(contentsOfFile: path) else { continue }
            let wallName = URL(fileURLWithPath: path).deletingPathExtension().lastPathComponent
            for scheme in [ColorScheme.dark, .light] {
                for (name, pane) in pages {
                    model.pane = pane
                    try? await Task.sleep(for: .milliseconds(300))
                    let renderer = ImageRenderer(content: desktop(model: model, wall: wall, scheme: scheme))
                    renderer.scale = 2
                    guard let image = renderer.nsImage, let tiff = image.tiffRepresentation,
                          let rep = NSBitmapImageRep(data: tiff) else { continue }
                    let file = "\(wallName)-\(name)-\(scheme == .dark ? "dark" : "light").png"
                    try? rep.representation(using: .png, properties: [:])?.write(to: folder.appendingPathComponent(file))
                }
            }
        }
        NSApplication.shared.terminate(nil)
    }

    private static func wallpaper(_ wall: NSImage) -> some View {
        Image(nsImage: wall)
            .resizable()
            .aspectRatio(contentMode: .fill)
            .frame(width: canvas.width, height: canvas.height)
            .clipped()
    }

    private static func desktop(model: AppModel, wall: NSImage, scheme: ColorScheme) -> some View {
        let dark = scheme == .dark
        let radius: CGFloat = 26
        return ZStack(alignment: .topTrailing) {
            wallpaper(wall)
                .overlay(dark ? Color.black.opacity(0.28) : Color.clear)

            VStack(alignment: .trailing, spacing: 6) {
                // The menu bar, with the app's item highlighted as it is while open.
                HStack(spacing: 18) {
                    Image(systemName: "wifi")
                    Image(systemName: "battery.75percent")
                    HStack(spacing: 4) {
                        Ring(percent: model.menuBarPercent, size: 15, lineWidth: 2.4) { EmptyView() }
                        Text(Format.percent(model.menuBarPercent ?? 0))
                            .font(.system(size: 13, weight: .medium))
                            .monospacedDigit()
                    }
                    .padding(.horizontal, 8)
                    .padding(.vertical, 3)
                    .background(.primary.opacity(0.14), in: Capsule())
                    Image(systemName: "magnifyingglass")
                    Text("Tue 17 Nov  16:42")
                        .font(.system(size: 13, weight: .medium))
                }
                .font(.system(size: 13, weight: .medium))
                .padding(.horizontal, 16)
                .frame(maxWidth: .infinity, alignment: .trailing)
                .frame(height: 30)
                .background(dark ? Color.black.opacity(0.18) : Color.white.opacity(0.18))

                PanelView(flat: true)
                    .environment(model)
                    .background {
                        GeometryReader { proxy in
                            let origin = proxy.frame(in: .named("desktop")).origin
                            wallpaper(wall)
                                .offset(x: -origin.x, y: -origin.y)
                                .blur(radius: 22)
                                .saturation(1.4)
                                .overlay(dark ? Color.black.opacity(0.52) : Color.white.opacity(0.58))
                        }
                        .clipShape(RoundedRectangle(cornerRadius: radius, style: .continuous))
                    }
                    .overlay {
                        RoundedRectangle(cornerRadius: radius, style: .continuous)
                            .strokeBorder(
                                LinearGradient(colors: [.white.opacity(dark ? 0.35 : 0.8), .white.opacity(0.08)],
                                               startPoint: .topLeading, endPoint: .bottomTrailing),
                                lineWidth: 1
                            )
                    }
                    .shadow(color: .black.opacity(0.35), radius: 30, y: 14)
                    .padding(.trailing, 150)
            }
        }
        .frame(width: canvas.width, height: canvas.height, alignment: .top)
        .coordinateSpace(.named("desktop"))
        .environment(\.colorScheme, scheme)
        .foregroundStyle(.primary)
    }
}

/// The limits closest to running out, for the menu bar: each one a provider's
/// mark the height of the bar, and beside it the limit's name over what is
/// left. An account with a name of its own (a second Claude, "Work") wears its
/// initials in a badge on the mark's corner, or two of one provider could not
/// be told apart. One template image, without a hue: the menu bar can be any
/// colour, and as a template the system draws it in the bar's own, as it draws
/// its own icons. The name is drawn a little fainter than the figure.
@MainActor
enum MenuBarLimits {
    private static let height: CGFloat = 22
    private static let mark: CGFloat = 16
    private static let markGap: CGFloat = 4
    private static let entryGap: CGFloat = 10

    private static let labelFont = NSFont.systemFont(ofSize: 9, weight: .semibold)
    private static let figureFont = NSFont.monospacedDigitSystemFont(ofSize: 11, weight: .semibold)

    static func image(_ entries: [(provider: String, account: String?, window: UsageWindow)], stale: Bool) -> NSImage {
        // "Fable" rather than "Fable weekly"; but two windows of one model of
        // one account both showing keep their full names, so they can be told apart.
        let compact = entries.map { WindowName.compact($0.window.label) }
        let laidOut = entries.enumerated().map { index, entry -> (NSImage?, String, String, String?, CGFloat) in
            let level = entry.window.remainingPercent ?? 0
            let clashes = entries.indices.contains { other in
                other != index && entries[other].provider == entry.provider
                    && entries[other].account == entry.account && compact[other] == compact[index]
            }
            let label = short(clashes ? entry.window.displayLabel : compact[index])
            let figure = Format.percent(level)
            let width = max(
                (label as NSString).size(withAttributes: [.font: labelFont]).width,
                (figure as NSString).size(withAttributes: [.font: figureFont]).width
            ).rounded(.up)
            return (Marks.image(entry.provider), label, figure, entry.account.map(AccountBadge.initials), width)
        }
        let total = laidOut.reduce(0) { $0 + mark + markGap + $1.4 } + entryGap * CGFloat(max(laidOut.count - 1, 0))

        let image = NSImage(size: NSSize(width: max(total, 1), height: height), flipped: true) { _ in
            let alpha: CGFloat = stale ? 0.55 : 1
            var x: CGFloat = 0
            for (markImage, label, figure, initials, width) in laidOut {
                // respectFlipped: this canvas is flipped, and the marks are not symmetric.
                let markRect = NSRect(x: x, y: (height - mark) / 2, width: mark, height: mark)
                markImage?.draw(in: markRect, from: .zero, operation: .sourceOver, fraction: alpha,
                                respectFlipped: true, hints: nil)
                if let initials {
                    AccountBadge.draw(initials, onCornerOf: markRect, alpha: alpha)
                }
                let textX = x + mark + markGap
                (label as NSString).draw(at: NSPoint(x: textX, y: 0), withAttributes: [
                    .font: labelFont,
                    .foregroundColor: NSColor.black.withAlphaComponent(0.8 * alpha),
                ])
                (figure as NSString).draw(at: NSPoint(x: textX, y: 8.5), withAttributes: [
                    .font: figureFont,
                    .foregroundColor: NSColor.black.withAlphaComponent(alpha),
                ])
                x = textX + width + entryGap
            }
            return true
        }
        image.isTemplate = true
        image.accessibilityDescription = entries.map { entry in
            let whose = Provider(key: entry.provider).displayName + (entry.account.map { " \($0)" } ?? "")
            return "\(whose) \(entry.window.displayLabel) \(Format.percent(entry.window.remainingPercent ?? 0)) left"
        }
            .joined(separator: ", ")
        return image
    }

    /// "Fable weekly" stays; a longer name is cut so one entry cannot crowd the bar.
    private static func short(_ label: String, to length: Int = 12) -> String {
        label.count <= length ? label : String(label.prefix(length - 1)) + "…"
    }
}


/// An account's initials in a small badge on a mark's bottom-right corner, as
/// T3 marks a project: "RZ", "W" for "Work", "CP" for "Claude Pro". Drawn into
/// a template image, so it is the bar's ink with the letters cut out of it,
/// and a thin gap is cut round it so it stands apart from the mark.
@MainActor
enum AccountBadge {
    private static let font = NSFont.systemFont(ofSize: 7, weight: .heavy)
    private static let badgeHeight: CGFloat = 9

    /// Up to two letters, in the case they were written in: the first of each
    /// of the first two words, a short name ("RZ") as it is, or of a single
    /// word its first; "#2" stays as it is. The other apps follow the same rule.
    nonisolated static func initials(_ name: String) -> String {
        let words = name.split(whereSeparator: { $0 == " " || $0 == "-" || $0 == "_" || $0 == "·" }).map(String.init)
        if words.count >= 2 { return String(words[0].prefix(1)) + String(words[1].prefix(1)) }
        guard let word = words.first else { return "" }
        return word.count <= 2 ? word : String(word.prefix(word.first?.isNumber == true || word.first == "#" ? 3 : 1))
    }

    static func draw(_ initials: String, onCornerOf mark: NSRect, alpha: CGFloat) {
        guard !initials.isEmpty, let context = NSGraphicsContext.current?.cgContext else { return }
        let text = initials as NSString
        let textSize = text.size(withAttributes: [.font: font])
        let width = max(badgeHeight, (textSize.width + 3.5).rounded(.up))
        // Hangs a little past the mark's corner, as T3's does.
        let rect = NSRect(x: mark.maxX - width + 3, y: mark.maxY - badgeHeight + 2.5, width: width, height: badgeHeight)
        let shape = NSBezierPath(roundedRect: rect, xRadius: badgeHeight / 2, yRadius: badgeHeight / 2)

        // The gap round it, then the badge, then the letters cut out of it.
        context.saveGState()
        context.setBlendMode(.clear)
        NSBezierPath(roundedRect: rect.insetBy(dx: -1.2, dy: -1.2), xRadius: badgeHeight / 2 + 1.2, yRadius: badgeHeight / 2 + 1.2).fill()
        context.restoreGState()

        NSColor.black.withAlphaComponent(alpha).setFill()
        shape.fill()

        context.saveGState()
        context.setBlendMode(.destinationOut)
        text.draw(at: NSPoint(x: rect.midX - textSize.width / 2, y: rect.midY - textSize.height / 2 - 0.25),
                  withAttributes: [.font: font, .foregroundColor: NSColor.black])
        context.restoreGState()
    }
}

/// What `--live-shot` puts behind the panel: a wallpaper, and over its top a
/// menu bar of its own, with the app's real item in it and a few of the
/// system's, so the picture shows the item without anyone's own menu bar.
final class ShotBackdrop: NSView {
    var wallpaper: NSImage?
    var item: NSImage?
    var dark = true
    private let barHeight: CGFloat
    /// Where the item is drawn, in this view's coordinates.
    private(set) var itemRect = NSRect.zero

    init(frame: NSRect, barHeight: CGFloat) {
        self.barHeight = barHeight
        super.init(frame: frame)
    }

    required init?(coder: NSCoder) { fatalError() }

    override func draw(_ dirtyRect: NSRect) {
        wallpaper?.draw(in: bounds)
        let ink: NSColor = dark ? .white : .black
        let midY = bounds.maxY - barHeight / 2
        var x = bounds.maxX - 46

        let clock = NSAttributedString(string: "Tue Nov 17  4:42 PM", attributes: [
            .font: NSFont.systemFont(ofSize: 13, weight: .medium), .foregroundColor: ink,
        ])
        x -= clock.size().width
        clock.draw(at: NSPoint(x: x, y: midY - clock.size().height / 2))

        for symbol in ["switch.2", "wifi", "battery.75percent"] {
            guard let image = NSImage(systemSymbolName: symbol, accessibilityDescription: nil)?
                .withSymbolConfiguration(.init(pointSize: 14, weight: .regular)) else { continue }
            x -= 22 + image.size.width
            Self.tinted(image, ink).draw(in: NSRect(x: x, y: midY - image.size.height / 2, width: image.size.width, height: image.size.height))
        }

        guard let item else { return }
        x -= 22 + item.size.width
        itemRect = NSRect(x: x - 7, y: bounds.maxY - barHeight + 4, width: item.size.width + 14, height: barHeight - 8)
        // Highlighted, as the item is while its panel is open.
        ink.withAlphaComponent(0.16).setFill()
        NSBezierPath(roundedRect: itemRect, xRadius: itemRect.height / 2, yRadius: itemRect.height / 2).fill()
        Self.tinted(item, ink).draw(in: NSRect(x: x, y: midY - item.size.height / 2, width: item.size.width, height: item.size.height))
    }

    /// A template image in the bar's colour, as the menu bar draws one.
    private static func tinted(_ image: NSImage, _ color: NSColor) -> NSImage {
        NSImage(size: image.size, flipped: false) { rect in
            image.draw(in: rect)
            color.set()
            rect.fill(using: .sourceAtop)
            return true
        }
    }
}

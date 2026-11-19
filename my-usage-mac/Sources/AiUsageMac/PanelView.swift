import AppKit
import SwiftUI

/// Which page of the panel is showing.
enum Pane: Hashable {
    case overview
    case account(String)
    case settings
}

/// The panel: a rail of subscriptions down the left, the page on the right.
/// The whole panel sits on one sheet of Liquid Glass (see `GlassPanel`);
/// controls and content sit on it as soft fills, never a second layer of glass.
struct PanelView: View {
    @Environment(AppModel.self) private var model
    /// Tells the window how tall the panel wants to be.
    var onHeightChange: (CGFloat) -> Void = { _ in }
    var onClose: () -> Void = {}
    /// Lays the page out in full, without scrolling: for drawing it to a file,
    /// where a scroll view would come out empty.
    var flat = false

    @State private var contentHeight: CGFloat = 200
    @State private var headerHeight: CGFloat = 58
    @State private var footerHeight: CGFloat = 50
    @Namespace private var glass

    static let width: CGFloat = 424

    private var pane: Pane {
        get { model.pane }
        nonmutating set { model.pane = newValue }
    }

    /// How tall the panel would like to be: everything shown without
    /// scrolling, and never shorter than the rail. The window takes this,
    /// capped to its screen; the scroll view takes up whatever does not fit.
    /// Each part is measured on its own, never the window, so the number does
    /// not depend on the size it is asking for.
    private var wantedHeight: CGFloat {
        let rail = 150 + 48 * CGFloat(model.railOrder.count)
        return max(headerHeight + max(contentHeight, 60) + footerHeight, rail).rounded(.up)
    }

    var body: some View {
        HStack(alignment: .top, spacing: 0) {
            rail
            VStack(alignment: .leading, spacing: 0) {
                header
                    .padding(.horizontal, 14)
                    .padding(.top, 14)
                    .padding(.bottom, 10)
                    .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { headerHeight = $0 }

                if flat {
                    pageStack
                } else {
                    ScrollViewReader { scroller in
                        ScrollView(.vertical) {
                            pageStack
                                .id("top")
                                .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { contentHeight = $0 }
                        }
                        .scrollIndicators(.never)
                        .scrollEdgeEffectStyle(.soft, for: .all)
                        .scrollBounceBehavior(.basedOnSize)
                        // One scroll view serves every page; a new page starts at its top.
                        .onChange(of: pane) { scroller.scrollTo("top", anchor: .top) }
                    }
                    .frame(maxHeight: .infinity, alignment: .top)
                }

                footer
                    .padding(.horizontal, 14)
                    .padding(.vertical, 12)
                    .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { footerHeight = $0 }
            }
        }
        .frame(width: Self.width)
        .modifier(FlatOrFilling(flat: flat))
        .modifier(PanelGlass(enabled: !flat))
        .onChange(of: wantedHeight, initial: true) { _, height in onHeightChange(height) }
        .onChange(of: model.snapshot?.usages.map(\.account)) { _, accounts in
            // An account that went away takes its page with it.
            if case .account(let key) = pane, !(accounts ?? []).contains(key) { pane = .overview }
        }
        .background {
            // Keyboard shortcuts with nowhere to be seen.
            Group {
                Button("") { Task { await model.refresh(force: true) } }.keyboardShortcut("r")
                Button("") { pane = pane == .settings ? .overview : .settings }.keyboardShortcut(",")
                Button("") { pane = .overview }.keyboardShortcut("0")
                Button("") { NSApplication.shared.terminate(nil) }.keyboardShortcut("q")
                Button("") { onClose() }.keyboardShortcut(.cancelAction)
                ForEach(Array(model.railOrder.prefix(9).enumerated()), id: \.element.id) { index, usage in
                    Button("") { pane = .account(usage.account) }
                        .keyboardShortcut(KeyEquivalent(Character("\(index + 1)")))
                }
            }
            .opacity(0)
            .allowsHitTesting(false)
            .accessibilityHidden(true)
        }
    }

    // MARK: Rail

    private var rail: some View {
        VStack(spacing: 6) {
            railButton(.overview, help: "Overview  ⌘0", shortcut: "⌘0") {
                Image(systemName: "square.grid.2x2.fill")
                    .font(.system(size: 14, weight: .semibold))
                    .frame(width: 34, height: 34)
            }

            Capsule()
                .fill(.primary.opacity(0.12))
                .frame(width: 18, height: 1.5)
                .padding(.vertical, 3)

            // The accounts scroll only when there are more than the panel can hold.
            ViewThatFits(in: .vertical) {
                accountRings
                ScrollView(.vertical) { accountRings }
                    .scrollIndicators(.never)
            }

            Spacer(minLength: 12)

            railButton(.settings, help: "Settings  ⌘,", shortcut: "⌘,") {
                Image(systemName: "gearshape.fill")
                    .font(.system(size: 14, weight: .semibold))
                    .frame(width: 34, height: 34)
            }
        }
        .padding(.vertical, 12)
        .frame(width: 58)
        .frame(maxHeight: .infinity, alignment: .top)
        .background(alignment: .trailing) {
            Rectangle()
                .fill(.primary.opacity(0.07))
                .frame(width: 0.5)
                .padding(.vertical, 14)
        }
    }

    private var accountRings: some View {
        VStack(spacing: 6) {
            ForEach(Array(model.railOrder.enumerated()), id: \.element.id) { index, usage in
                railButton(.account(usage.account), help: index < 9 ? "\(usage.displayName)  ⌘\(index + 1)" : usage.displayName,
                           shortcut: index < 9 ? "⌘\(index + 1)" : nil) {
                    RailRing(usage: usage)
                }
            }
        }
    }

    private func railButton<Label: View>(_ target: Pane, help: String, shortcut: String? = nil,
                                         @ViewBuilder label: () -> Label) -> some View {
        let selected = pane == target
        return Button {
            pane = target
        } label: {
            label()
                .foregroundStyle(selected ? .primary : .secondary)
                .frame(width: 42, height: 42)
                .background {
                    if selected {
                        Circle()
                            .fill(.primary.opacity(0.13))
                            .overlay(Circle().stroke(.primary.opacity(0.14), lineWidth: 0.5))
                            .matchedGeometryEffect(id: "selection", in: glass)
                    }
                }
                .contentShape(Circle())
                .overlay(alignment: .bottom) {
                    // Held ⌘ shows what each item's shortcut is.
                    if model.commandHeld, let shortcut {
                        ShortcutBadge(text: shortcut)
                            .offset(y: 5)
                            .transition(.scale(scale: 0.6).combined(with: .opacity))
                    }
                }
                .animation(.snappy(duration: 0.16), value: model.commandHeld)
        }
        .buttonStyle(RailButtonStyle())
        .help(help)
        .accessibilityLabel(help)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }

    // MARK: Header and footer

    private var header: some View {
        HStack(alignment: .center, spacing: 10) {
            VStack(alignment: .leading, spacing: 1) {
                Text(title)
                    .font(.system(size: 17, weight: .bold, design: .rounded))
                    .lineLimit(1)
                    .contentTransition(.opacity)
                if let subtitle {
                    Text(subtitle)
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
            }

            Spacer(minLength: 0)

            if model.isDemo {
                Chip(text: "Sample", tint: Palette.warning, symbol: "sparkles")
            }

            if pane != .settings {
                // Point-symmetric, so it sits centred in its circle and spins true.
                GlassIconButton(symbol: "arrow.triangle.2.circlepath", help: "Ask the server to poll every provider now  ⌘R",
                                spinning: model.isLoading) {
                    Task { await model.refresh(force: true) }
                }
                .disabled(!model.settings.isConfigured && !model.isDemo)
            }
        }
    }

    private var title: String {
        switch pane {
        case .overview: "Usage"
        case .settings: "Settings"
        case .account(let key): usage(key)?.displayName ?? "Usage"
        }
    }

    private var subtitle: String? {
        switch pane {
        case .overview:
            let usages = model.snapshot?.usages ?? []
            guard !usages.isEmpty else { return nil }
            let broken = usages.filter { !$0.isOK }.count
            let count = "\(usages.count) subscription\(usages.count == 1 ? "" : "s")"
            return broken == 0 ? count : "\(count) · \(broken) need\(broken == 1 ? "s" : "") attention"
        case .settings:
            return "Server, menu bar, startup"
        case .account(let key):
            guard let usage = usage(key) else { return nil }
            if !usage.isOK { return "Needs attention" }
            return usage.fetchedDate.map { "Read \(Format.ago($0))" }
        }
    }

    private func usage(_ key: String) -> ProviderUsage? {
        model.snapshot?.usages.first { $0.account == key }
    }

    private var footer: some View {
        HStack(spacing: 8) {
            Circle()
                .fill(statusColor)
                .frame(width: 6, height: 6)
                .shadow(color: statusColor.opacity(0.8), radius: 3)
            Text(model.statusLine)
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .truncationMode(.middle)

            Spacer(minLength: 6)

            if let url = model.settings.baseURL, !model.isDemo {
                Button {
                    NSWorkspace.shared.open(url)
                    onClose()
                } label: {
                    Label("Dashboard", systemImage: "arrow.up.right")
                        .font(.system(size: 11, weight: .medium))
                }
                .buttonStyle(.soft)
                .help("Open the server's page in the browser")
            }

            GlassIconButton(symbol: "power", help: "Quit usagetop  ⌘Q", size: 26) {
                NSApplication.shared.terminate(nil)
            }
        }
    }

    private var statusColor: Color {
        if model.isDemo { return Palette.warning }
        if model.problem != nil { return Palette.critical }
        if model.snapshot == nil { return .secondary }
        if let at = model.snapshot?.fetchedDate, model.now.timeIntervalSince(at) > 15 * 60 { return Palette.warning }
        return Color(hex: 0x5BC48A)
    }

    // MARK: Pages

    /// The pages overlap while one fades into the next, so the measured height
    /// is never the two of them stacked.
    private var pageStack: some View {
        ZStack(alignment: .top) {
            page
        }
        .padding(.horizontal, 12)
        .padding(.bottom, 4)
        .animation(.smooth(duration: 0.25), value: pane)
    }

    @ViewBuilder
    private var page: some View {
        switch pane {
        case .settings:
            SettingsPane { pane = .overview }
                .transition(.opacity.combined(with: .offset(y: 6)))
        case .account(let key):
            if let usage = usage(key) {
                AccountDetailView(usage: usage)
                    .id(key)
                    .transition(.opacity.combined(with: .offset(y: 6)))
            }
        case .overview:
            if !model.settings.isConfigured && !model.isDemo {
                Onboarding(openSettings: { pane = .settings })
                    .transition(.opacity)
            } else if model.ordered.isEmpty && model.snapshot != nil && model.problem == nil {
                // A reading came back with nothing in it: the server follows no
                // accounts yet, and nothing will arrive until one is added.
                NoAccounts(openSettings: { pane = .settings })
            } else if model.ordered.isEmpty {
                Waiting(problem: model.problem)
            } else {
                OverviewView(open: { pane = .account($0) })
                    .transition(.opacity.combined(with: .offset(y: 6)))
            }
        }
    }
}

/// A keyboard shortcut, shown on its item while ⌘ is held.
private struct ShortcutBadge: View {
    let text: String

    var body: some View {
        Text(text)
            .font(.system(size: 9.5, weight: .bold, design: .rounded))
            .monospacedDigit()
            .foregroundStyle(.primary)
            .padding(.horizontal, 5)
            .padding(.vertical, 1.5)
            .background(Color(nsColor: .windowBackgroundColor), in: Capsule())
            .overlay(Capsule().strokeBorder(.primary.opacity(0.18), lineWidth: 0.5))
            .shadow(color: .black.opacity(0.18), radius: 2, y: 1)
            .fixedSize()
            .allowsHitTesting(false)
    }
}

/// No highlight of its own: the soft disc behind the selected item is the highlight.
private struct RailButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .scaleEffect(configuration.isPressed ? 0.92 : 1)
            .animation(.snappy(duration: 0.18), value: configuration.isPressed)
    }
}

/// A subscription in the rail: its mark inside a ring of what is left.
private struct RailRing: View {
    let usage: ProviderUsage

    var body: some View {
        let level = usage.isOK ? usage.tightest?.remainingPercent : nil
        Ring(percent: level, size: 32, lineWidth: 2.6) {
            ProviderMark(key: usage.providerKey, size: 14)
        }
        .overlay(alignment: .bottomTrailing) {
            if !usage.isOK {
                Image(systemName: "exclamationmark.circle.fill")
                    .font(.system(size: 11, weight: .bold))
                    .foregroundStyle(.white, Palette.critical)
                    .offset(x: 3, y: 3)
            } else if usage.accountLabel != nil {
                Text(String(usage.accountLabel!.prefix(1)))
                    .font(.system(size: 7.5, weight: .heavy, design: .rounded))
                    .foregroundStyle(.primary)
                    .frame(width: 12, height: 12)
                    .background(.thickMaterial, in: Circle())
                    .offset(x: 3, y: 3)
            }
        }
    }
}

/// Before any server is set.
private struct Onboarding: View {
    @Environment(AppModel.self) private var model
    let openSettings: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Ring(percent: 72, size: 54, lineWidth: 4.5) {
                Image(systemName: "gauge.with.dots.needle.67percent")
                    .font(.system(size: 18, weight: .semibold))
                    .foregroundStyle(.secondary)
            }

            VStack(alignment: .leading, spacing: 5) {
                Text("Connect your usage server")
                    .font(.system(size: 15, weight: .semibold, design: .rounded))
                Text("It polls Claude, ChatGPT, Mistral, Kimi, Muse and Antigravity, keeps the history and holds the sign-ins. This app just reads it.")
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            HStack(spacing: 8) {
                Button("Set up server", action: openSettings)
                    .buttonStyle(.glassProminent)
                Button("Try with sample data") { model.showDemo() }
                    .buttonStyle(.soft)
            }
            .controlSize(.regular)
        }
        .padding(16)
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
    }
}

/// A server is set but has nothing to show yet.
private struct NoAccounts: View {
    let openSettings: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("No accounts yet")
                .font(.system(size: 13, weight: .semibold))
            Text("The server follows no subscriptions. Add the ones you have under Accounts on the server, then sign in or set a key.")
                .font(.system(size: 12))
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Button("Open Settings", action: openSettings)
                .buttonStyle(.soft)
                .controlSize(.small)
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
    }
}

private struct Waiting: View {
    let problem: String?

    var body: some View {
        HStack(spacing: 10) {
            if problem == nil {
                ProgressView().controlSize(.small)
            } else {
                Image(systemName: "exclamationmark.triangle.fill")
                    .foregroundStyle(Palette.warning)
            }
            Text(problem ?? "Waiting for the first reading")
                .font(.system(size: 12))
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
    }
}

/// The one sheet of Liquid Glass the whole panel sits on. Clear glass over a
/// busy desktop leaves grey text with nothing to stand on, so it carries a
/// tint in the panel's own shade, dark or light: the lensing stays, and the
/// text gets a floor.
///
/// How much of it is glass is a setting: over the glass lies the window
/// colour, from not at all (clear glass) to fully (a flat, opaque panel).
private struct PanelGlass: ViewModifier {
    let enabled: Bool
    @Environment(AppModel.self) private var model
    @Environment(\.colorScheme) private var scheme

    func body(content: Content) -> some View {
        if enabled {
            let shape = RoundedRectangle(cornerRadius: StatusController.cornerRadius, style: .continuous)
            content
                .background(Color(nsColor: .windowBackgroundColor).opacity(1 - model.settings.glass))
                .clipShape(shape)
                .glassEffect(.regular.tint(scheme == .dark ? .black.opacity(0.32) : .white.opacity(0.42)), in: shape)
        } else {
            content
        }
    }
}

/// Drawn to a file, the panel is as tall as its content; in the window it
/// fills the window exactly, top to bottom.
private struct FlatOrFilling: ViewModifier {
    let flat: Bool

    func body(content: Content) -> some View {
        if flat {
            content.fixedSize(horizontal: false, vertical: true)
        } else {
            content.frame(maxHeight: .infinity, alignment: .top)
        }
    }
}

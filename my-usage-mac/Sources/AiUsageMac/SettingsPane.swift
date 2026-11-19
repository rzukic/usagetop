import SwiftUI

/// Where the server lives and how to authenticate, what the menu bar shows,
/// and whether the app opens at login. A page of the panel, so the app never
/// grows a window. The token is a credential, so it goes to the Keychain.
struct SettingsPane: View {
    @Environment(AppModel.self) private var model
    let onDone: () -> Void

    @State private var address = ""
    @State private var token = ""
    @State private var testing = false
    @State private var result: String?
    @State private var resultIsError = false
    @State private var launchAtLogin = false

    private var dirty: Bool {
        address != model.settings.serverURL || token != model.settings.token
    }

    var body: some View {
        @Bindable var settings = model.settings

        VStack(alignment: .leading, spacing: 10) {
            // The accounts come first: with no server, this is all there is to set up.
            if !model.isDemo {
                ServerAccountsSection()
            }

            section("Server (optional)", symbol: "server.rack") {
                Text(model.usesServer
                     ? "This Mac reads everything from the server. Clear the address to have it ask the providers itself."
                     : "Without one, this Mac asks the providers itself. A server of your own polls around the clock and shares one history with your other devices.")
                    .font(.system(size: 10.5))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                field("Address") {
                    TextField("usage.example.com", text: $address)
                        .autocorrectionDisabled()
                        .onSubmit(save)
                }
                field("Token") {
                    SecureField("Bearer token", text: $token)
                        .onSubmit(save)
                }

                if let result {
                    Label(result, systemImage: resultIsError ? "xmark.octagon.fill" : "checkmark.circle.fill")
                        .font(.system(size: 11))
                        .foregroundStyle(resultIsError ? Palette.critical : Color(hex: 0x5BC48A))
                        .fixedSize(horizontal: false, vertical: true)
                        .transition(.opacity)
                }

                HStack(spacing: 8) {
                    Button {
                        Task { await test() }
                    } label: {
                        HStack(spacing: 5) {
                            if testing { ProgressView().controlSize(.mini) }
                            Text("Test connection")
                        }
                    }
                    .buttonStyle(.soft)
                    .disabled(testing || address.trimmingCharacters(in: .whitespaces).isEmpty)

                    Spacer()

                    Button("Save", action: save)
                        .buttonStyle(.glassProminent)
                        .disabled(!dirty)
                }
                .controlSize(.small)
            }


            if !model.railOrder.isEmpty {
                OrderSection()
            }

            if !model.hiddenList.isEmpty {
                section("Hidden limits", symbol: "eye.slash") {
                    ForEach(model.hiddenList, id: \.self) { key in
                        HStack {
                            Text(model.hiddenName(key))
                                .font(.system(size: 12))
                                .lineLimit(1)
                            Spacer()
                            Button("Show") { model.setHidden(false, key: key) }
                                .buttonStyle(.soft)
                                .controlSize(.small)
                        }
                    }
                    Text(model.usesServer
                         ? "Kept by the server, for every app that reads from it. Left out of the overview, the menu bar and the sidebar's rings; each still shows on its account's page, where the eye button hides or shows it."
                         : "Left out of the overview, the menu bar and the sidebar's rings; each still shows on its account's page, where the eye button hides or shows it.")
                        .font(.system(size: 10.5))
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }

            section("Menu bar", symbol: "menubar.rectangle") {
                VStack(alignment: .leading, spacing: 4) {
                    HStack {
                        Text("Show")
                            .font(.system(size: 12))
                        Spacer()
                        Picker("Show", selection: $settings.menuBarLimits) {
                            Text("Ring").tag(0)
                            ForEach(1...Settings.maxMenuBarLimits, id: \.self) { Text("\($0)").tag($0) }
                        }
                        .labelsHidden()
                        .pickerStyle(.segmented)
                        .fixedSize()
                    }
                    Text(settings.menuBarLimits == 0
                         ? "The combined figure, in a ring."
                         : "The \(settings.menuBarLimits == 1 ? "limit" : "\(settings.menuBarLimits) limits") closest to running out.")
                        .font(.system(size: 10.5))
                        .foregroundStyle(.secondary)
                }
                if settings.menuBarLimits == 0 {
                    switchRow("Percentage beside the ring", isOn: $settings.showsPercentInMenuBar)
                }
            }

            section("Look", symbol: "circle.lefthalf.filled") {
                VStack(alignment: .leading, spacing: 4) {
                    HStack {
                        Text("Glass")
                            .font(.system(size: 12))
                        Spacer()
                        Text(glassName(settings.glass))
                            .font(.system(size: 11))
                            .foregroundStyle(.secondary)
                            .monospacedDigit()
                    }
                    HStack(spacing: 8) {
                        Image(systemName: "square.fill")
                            .font(.system(size: 10))
                            .foregroundStyle(.secondary)
                            .help("Flat")
                        Slider(value: $settings.glass, in: 0...1)
                            .controlSize(.small)
                        Image(systemName: "drop.halffull")
                            .font(.system(size: 11))
                            .foregroundStyle(.secondary)
                            .help("Liquid Glass")
                    }
                }

                VStack(alignment: .leading, spacing: 4) {
                    HStack {
                        Text("Tiles")
                            .font(.system(size: 12))
                        Spacer()
                        Text(tilesName(settings.tiles))
                            .font(.system(size: 11))
                            .foregroundStyle(.secondary)
                            .monospacedDigit()
                    }
                    HStack(spacing: 8) {
                        Image(systemName: "square.dashed")
                            .font(.system(size: 11))
                            .foregroundStyle(.secondary)
                            .help("See-through")
                        Slider(value: $settings.tiles, in: 0...1)
                            .controlSize(.small)
                        Image(systemName: "square.fill")
                            .font(.system(size: 10))
                            .foregroundStyle(.secondary)
                            .help("Solid")
                    }
                    Text("How solid the tiles are, apart from the panel behind them.")
                        .font(.system(size: 10.5))
                        .foregroundStyle(.secondary)
                }
            }

            section("General", symbol: "switch.2") {
                switchRow("Open at login", isOn: $launchAtLogin)
                    .onChange(of: launchAtLogin) { _, wanted in
                        if model.settings.launchesAtLogin != wanted { model.settings.launchesAtLogin = wanted }
                        launchAtLogin = model.settings.launchesAtLogin
                    }

                HStack {
                    Text(model.isDemo ? "Showing sample data" : "Sample data")
                        .font(.system(size: 12))
                    Spacer()
                    Button(model.isDemo ? "Back to the server" : "Preview") {
                        if model.isDemo { model.leaveDemo() } else { model.showDemo(); onDone() }
                    }
                    .buttonStyle(.soft)
                }
            }

            updates

            Text("usagetop \(version) · ⌘R refresh · ⌘1–9 accounts · esc close")
                .font(.system(size: 10))
                .foregroundStyle(.secondary)
                .frame(maxWidth: .infinity)
                .padding(.top, 2)
        }
        .animation(.smooth(duration: 0.2), value: result)
        .onAppear {
            address = model.settings.serverURL
            token = model.settings.token
            launchAtLogin = model.settings.launchesAtLogin
        }
    }

    /// The app's version and where it stands with updates; "Restart to update"
    /// once a newer one has been downloaded.
    @ViewBuilder
    private var updates: some View {
        @Bindable var updater = model.updater

        section("Updates", symbol: "arrow.down.circle") {
            HStack(alignment: .center, spacing: 10) {
                VStack(alignment: .leading, spacing: 2) {
                    Text("usagetop \(updater.version)")
                        .font(.system(size: 12))
                    Text(updateStatus(updater))
                        .font(.system(size: 10.5))
                        .foregroundStyle(updater.phase == .ready ? Color(hex: 0x5BC48A) : .secondary)
                        .fixedSize(horizontal: false, vertical: true)
                        .help(updater.phase == .failed ? updater.failureDetail ?? ""
                              : updater.sourceOverride.map { "Updates from \($0)" } ?? "")
                }
                Spacer()
                if updater.isInstalled {
                    Button("Check now") {
                        Task { await updater.check() }
                    }
                    .buttonStyle(.soft)
                    .controlSize(.small)
                    .disabled(updater.busy)
                    .help("Look for a newer version now")
                }
            }

            if updater.phase == .ready {
                VStack(alignment: .leading, spacing: 4) {
                    Button("Restart to update") {
                        if updater.restartToUpdate() { NSApplication.shared.terminate(nil) }
                    }
                    .buttonStyle(.glassProminent)
                    .controlSize(.small)
                    .help("Close usagetop, put the new version in and start it again")
                    Text("Otherwise it goes in when usagetop next quits or starts.")
                        .font(.system(size: 10.5))
                        .foregroundStyle(.secondary)
                }
            }

            if updater.isInstalled {
                VStack(alignment: .leading, spacing: 4) {
                    switchRow("Beta versions", isOn: $updater.beta)
                    Text("Early builds as well as releases, whichever is newer.")
                        .font(.system(size: 10.5))
                        .foregroundStyle(.secondary)
                }
            }
        }
    }

    private func updateStatus(_ updater: Updater) -> String {
        // Reading the clock keeps "checked 2h ago" ageing while the page is open.
        _ = model.now
        switch updater.phase {
        case .off: return "Move usagetop to the Applications folder and it updates itself"
        case .waiting: return "Looks for updates shortly"
        case .checking: return "Checking for updates…"
        case .upToDate: return updater.checkedAt.map { "Up to date · checked \(Format.ago($0))" } ?? "Up to date"
        case .downloading: return "Downloading \(updater.target ?? "")…"
        case .ready: return "\(updater.target ?? "A newer version") is ready"
        case .failed: return updater.failure ?? "Couldn't check for updates"
        }
    }

    private var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev"
    }

    private func section<Content: View>(_ title: String, symbol: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Label(title, systemImage: symbol)
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(.secondary)
            content()
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
    }

    private func tilesName(_ value: Double) -> String {
        switch value {
        case ..<0.02: "See-through"
        case 0.98...: "Solid"
        default: "\(Int((value * 100).rounded()))% solid"
        }
    }

    private func glassName(_ value: Double) -> String {
        switch value {
        case ..<0.02: "Flat"
        case 0.98...: "Liquid Glass"
        default: "\(Int((value * 100).rounded()))% glass"
        }
    }

    private func switchRow(_ title: String, isOn: Binding<Bool>) -> some View {
        HStack {
            Text(title).font(.system(size: 12))
            Spacer()
            Toggle(title, isOn: isOn)
                .labelsHidden()
                .toggleStyle(.switch)
                .controlSize(.mini)
        }
    }

    private func field<Content: View>(_ label: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(label)
                .font(.system(size: 10.5, weight: .medium))
                .foregroundStyle(.secondary)
            content()
                .textFieldStyle(.plain)
                .font(.system(size: 12.5))
                .padding(.horizontal, 10)
                .padding(.vertical, 7)
                .background(.primary.opacity(0.06), in: RoundedRectangle(cornerRadius: 9, style: .continuous))
                .overlay(RoundedRectangle(cornerRadius: 9, style: .continuous).strokeBorder(.primary.opacity(0.1), lineWidth: 0.5))
        }
    }

    private func save() {
        model.settings.serverURL = address
        model.settings.token = token
        if model.isDemo { model.leaveDemo() } else { model.serverChanged() }
        result = nil
        onDone()
    }

    private func test() async {
        testing = true
        defer { testing = false }

        var trimmed = address.trimmingCharacters(in: .whitespacesAndNewlines)
        if !trimmed.contains("://") { trimmed = "http://" + trimmed }
        while trimmed.hasSuffix("/") { trimmed.removeLast() }

        guard let url = URL(string: trimmed), url.host != nil else {
            result = "That is not an address"
            resultIsError = true
            return
        }

        do {
            let state = try await ServerClient(baseURL: url, token: token).state()
            let count = state.snapshot?.usages.count ?? 0
            result = count == 0
                ? "Reached the server, but it has no reading yet"
                : "Reached the server · \(count) subscription\(count == 1 ? "" : "s")"
            resultIsError = false
        } catch {
            result = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
            resultIsError = true
        }
    }
}

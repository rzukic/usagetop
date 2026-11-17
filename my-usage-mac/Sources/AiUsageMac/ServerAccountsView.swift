import AppKit
import SwiftUI

/// "Accounts on the server" in Settings: every account the server follows,
/// what it has stored, and what can be done about it. Steps open inline under
/// their row, and a confirmation is a row of its own rather than a dialog: a
/// dialog would take the keyboard from the panel and close it.
struct ServerAccountsSection: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        let accounts = model.accounts

        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Label("Accounts on the server", systemImage: "person.2")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(.secondary)
                Spacer()
                if !accounts.loaded { ProgressView().controlSize(.mini) }
            }

            if let problem = accounts.listProblem {
                Label(problem, systemImage: "exclamationmark.triangle.fill")
                    .font(.system(size: 11))
                    .foregroundStyle(Palette.warning)
                    .fixedSize(horizontal: false, vertical: true)
            } else if accounts.loaded && accounts.accounts.isEmpty {
                Text("The server follows no accounts yet.")
                    .font(.system(size: 11.5))
                    .foregroundStyle(.secondary)
            }

            VStack(spacing: 0) {
                ForEach(Array(accounts.accounts.enumerated()), id: \.element.id) { index, account in
                    if index > 0 {
                        Rectangle().fill(.primary.opacity(0.08)).frame(height: 0.5)
                    }
                    AccountRow(account: account)
                        .padding(.vertical, 9)
                }
            }

            if accounts.loaded {
                Text("Keys and sessions stay on the server; this app never reads them back.")
                    .font(.system(size: 10.5))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)

                VStack(alignment: .leading, spacing: 6) {
                    Text("Add another account")
                        .font(.system(size: 11, weight: .medium))
                        .foregroundStyle(.secondary)
                    HStack(spacing: 6) {
                        ForEach(ServerAccounts.providers, id: \.self) { provider in
                            Button {
                                Task { await accounts.add(provider: provider) }
                            } label: {
                                ProviderMark(key: provider, size: 13)
                                    .frame(width: 28, height: 28)
                            }
                            .buttonStyle(.softCircle)
                            .help("Add another \(Provider(key: provider).displayName) account")
                            .accessibilityLabel("Add another \(Provider(key: provider).displayName) account")
                        }
                    }
                }
                .padding(.top, 2)
            }
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
        .task { await accounts.load() }
    }
}

/// One account: its mark, name and what is stored; its actions; and the step
/// it is in the middle of, if any.
private struct AccountRow: View {
    @Environment(AppModel.self) private var model
    let account: ServerAccount

    private var accounts: ServerAccounts { model.accounts }
    private var busy: Bool { accounts.busy.contains(account.key) }

    private var statusColor: Color {
        if !account.configured { return .secondary }
        if account.takesKey || account.renews { return Color(hex: 0x5BC48A) }
        // A token that does not renew stops working when it expires.
        return Palette.warning
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 10) {
                ProviderMark(key: account.provider, size: 14)
                    .frame(width: 28, height: 28)
                    .background(.primary.opacity(0.07), in: Circle())

                VStack(alignment: .leading, spacing: 2) {
                    Text(account.title)
                        .font(.system(size: 12.5, weight: .semibold))
                        .lineLimit(1)
                    HStack(spacing: 5) {
                        Circle().fill(statusColor).frame(width: 6, height: 6)
                        Text(accounts.status(account))
                            .font(.system(size: 10.5))
                            .foregroundStyle(.secondary)
                    }
                }

                Spacer(minLength: 6)

                if busy { ProgressView().controlSize(.mini) }

                Button(accounts.actionTitle(account)) {
                    Task { await accounts.act(account) }
                }
                .buttonStyle(.soft)
                .disabled(busy)

                Menu {
                    Button("Rename…") { accounts.show(.rename, for: account.key) }
                    if account.configured {
                        Button("Sign out…") { accounts.show(.confirmSignOut, for: account.key) }
                    }
                    if account.removable {
                        Divider()
                        Button("Remove…", role: .destructive) { accounts.show(.confirmRemove, for: account.key) }
                    }
                } label: {
                    Image(systemName: "ellipsis")
                        .font(.system(size: 11, weight: .bold))
                        .frame(width: 24, height: 24)
                        .background(.primary.opacity(0.09), in: Circle())
                        .overlay(Circle().stroke(.primary.opacity(0.12), lineWidth: 0.5))
                        .contentShape(Circle())
                }
                .menuStyle(.button)
                .menuIndicator(.hidden)
                .buttonStyle(.plain)
                .fixedSize()
                .help("More for \(account.title)")
                .accessibilityLabel("More for \(account.title)")
            }

            if let step = accounts.steps[account.key] {
                StepView(account: account, step: step)
                    .padding(.leading, 38)
                    .transition(.opacity.combined(with: .move(edge: .top)))
            }

            if let problem = accounts.problems[account.key] {
                Label(problem, systemImage: "exclamationmark.triangle.fill")
                    .font(.system(size: 11))
                    .foregroundStyle(Palette.warning)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.leading, 38)
            }
        }
        .animation(.smooth(duration: 0.2), value: accounts.steps[account.key])
    }
}

/// What a row is in the middle of: a key form, a sign-in, a rename, or a
/// confirmation.
private struct StepView: View {
    @Environment(AppModel.self) private var model
    let account: ServerAccount
    let step: ServerAccounts.Step

    @State private var text = ""
    @State private var china = false
    @State private var budget = ""
    @State private var spent = ""

    private var accounts: ServerAccounts { model.accounts }
    private var busy: Bool { accounts.busy.contains(account.key) }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            switch step {
            case .key: keyForm
            case .signIn(let signIn): signInForm(signIn)
            case .rename: renameForm
            case .confirmSignOut:
                confirm("Sign the server out of \(account.title)?", action: "Sign out") {
                    await accounts.signOut(account)
                }
            case .confirmRemove:
                confirm("Remove \(account.title) from the server? Its sign-in is forgotten; its history stays on disk.",
                        action: "Remove") {
                    await accounts.remove(account)
                }
            }
        }
        .padding(10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.primary.opacity(0.05), in: RoundedRectangle(cornerRadius: 12, style: .continuous))
        .onAppear { if step == .rename { text = account.name ?? "" } }
    }

    private var keyForm: some View {
        Group {
            fieldLabel("\(account.title) API key")
            SecureField("Paste the key", text: $text)
                .modifier(FieldStyle())
                .onSubmit(saveKey)
            if account.provider == "kimi" {
                HStack {
                    Text("Use China endpoint (api.moonshot.cn)").font(.system(size: 11.5))
                    Spacer()
                    Toggle("China endpoint", isOn: $china).labelsHidden().toggleStyle(.switch).controlSize(.mini)
                }
            }
            if account.provider == "mistral" {
                HStack(spacing: 8) {
                    VStack(alignment: .leading, spacing: 3) {
                        fieldLabel("Monthly budget (€)")
                        TextField("e.g. 20", text: $budget).modifier(FieldStyle())
                    }
                    VStack(alignment: .leading, spacing: 3) {
                        fieldLabel("Spent so far (€)")
                        TextField("e.g. 4.50", text: $spent).modifier(FieldStyle())
                    }
                }
                Text("Only used when the key cannot read billing.")
                    .font(.system(size: 10.5))
                    .foregroundStyle(.secondary)
            }
            buttons(primary: "Save", action: saveKey)
        }
    }

    private func saveKey() {
        Task {
            await accounts.saveKey(account, value: text, kimiChina: account.provider == "kimi" ? china : nil,
                                   budget: budget, spent: spent)
        }
    }

    @ViewBuilder
    private func signInForm(_ signIn: SignInStep) -> some View {
        if let instructions = signIn.instructions {
            Text(instructions)
                .font(.system(size: 11.5))
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        Button {
            if let url = URL(string: signIn.url) { NSWorkspace.shared.open(url) }
        } label: {
            Label("Open the sign-in page", systemImage: "arrow.up.right")
        }
        .buttonStyle(.soft)

        if signIn.kind == "device" {
            HStack(spacing: 8) {
                Text(signIn.userCode ?? "")
                    .font(.system(size: 20, weight: .semibold, design: .monospaced))
                    .textSelection(.enabled)
                if let code = signIn.userCode {
                    Button {
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.setString(code, forType: .string)
                    } label: {
                        Image(systemName: "doc.on.doc").font(.system(size: 11, weight: .semibold)).frame(width: 24, height: 24)
                    }
                    .buttonStyle(.softCircle)
                    .help("Copy the code")
                }
            }
            HStack(spacing: 6) {
                ProgressView().controlSize(.mini)
                Text("Waiting for you to approve…").font(.system(size: 11)).foregroundStyle(.secondary)
                Spacer()
                Button("Cancel") { accounts.close(account.key) }.buttonStyle(.soft)
            }
        } else {
            fieldLabel(signIn.kind == "redirect" ? "The address the browser landed on" : "The code the page shows")
            TextField("Paste it here", text: $text)
                .modifier(FieldStyle())
                .onSubmit(finish)
            buttons(primary: "Finish", action: finish)
        }
    }

    private func finish() {
        Task { await accounts.finishSignIn(account, input: text) }
    }

    private var renameForm: some View {
        Group {
            fieldLabel("Name, e.g. Work (empty for none)")
            TextField("Name", text: $text)
                .modifier(FieldStyle())
                .onSubmit(rename)
            buttons(primary: "Save", action: rename)
        }
    }

    private func rename() {
        Task { await accounts.rename(account, to: text) }
    }

    private func confirm(_ question: String, action: String, perform: @escaping () async -> Void) -> some View {
        Group {
            Text(question)
                .font(.system(size: 11.5))
                .fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 8) {
                Spacer()
                Button("Cancel") { accounts.close(account.key) }.buttonStyle(.soft)
                Button(action, role: .destructive) { Task { await perform() } }
                    .buttonStyle(.glassProminent)
                    .tint(Palette.critical)
                    .controlSize(.small)
                    .disabled(busy)
            }
        }
    }

    private func buttons(primary: String, action: @escaping () -> Void) -> some View {
        HStack(spacing: 8) {
            Spacer()
            Button("Cancel") { accounts.close(account.key) }.buttonStyle(.soft)
            Button(primary, action: action)
                .buttonStyle(.glassProminent)
                .controlSize(.small)
                .disabled(busy)
        }
    }

    private func fieldLabel(_ text: String) -> some View {
        Text(text)
            .font(.system(size: 10.5, weight: .medium))
            .foregroundStyle(.secondary)
    }
}

/// The settings' text field look: a soft fill and a hairline.
struct FieldStyle: ViewModifier {
    func body(content: Content) -> some View {
        content
            .textFieldStyle(.plain)
            .font(.system(size: 12.5))
            .padding(.horizontal, 10)
            .padding(.vertical, 7)
            .background(.primary.opacity(0.06), in: RoundedRectangle(cornerRadius: 9, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 9, style: .continuous).strokeBorder(.primary.opacity(0.1), lineWidth: 0.5))
    }
}

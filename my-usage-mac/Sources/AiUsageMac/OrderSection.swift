import SwiftUI
import UniformTypeIdentifiers

/// "Order" in Settings: the accounts as the sidebar lists them, dragged into
/// the order wanted. The sidebar and ⌘1–9 follow it; the overview follows it
/// too, or lists whatever is closest to running out first.
struct OrderSection: View {
    @Environment(AppModel.self) private var model

    /// The order on screen while dragging; saved when the drop lands.
    @State private var keys: [String] = []
    @State private var dragging: String?

    var body: some View {
        @Bindable var settings = model.settings
        let usages = Dictionary(model.railOrder.map { ($0.account, $0) }, uniquingKeysWith: { first, _ in first })

        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Label("Order", systemImage: "arrow.up.arrow.down")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(.secondary)
                Spacer()
                if !settings.accountOrder.isEmpty {
                    Button("Reset") {
                        settings.accountOrder = []
                        keys = model.railOrder.map(\.account)
                    }
                    .buttonStyle(.soft)
                    .help("Back to the default order")
                }
            }

            VStack(spacing: 4) {
                ForEach(Array(keys.enumerated()), id: \.element) { index, key in
                    if let usage = usages[key] {
                        row(usage, index: index)
                            .onDrag {
                                dragging = key
                                return NSItemProvider(object: key as NSString)
                            }
                            .onDrop(of: [.text], delegate: Reorder(target: key, keys: $keys, dragging: $dragging) {
                                settings.accountOrder = keys
                            })
                    }
                }
            }
            .animation(.snappy(duration: 0.2), value: keys)
            // A drop between the rows lands here, and still keeps the order.
            .onDrop(of: [.text], isTargeted: nil) { _ in
                settings.accountOrder = keys
                dragging = nil
                return true
            }

            Text("Drag to reorder. The sidebar and ⌘1–9 follow this order.")
                .font(.system(size: 10.5))
                .foregroundStyle(.secondary)

            HStack {
                VStack(alignment: .leading, spacing: 1) {
                    Text("Most at risk first").font(.system(size: 12))
                    Text(settings.overviewByRisk
                         ? "The overview lists what runs out soonest at the top."
                         : "The overview follows the order above.")
                        .font(.system(size: 10.5))
                        .foregroundStyle(.secondary)
                }
                Spacer()
                Toggle("Most at risk first", isOn: $settings.overviewByRisk)
                    .labelsHidden()
                    .toggleStyle(.switch)
                    .controlSize(.mini)
            }
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
        .onAppear { keys = model.railOrder.map(\.account) }
        .onChange(of: model.railOrder.map(\.account)) { _, order in
            if dragging == nil { keys = order }
        }
    }

    private func row(_ usage: ProviderUsage, index: Int) -> some View {
        HStack(spacing: 10) {
            Image(systemName: "line.3.horizontal")
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(.secondary)
            ProviderMark(key: usage.providerKey, size: 13)
                .frame(width: 24, height: 24)
                .background(.primary.opacity(0.07), in: Circle())
            Text(usage.displayName)
                .font(.system(size: 12))
                .lineLimit(1)
            Spacer()
            if index < 9 {
                Text("⌘\(index + 1)")
                    .font(.system(size: 10.5, weight: .medium, design: .rounded))
                    .monospacedDigit()
                    .foregroundStyle(.secondary)
            }
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 5)
        .background(.primary.opacity(dragging == usage.account ? 0.12 : 0.05),
                    in: RoundedRectangle(cornerRadius: 9, style: .continuous))
        .contentShape(RoundedRectangle(cornerRadius: 9, style: .continuous))
        .accessibilityElement(children: .combine)
        .accessibilityAction(named: "Move up") { move(usage.account, by: -1) }
        .accessibilityAction(named: "Move down") { move(usage.account, by: 1) }
    }

    private func move(_ key: String, by offset: Int) {
        guard let from = keys.firstIndex(of: key) else { return }
        let to = min(max(from + offset, 0), keys.count - 1)
        guard to != from else { return }
        keys.move(fromOffsets: IndexSet(integer: from), toOffset: to > from ? to + 1 : to)
        model.settings.accountOrder = keys
    }
}

/// Moves the dragged row to where the pointer is as it goes, and saves the
/// order when it is dropped.
private struct Reorder: DropDelegate {
    let target: String
    @Binding var keys: [String]
    @Binding var dragging: String?
    let save: () -> Void

    func dropEntered(info: DropInfo) {
        guard let dragging, dragging != target,
              let from = keys.firstIndex(of: dragging),
              let to = keys.firstIndex(of: target) else { return }
        keys.move(fromOffsets: IndexSet(integer: from), toOffset: to > from ? to + 1 : to)
    }

    func dropUpdated(info: DropInfo) -> DropProposal? {
        DropProposal(operation: .move)
    }

    func performDrop(info: DropInfo) -> Bool {
        save()
        dragging = nil
        return true
    }
}

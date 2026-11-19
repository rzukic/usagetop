import SwiftUI

/// Every subscription on one page: the combined figure up top with where it is
/// heading, then each account, the one about to run out first.
struct OverviewView: View {
    @Environment(AppModel.self) private var model
    let open: (String) -> Void

    var body: some View {
        VStack(spacing: 10) {
            if let combined = model.combinedPercent {
                CombinedCard(percent: combined)
            }
            ForEach(model.ordered) { usage in
                AccountCard(usage: usage) { open(usage.account) }
            }
        }
    }
}

/// The hero: one number for everything, its week, and the forecast.
private struct CombinedCard: View {
    @Environment(AppModel.self) private var model
    let percent: Double

    var body: some View {
        let outlook = model.combinedOutlook
        let providers = model.snapshot?.combinedProviders ?? model.snapshot?.usages.filter(\.isOK).count ?? 0

        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .center, spacing: 14) {
                Ring(percent: percent, size: 66, lineWidth: 6) {
                    Image(systemName: "sum")
                        .font(.system(size: 16, weight: .bold))
                        .foregroundStyle(.secondary)
                }

                VStack(alignment: .leading, spacing: 0) {
                    Text("COMBINED")
                        .font(.system(size: 9.5, weight: .bold))
                        .tracking(0.8)
                        .foregroundStyle(.secondary)
                    HStack(alignment: .firstTextBaseline, spacing: 0) {
                        Text("\(Int(percent.rounded()))")
                            .font(.system(size: 40, weight: .semibold, design: .rounded))
                            .monospacedDigit()
                            .contentTransition(.numericText(value: percent))
                        Text("%")
                            .font(.system(size: 22, weight: .semibold, design: .rounded))
                        Text(" left")
                            .font(.system(size: 14, weight: .medium, design: .rounded))
                            .foregroundStyle(.secondary)
                    }
                    .foregroundStyle(Severity.textColor(for: percent))
                    Text("across \(providers) subscription\(providers == 1 ? "" : "s")")
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                }

                Spacer(minLength: 0)
            }

            if model.combinedHistory.count > 1 {
                HistoryChart(points: model.combinedHistory, days: 7, now: model.now,
                             height: 64, showsAxes: false)
            }

            if let outlook {
                BurnLine(outlook: outlook, combined: true)
            }
        }
        .padding(14)
        .card()
    }
}

/// One account: its mark, its plan, and a bar for every window.
private struct AccountCard: View {
    @Environment(AppModel.self) private var model
    let usage: ProviderUsage
    let open: () -> Void

    @State private var hovering = false

    var body: some View {
        Button(action: open) {
            VStack(alignment: .leading, spacing: 10) {
                HStack(spacing: 9) {
                    ProviderMark(key: usage.providerKey, size: 15)
                        .frame(width: 28, height: 28)
                        .background(.primary.opacity(0.07), in: Circle())

                    VStack(alignment: .leading, spacing: 1) {
                        Text(usage.displayName)
                            .font(.system(size: 13, weight: .semibold))
                            .foregroundStyle(usage.isOK ? .primary : .secondary)
                            .lineLimit(1)
                        if let plan = usage.plan {
                            Text(plan)
                                .font(.system(size: 10.5, weight: .medium))
                                .foregroundStyle(.secondary)
                                .lineLimit(1)
                        }
                    }

                    Spacer(minLength: 4)

                    if usage.stale && usage.isOK {
                        Chip(text: "Earlier reading", symbol: "clock.arrow.circlepath")
                    }

                    Image(systemName: "chevron.right")
                        .font(.system(size: 10, weight: .bold))
                        .foregroundStyle(.secondary)
                        .offset(x: hovering ? 2 : 0)
                }

                if !usage.isOK {
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        Image(systemName: usage.isNotConfigured ? "person.crop.circle.badge.questionmark" : "exclamationmark.triangle.fill")
                            .foregroundStyle(usage.isNotConfigured ? Color.secondary : Palette.warning)
                        Text(usage.isNotConfigured ? "Not set up on the server" : (usage.message ?? "Unavailable"))
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    .font(.system(size: 11.5))
                } else if usage.windows.isEmpty {
                    Text("No limits reported")
                        .font(.system(size: 11.5))
                        .foregroundStyle(.secondary)
                } else {
                    let shown = model.shownWindows(usage)
                    let hidden = usage.windows.count - shown.count
                    VStack(alignment: .leading, spacing: 9) {
                        ForEach(shown) { window in
                            WindowRow(window: window, now: model.now)
                                .contextMenu { HideMenu(usage: usage, window: window) }
                        }
                        if hidden > 0 {
                            Label(shown.isEmpty ? "All limits hidden" : "\(hidden) hidden", systemImage: "eye.slash")
                                .font(.system(size: 10.5))
                                .foregroundStyle(.secondary)
                        }
                    }
                }
            }
            .padding(12)
            .contentShape(RoundedRectangle(cornerRadius: 18, style: .continuous))
            .card(highlighted: hovering)
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .animation(.smooth(duration: 0.2), value: hovering)
        .help("Show \(usage.displayName)'s history and forecast")
    }
}

/// A window in a card: its name and when it refills, then the bar.
struct WindowRow: View {
    let window: UsageWindow
    let now: Date

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Text(window.displayLabel)
                    .font(.system(size: 11.5, weight: .semibold))
                    .foregroundStyle(window.countsTowardCombined ? .primary : .secondary)
                if let reset = Format.reset(window.resetDate) {
                    Text("resets in \(reset)")
                        .font(.system(size: 10.5))
                        .foregroundStyle(.secondary)
                        .monospacedDigit()
                }
                Spacer(minLength: 4)
                // Only what needs a look: being spent too fast, or already gone.
                // The flame alone is enough here; the account page spells it out.
                switch Forecast.pace(window, now: now) {
                case .empty:
                    PaceBadge(pace: .empty)
                case .ahead:
                    PaceBadge(pace: .ahead, compact: true)
                    LevelText(percent: window.remainingPercent, size: 13)
                default:
                    LevelText(percent: window.remainingPercent, size: 13)
                }
            }
            if let level = window.remainingPercent {
                UsageBar(percent: level, even: Forecast.evenPercent(window, now: now))
            }
        }
    }
}

/// "Burning 2.1%/h · usually 0.6%/h" and where that ends up.
struct BurnLine: View {
    @Environment(AppModel.self) private var model
    let outlook: Forecast.Outlook
    var combined = false

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 6) {
            Image(systemName: icon)
                .font(.system(size: 10, weight: .semibold))
                .foregroundStyle(iconColor)
                .frame(width: 12)
            VStack(alignment: .leading, spacing: 2) {
                Text(Forecast.rates(outlook))
                    .foregroundStyle(.secondary)
                if outlook.draining || alreadyEmpty, let outcome {
                    Text(outcome)
                        .foregroundStyle(runsDry ? Palette.critical : Color.secondary)
                        .fontWeight(runsDry ? .semibold : .regular)
                }
            }
            .font(.system(size: 11))
            .monospacedDigit()
            .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var alreadyEmpty: Bool { outlook.level < 0.5 }

    /// Empty before the next refill is the thing worth shouting about.
    private var runsDry: Bool {
        if alreadyEmpty { return true }
        guard let empty = outlook.emptyAt else { return false }
        if combined, let next = outlook.next, empty >= next.reset { return false }
        return true
    }

    private var outcome: String? {
        if alreadyEmpty {
            return outlook.reset.map { "Empty until the reset, \(Format.when($0))" } ?? "Empty"
        }
        if runsDry, let empty = outlook.emptyAt {
            return "Empty in \(Format.span(empty.timeIntervalSince(model.now))), \(Format.when(empty))"
        }
        if combined {
            guard let next = outlook.next else { return "No reset reported" }
            return "Next lift \(Format.when(next.reset)) — \(next.name) resets"
        }
        return outlook.reset == nil ? "No reset reported" : "Lasts past the reset"
    }

    private var icon: String {
        if !outlook.draining { return "moon.zzz.fill" }
        return runsDry ? "flame.fill" : "speedometer"
    }

    private var iconColor: Color {
        if !outlook.draining { return .secondary }
        return runsDry ? Palette.critical : .secondary
    }
}

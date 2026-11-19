import SwiftUI

/// One account in full: every window with its history, its pace and where it
/// is heading.
struct AccountDetailView: View {
    @Environment(AppModel.self) private var model
    let usage: ProviderUsage

    var body: some View {
        VStack(spacing: 10) {
            summary

            if usage.isOK {
                ForEach(usage.windows) { window in
                    WindowDetailCard(usage: usage, window: window)
                }
            }
        }
    }

    private var summary: some View {
        HStack(spacing: 14) {
            Ring(percent: usage.isOK ? model.tightest(usage)?.remainingPercent : nil, size: 58, lineWidth: 5) {
                ProviderMark(key: usage.providerKey, size: 22)
            }

            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 6) {
                    if let plan = usage.plan { Chip(text: plan, symbol: "creditcard") }
                    if usage.stale { Chip(text: "Earlier reading", tint: Palette.warning, symbol: "clock.arrow.circlepath") }
                }
                if usage.isOK, let tightest = model.tightest(usage) ?? usage.tightest, let level = tightest.remainingPercent {
                    HStack(alignment: .firstTextBaseline, spacing: 4) {
                        Text(Format.percent(level))
                            .font(.system(size: 26, weight: .semibold, design: .rounded))
                            .monospacedDigit()
                            .foregroundStyle(Severity.textColor(for: level))
                        Text("left of \(tightest.displayLabel)")
                            .font(.system(size: 12, weight: .medium))
                            .foregroundStyle(.secondary)
                    }
                } else {
                    Text(usage.isNotConfigured ? "Not set up on the server" : (usage.message ?? "Unavailable"))
                        .font(.system(size: 12))
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer(minLength: 0)
        }
        .padding(14)
        .card()
    }
}

/// A window: the figure, the bar with its pace tick, the chart and the burn.
private struct WindowDetailCard: View {
    @Environment(AppModel.self) private var model
    let usage: ProviderUsage
    let window: UsageWindow

    /// A window that refills within a day charts half a day (a couple of
    /// cycles, with room to see the forecast), one that refills within a week
    /// charts a week; anything slower, five.
    private var days: Double {
        guard let length = Forecast.period(of: window) else { return 35 }
        if length < Forecast.day { return 0.5 }
        return length <= 7 * Forecast.day ? 7 : 35
    }

    var body: some View {
        let outlook = model.outlook(for: usage, window)
        let pace = Forecast.pace(window, now: model.now)
        let hidden = model.isHidden(usage, window)

        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .firstTextBaseline) {
                VStack(alignment: .leading, spacing: 2) {
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        Text(window.displayLabel)
                            .font(.system(size: 14, weight: .semibold, design: .rounded))
                        if hidden {
                            Text("Hidden")
                                .font(.system(size: 10.5, weight: .medium))
                                .foregroundStyle(.secondary)
                                .help("Left out of the overview, the menu bar and the sidebar's ring.")
                        }
                    }
                    Text(resetText)
                        .font(.system(size: 10.5))
                        .foregroundStyle(.secondary)
                        .monospacedDigit()
                }
                Spacer()
                // At 0% the Empty badge below says it; the figure would only repeat it.
                if Forecast.pace(window, now: model.now) == .empty {
                    PaceBadge(pace: .empty)
                } else {
                    LevelText(percent: window.remainingPercent, size: 22)
                }
            }

            if let level = window.remainingPercent {
                UsageBar(percent: level, even: Forecast.evenPercent(window, now: model.now), height: 8)
            }

            HStack(spacing: 6) {
                if let pace, pace != .empty { PaceBadge(pace: pace) }
                if !window.countsTowardCombined {
                    Chip(text: "Not combined", symbol: "minus.circle")
                        .help("A slice of another window, so it is left out of the combined figure.")
                }
                Spacer(minLength: 4)
                if let even = Forecast.evenPercent(window, now: model.now) {
                    Text("even pace \(Format.percent(even))")
                        .font(.system(size: 10.5))
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .help("What an even spread over the cycle would leave now: the tick on the bar.")
                }
            }

            HistoryChart(points: model.points(for: usage, window), outlook: outlook, days: days, now: model.now)
                .padding(.top, 2)

            if let outlook {
                BurnLine(outlook: outlook)
            }

            if let detail = window.detail, !detail.isEmpty {
                Text(detail)
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(14)
        .opacity(hidden ? 0.55 : 1)
        .card()
        .contextMenu { HideMenu(usage: usage, window: window) }
        .accessibilityAction(named: hidden ? "Show in the overview" : "Hide from the overview") {
            model.setHidden(!hidden, usage, window)
        }
    }

    private var resetText: String {
        guard let reset = window.resetDate else { return "No reset reported" }
        guard reset > model.now else { return "Resetting now" }
        return "Resets in \(Format.span(reset.timeIntervalSince(model.now))) · \(Format.when(reset))"
    }
}


/// What a right-click on a limit offers: hiding it from the overview, the menu
/// bar and the sidebar's ring, or showing it there again.
struct HideMenu: View {
    @Environment(AppModel.self) private var model
    let usage: ProviderUsage
    let window: UsageWindow

    var body: some View {
        if model.isHidden(usage, window) {
            Button("Show \(window.displayLabel) in the overview", systemImage: "eye") {
                model.setHidden(false, usage, window)
            }
        } else {
            Button("Hide \(window.displayLabel) from the overview", systemImage: "eye.slash") {
                model.setHidden(true, usage, window)
            }
        }
    }
}

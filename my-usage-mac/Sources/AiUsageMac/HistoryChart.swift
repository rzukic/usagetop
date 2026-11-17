import Charts
import SwiftUI

/// One window's level over time on a fixed 0–100 axis, then the forecast from
/// now as a dashed line, flat once it runs dry. Hover to read a moment.
struct HistoryChart: View {
    let points: [SeriesPoint]
    var outlook: Forecast.Outlook?
    var days: Double = 7
    var now: Date = .now
    var height: CGFloat = 96
    var showsAxes = true

    @State private var hovered: Date?
    @State private var readoutWidth: CGFloat = 110

    /// Where the history starts, or the range's start if there is more: a
    /// short history fills the width rather than leaving a blank left side.
    /// Never narrower than a quarter of the range.
    private var from: Date {
        let start = now.addingTimeInterval(-days * Forecast.day)
        guard let first = points.first?.date, first > start else { return start }
        return min(first, now.addingTimeInterval(-days * Forecast.day * 0.25))
    }

    /// With a forecast the chart runs on past now: a quarter as far again,
    /// further if it runs dry later, never past the reset.
    private var to: Date {
        guard let outlook, outlook.draining || outlook.reset != nil else { return now }
        var end = now.addingTimeInterval(days * Forecast.day * 0.25)
        if let empty = outlook.emptyAt, empty > end {
            end = min(empty.addingTimeInterval(empty.timeIntervalSince(now) * 0.15), now.addingTimeInterval(days * Forecast.day))
        }
        if let reset = outlook.reset, reset < end { end = reset }
        return end
    }

    private var shown: [SeriesPoint] {
        let inRange = points.filter { $0.date >= from && $0.date <= now }
        // The level going into the range, so the line starts at the left edge.
        var list = inRange
        if let before = points.last(where: { $0.date < from }) {
            list.insert(SeriesPoint(date: from, value: before.value), at: 0)
        }
        if let last = list.last, last.date < now {
            list.append(SeriesPoint(date: now, value: last.value))
        }
        return list
    }

    /// Stops short of the reset: the refill is not a level the window reaches
    /// by being spent, and drawn at the very edge it is a stray vertical line.
    private var forecast: [SeriesPoint] {
        guard let outlook, to > now else { return [] }
        let steps = outlook.steps(now, to)
        guard let reset = outlook.reset else { return steps }
        return steps.filter { $0.date < reset }
    }

    private var tone: Severity { Severity(points.last?.value ?? 100) }

    private func value(at date: Date) -> (Date, Double)? {
        if date > now, let point = forecast.min(by: { abs($0.date.timeIntervalSince(date)) < abs($1.date.timeIntervalSince(date)) }) {
            return (point.date, point.value)
        }
        // Before the first reading there is nothing to report.
        guard let point = shown.last(where: { $0.date <= date }) else { return nil }
        return (date, point.value)
    }

    /// The level at the pointer and when that was, or "forecast" for a moment ahead.
    private func readout(at date: Date, level: Double) -> some View {
        HStack(spacing: 5) {
            Text(Format.percent(level))
                .font(.system(size: 11, weight: .semibold, design: .rounded))
                .monospacedDigit()
            Text(date > now ? "forecast \(Format.when(date))" : Format.when(date))
                .font(.system(size: 10))
                .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 7)
        .padding(.vertical, 3)
        .background(.background.opacity(0.85), in: Capsule())
        .overlay(Capsule().stroke(.primary.opacity(0.15), lineWidth: 0.5))
    }

    var body: some View {
        let history = shown
        let ahead = forecast
        let tone = tone

        Chart {
            ForEach(history, id: \.date) { point in
                AreaMark(
                    x: .value("Time", point.date),
                    yStart: .value("Floor", 0),
                    yEnd: .value("Left", point.value),
                    series: .value("Series", "history")
                )
                .interpolationMethod(.stepEnd)
                .foregroundStyle(
                    LinearGradient(colors: [tone.color.opacity(0.32), tone.color.opacity(0.02)],
                                   startPoint: .top, endPoint: .bottom)
                )

                LineMark(
                    x: .value("Time", point.date),
                    y: .value("Left", point.value),
                    series: .value("Series", "history")
                )
                .interpolationMethod(.stepEnd)
                .foregroundStyle(tone.color)
                .lineStyle(StrokeStyle(lineWidth: 1.6, lineJoin: .round))
            }

            ForEach(ahead, id: \.date) { point in
                LineMark(
                    x: .value("Time", point.date),
                    y: .value("Left", point.value),
                    series: .value("Series", "forecast")
                )
                .foregroundStyle(Color.primary.opacity(0.55))
                .lineStyle(StrokeStyle(lineWidth: 1.3, lineCap: .round, dash: [3, 3]))
            }

            if !ahead.isEmpty {
                RuleMark(x: .value("Now", now))
                    .foregroundStyle(Color.primary.opacity(0.18))
                    .lineStyle(StrokeStyle(lineWidth: 1))
            }

            if let empty = outlook?.emptyAt, empty <= to, empty > now {
                PointMark(x: .value("Empty", empty), y: .value("Left", 0))
                    .symbolSize(28)
                    .foregroundStyle(Palette.critical)
            }

            if let hovered, let hit = value(at: hovered) {
                RuleMark(x: .value("Hover", hit.0))
                    .foregroundStyle(Color.primary.opacity(0.4))
                    .lineStyle(StrokeStyle(lineWidth: 1))
                PointMark(x: .value("Hover", hit.0), y: .value("Left", hit.1))
                    .symbolSize(26)
                    .foregroundStyle(hit.0 > now ? Color.primary.opacity(0.6) : tone.color)
            }
        }
        .chartXScale(domain: from...max(to, now.addingTimeInterval(1)))
        // A little room below zero, so a window sitting at 0% still shows its line.
        .chartYScale(domain: -4...100)
        // Hover only: a click selects nothing, so it cannot clear the readout.
        .chartOverlay { proxy in
            GeometryReader { geometry in
                let plot = proxy.plotFrame.map { geometry[$0] } ?? .zero
                Rectangle()
                    .fill(.clear)
                    .contentShape(Rectangle())
                    .onContinuousHover { phase in
                        switch phase {
                        case .active(let location):
                            let x = location.x - plot.minX
                            if x >= 0, x <= plot.width, let date: Date = proxy.value(atX: x) {
                                hovered = date
                            } else {
                                hovered = nil
                            }
                        case .ended:
                            hovered = nil
                        }
                    }

                if let hovered, let hit = value(at: hovered), let x = proxy.position(forX: hit.0) {
                    // Kept inside the plot, and below the line when the line is high.
                    let half = readoutWidth / 2
                    readout(at: hit.0, level: hit.1)
                        .fixedSize()
                        .onGeometryChange(for: CGFloat.self) { $0.size.width } action: { readoutWidth = $0 }
                        .position(x: min(max(plot.minX + x, plot.minX + half), max(plot.maxX - half, plot.minX + half)),
                                  y: hit.1 > 70 ? plot.maxY - 12 : plot.minY + 12)
                        .allowsHitTesting(false)
                }
            }
        }
        .chartYAxis {
            AxisMarks(position: .trailing, values: [0, 50, 100]) { value in
                AxisGridLine(stroke: StrokeStyle(lineWidth: 0.5, dash: [2, 3]))
                    .foregroundStyle(Color.primary.opacity(0.16))
                if showsAxes {
                    AxisValueLabel {
                        if let v = value.as(Int.self) {
                            Text("\(v)").font(.system(size: 10)).foregroundStyle(Color.primary.opacity(0.75))
                        }
                    }
                }
            }
        }
        .chartXAxis {
            if showsAxes {
                AxisMarks(preset: .aligned, values: .automatic(desiredCount: 4)) { _ in
                    AxisValueLabel(format: days > 8 ? .dateTime.day().month(.abbreviated)
                                        : days < 2 ? .dateTime.hour().minute() : .dateTime.weekday(.abbreviated))
                        .font(.system(size: 10))
                        .foregroundStyle(Color.primary.opacity(0.75))
                }
            }
        }
        .chartPlotStyle { plot in
            plot.clipped()
        }
        .frame(height: height)
        .overlay {
            if history.count < 2 {
                Text("No history yet")
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
            }
        }
    }
}

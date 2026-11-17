import SwiftUI

/// What is left of a window as a lit bar, with a tick where an even spread
/// over the cycle would be.
struct UsageBar: View {
    let percent: Double
    var even: Double?
    var height: CGFloat = 7

    private var fraction: Double { min(max(percent / 100, 0), 1) }

    var body: some View {
        let severity = Severity(percent)
        GeometryReader { proxy in
            let width = proxy.size.width
            ZStack(alignment: .leading) {
                Capsule()
                    .fill(.primary.opacity(0.14))
                    .frame(height: height)

                Capsule()
                    .fill(severity.gradient)
                    .frame(width: percent <= 0 ? 0 : max(height, width * fraction), height: height)
                    .shadow(color: severity.color.opacity(0.55), radius: 5, x: 0, y: 0)
                    .opacity(percent <= 0 ? 0 : 1)
            }
            .frame(width: width, height: height, alignment: .leading)
            .overlay(alignment: .leading) {
                if let even {
                    Capsule()
                        .fill(.primary.opacity(0.75))
                        .frame(width: 2, height: height + 6)
                        .offset(x: min(max(width * even / 100 - 1, 0), width - 2))
                        .help("Even pace: \(Format.percent(even)) left")
                }
            }
        }
        .frame(height: height)
        .animation(.smooth(duration: 0.5), value: percent)
        .accessibilityElement()
        .accessibilityLabel("\(Format.percent(percent)) left")
    }
}

/// "Ahead of pace" with a flame, "Under pace" with a leaf.
struct PaceBadge: View {
    let pace: Forecast.Pace
    var compact = false

    private var tint: Color {
        switch pace {
        case .empty: Palette.critical
        case .ahead: Palette.warning
        case .on: .secondary
        case .under: Palette.normal
        }
    }

    var body: some View {
        // The hue goes on the symbol and the capsule; the words stay in the
        // label colour, so they read on whatever the glass shows through.
        HStack(spacing: 3) {
            Image(systemName: pace.symbol)
                .font(.system(size: compact ? 8.5 : 9, weight: .bold))
                .foregroundStyle(tint)
            if !compact {
                Text(pace.text)
                    .font(.system(size: 10, weight: .semibold))
                    .foregroundStyle(.primary)
                    .lineLimit(1)
                    .fixedSize()
            }
        }
        .padding(.horizontal, compact ? 4 : 7)
        .padding(.vertical, compact ? 2.5 : 3)
        .background(tint.opacity(0.24), in: Capsule())
        .overlay(Capsule().strokeBorder(tint.opacity(0.45), lineWidth: 0.5))
        .help(pace.help)
    }
}

/// A small label in a capsule: a plan's name, "Sample", "Earlier reading".
struct Chip: View {
    let text: String
    var tint: Color = .secondary
    var symbol: String?

    var body: some View {
        HStack(spacing: 3) {
            if let symbol {
                Image(systemName: symbol).font(.system(size: 8.5, weight: .bold))
            }
            Text(text)
                .font(.system(size: 10, weight: .semibold))
                .lineLimit(1)
        }
        .foregroundStyle(tint)
        .padding(.horizontal, 7)
        .padding(.vertical, 2.5)
        .background(.primary.opacity(0.07), in: Capsule())
        .overlay(Capsule().strokeBorder(.primary.opacity(0.08), lineWidth: 0.5))
    }
}

/// A ring showing how much is left, with whatever goes in the middle.
struct Ring<Center: View>: View {
    let percent: Double?
    var size: CGFloat = 34
    var lineWidth: CGFloat = 3
    @ViewBuilder var center: () -> Center

    private var fraction: Double { max(0.001, min(1, (percent ?? 0) / 100)) }

    var body: some View {
        let severity = Severity(percent ?? 100)
        ZStack {
            Circle()
                .stroke(.primary.opacity(0.1), lineWidth: lineWidth)

            if let percent, percent > 0 {
                Circle()
                    .trim(from: 0, to: fraction)
                    .stroke(
                        AngularGradient(colors: [severity.color, severity.light], center: .center,
                                        startAngle: .degrees(0), endAngle: .degrees(360 * fraction)),
                        style: StrokeStyle(lineWidth: lineWidth, lineCap: .round)
                    )
                    .rotationEffect(.degrees(-90))
                    .shadow(color: severity.color.opacity(0.5), radius: lineWidth * 1.2)
            }

            center()
        }
        .frame(width: size, height: size)
        .animation(.smooth(duration: 0.6), value: percent)
    }
}

/// A row's figure: "62%" and, quieter, "left".
struct LevelText: View {
    let percent: Double?
    var size: CGFloat = 13

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 2) {
            Text(percent.map(Format.percent) ?? "—")
                .font(.system(size: size, weight: .semibold, design: .rounded))
                .monospacedDigit()
                .foregroundStyle(percent.map(Severity.textColor) ?? .secondary)
                .contentTransition(.numericText(value: percent ?? 0))
            if percent != nil {
                Text("left")
                    .font(.system(size: size * 0.78, weight: .medium))
                    .foregroundStyle(.secondary)
            }
        }
    }
}

/// The panel is the one sheet of glass. Controls on it are a soft fill and a
/// hairline, as the cards are, never a second layer of glass that cannot
/// lens anything and comes out as a grey disc.
struct SoftButtonStyle: ButtonStyle {
    var circle = false

    func makeBody(configuration: Configuration) -> some View {
        SoftButton(configuration: configuration, circle: circle)
    }

    private struct SoftButton: View {
        let configuration: Configuration
        let circle: Bool
        @Environment(\.isEnabled) private var enabled
        @State private var hovering = false

        var body: some View {
            let shape = circle ? AnyShape(Circle()) : AnyShape(Capsule())
            configuration.label
                .font(.system(size: 11.5, weight: .medium))
                .padding(.horizontal, circle ? 0 : 11)
                .padding(.vertical, circle ? 0 : 5)
                .background(.primary.opacity(configuration.isPressed ? 0.2 : hovering ? 0.14 : 0.09), in: shape)
                .overlay(shape.stroke(.primary.opacity(0.12), lineWidth: 0.5))
                .contentShape(shape)
                .opacity(enabled ? 1 : 0.45)
                .scaleEffect(configuration.isPressed ? 0.96 : 1)
                .animation(.snappy(duration: 0.15), value: configuration.isPressed)
                .onHover { hovering = $0 }
        }
    }
}

extension ButtonStyle where Self == SoftButtonStyle {
    static var soft: SoftButtonStyle { SoftButtonStyle() }
    static var softCircle: SoftButtonStyle { SoftButtonStyle(circle: true) }
}

/// A button that is just a symbol in a circle.
struct GlassIconButton: View {
    let symbol: String
    let help: String
    var spinning = false
    /// Its diameter: a true circle, not a capsule padded to look like one.
    var size: CGFloat = 30
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            // Spun by hand about the frame's centre, not with the symbol
            // effect, which turns a glyph about its own (off-centre) anchor.
            TimelineView(.animation(paused: !spinning)) { timeline in
                let turn = timeline.date.timeIntervalSinceReferenceDate.truncatingRemainder(dividingBy: 1.1) / 1.1
                Image(systemName: symbol)
                    .font(.system(size: size * 0.4, weight: .semibold))
                    .frame(width: size, height: size)
                    .rotationEffect(.degrees(spinning ? turn * 360 : 0))
            }
            .frame(width: size, height: size)
        }
        .buttonStyle(.softCircle)
        .help(help)
        .accessibilityLabel(help)
    }
}

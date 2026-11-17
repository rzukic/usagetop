import SwiftUI

/// The apps' shared palette, from `my-usage-server/src/AiUsage.Server/Web/app.css`.
/// Chrome takes no hue of its own — on glass it takes the system's label and
/// fill colours, so it reads in light and dark alike. A hue means data or an
/// alarm, never decoration.
enum Palette {
    static let normal = Color(hex: 0x6B93C9)
    static let warning = Color(hex: 0xE0A33C)
    static let critical = Color(hex: 0xE05A50)

    /// A brighter partner for each, for the lit end of a bar.
    static let normalLight = Color(hex: 0x9CC0F0)
    static let warningLight = Color(hex: 0xF4CB7A)
    static let criticalLight = Color(hex: 0xF59088)
}

/// One place for the rule that a limit is blue until it warns, then amber, then red.
enum Severity {
    case normal, warning, critical

    init(_ percent: Double) {
        self = percent <= 15 ? .critical : percent <= 40 ? .warning : .normal
    }

    var color: Color {
        switch self {
        case .normal: Palette.normal
        case .warning: Palette.warning
        case .critical: Palette.critical
        }
    }

    var light: Color {
        switch self {
        case .normal: Palette.normalLight
        case .warning: Palette.warningLight
        case .critical: Palette.criticalLight
        }
    }

    var gradient: LinearGradient {
        LinearGradient(colors: [color, light], startPoint: .leading, endPoint: .trailing)
    }

    static func color(for percent: Double) -> Color { Severity(percent).color }

    /// Only a warning or a critical figure is coloured; a healthy one stays quiet.
    static func textColor(for percent: Double) -> Color {
        let severity = Severity(percent)
        return severity == .normal ? .primary : severity.color
    }
}

extension Color {
    init(hex: UInt32) {
        self.init(
            .sRGB,
            red: Double((hex >> 16) & 0xFF) / 255,
            green: Double((hex >> 8) & 0xFF) / 255,
            blue: Double(hex & 0xFF) / 255,
            opacity: 1
        )
    }
}

/// A surface for content that sits on the glass panel: a faint fill and a
/// hairline, never a second layer of glass. How solid it is is a setting of
/// its own (Settings › Look › Tiles), apart from the panel's glass: the tiles
/// can be made solid for reading while the panel around them stays clear.
struct CardSurface: ViewModifier {
    var radius: CGFloat = 18
    var highlighted = false
    @Environment(AppModel.self) private var model
    @Environment(\.colorScheme) private var scheme

    /// A solid tile: a step off the window colour, so it still stands apart
    /// from a panel that has been made flat.
    private var solid: Color {
        scheme == .dark ? Color(white: highlighted ? 0.21 : 0.18) : Color(white: highlighted ? 0.97 : 1)
    }

    /// Enough of a floor for small grey text over a busy desktop: a lift of
    /// white in both schemes, stronger in light where the glass is paler.
    private var fill: Color {
        scheme == .dark
            ? .white.opacity(highlighted ? 0.15 : 0.11)
            : .white.opacity(highlighted ? 0.62 : 0.5)
    }

    func body(content: Content) -> some View {
        content
            .background {
                let shape = RoundedRectangle(cornerRadius: radius, style: .continuous)
                ZStack {
                    shape.fill(solid.opacity(model.settings.tiles))
                    shape.fill(fill)
                }
            }
            .overlay {
                RoundedRectangle(cornerRadius: radius, style: .continuous)
                    .strokeBorder(
                        LinearGradient(
                            colors: [.white.opacity(0.16), .white.opacity(0.03)],
                            startPoint: .top, endPoint: .bottom
                        ),
                        lineWidth: 0.75
                    )
            }
    }
}

extension View {
    func card(radius: CGFloat = 18, highlighted: Bool = false) -> some View {
        modifier(CardSurface(radius: radius, highlighted: highlighted))
    }
}

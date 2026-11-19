using Avalonia.Media;

namespace AiUsage.Ui;

/// <summary>
/// The apps' shared palette, from the Mac app's Look.swift. Chrome takes no hue
/// of its own: it is the label colour at some strength, so it reads in light and
/// dark alike. A hue means data or an alarm, never decoration.
/// </summary>
public static class Look
{
    /// <summary>Follows the desktop's light or dark setting; the app sets it before drawing.</summary>
    public static bool Dark { get; set; } = true;

    public static readonly Color Normal = Color.FromRgb(0x6B, 0x93, 0xC9);
    public static readonly Color Warning = Color.FromRgb(0xE0, 0xA3, 0x3C);
    public static readonly Color Critical = Color.FromRgb(0xE0, 0x5A, 0x50);

    /// <summary>A brighter partner for each, for the lit end of a bar.</summary>
    public static readonly Color NormalLight = Color.FromRgb(0x9C, 0xC0, 0xF0);
    public static readonly Color WarningLight = Color.FromRgb(0xF4, 0xCB, 0x7A);
    public static readonly Color CriticalLight = Color.FromRgb(0xF5, 0x90, 0x88);

    public static readonly Color Fresh = Color.FromRgb(0x5B, 0xC4, 0x8A);

    /// <summary>The label colour: what every text and glyph is a strength of.</summary>
    public static Color InkColor => Dark ? Color.FromRgb(0xF5, 0xF5, 0xF7) : Color.FromRgb(0x1B, 0x1C, 0x1F);

    public static IBrush Primary => Ink(Dark ? 0.96 : 0.92);

    public static IBrush Secondary => Ink(Dark ? 0.58 : 0.56);

    public static IBrush Ink(double strength) => new SolidColorBrush(InkColor, strength);

    public static IBrush Solid(Color color, double opacity = 1) => new SolidColorBrush(color, opacity);

    /// <summary>The panel itself, flat: the window colour, a shade deeper toward the bottom.</summary>
    public static (Color Top, Color Bottom) Panel => Dark
        ? (Color.FromRgb(0x25, 0x27, 0x2E), Color.FromRgb(0x1B, 0x1C, 0x21))
        : (Color.FromRgb(0xF4, 0xF5, 0xF8), Color.FromRgb(0xE9, 0xEA, 0xEE));

    /// <summary>A tile on the panel: a lift of white, stronger in light where the panel is paler.</summary>
    public static IBrush Tile(bool highlighted = false) => new SolidColorBrush(
        Colors.White,
        Dark ? (highlighted ? 0.105 : 0.07) : (highlighted ? 0.82 : 0.62));

    /// <summary>The hairline round a tile: light from above.</summary>
    public static IBrush TileEdge => new LinearGradientBrush
    {
        StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
        EndPoint = new Avalonia.RelativePoint(0, 1, Avalonia.RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(Dark ? (byte)0x29 : (byte)0xB0, 0xFF, 0xFF, 0xFF), 0),
            new GradientStop(Color.FromArgb(Dark ? (byte)0x08 : (byte)0x30, Dark ? (byte)0xFF : (byte)0x00, Dark ? (byte)0xFF : (byte)0x00, Dark ? (byte)0xFF : (byte)0x00), 1),
        },
    };

    // ---- Severity: blue until it warns, then amber, then red ----

    public static Color ColorOf(double percent) => percent <= 15 ? Critical : percent <= 40 ? Warning : Normal;

    public static Color LightOf(double percent) => percent <= 15 ? CriticalLight : percent <= 40 ? WarningLight : NormalLight;

    /// <summary>Only a warning or a critical figure is coloured; a healthy one stays quiet.</summary>
    public static IBrush TextOf(double percent) => percent <= 40 ? Solid(ColorOf(percent)) : Primary;
}

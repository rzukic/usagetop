using System.Windows.Media;

namespace AiUsage.Ui;

/// <summary>One place for the rule that a limit is blue until it warns, then orange, then red.</summary>
public static class Severity
{
    public static readonly Color Normal = Color.FromRgb(0x6B, 0x93, 0xC9);
    public static readonly Color Warning = Color.FromRgb(0xE0, 0xA3, 0x3C);
    public static readonly Color Critical = Color.FromRgb(0xE0, 0x5A, 0x50);

    private static readonly Color Primary = Color.FromRgb(0xF2, 0xF2, 0xF2);

    private static readonly Brush NormalBrush = Frozen(Normal);
    private static readonly Brush WarningBrush = Frozen(Warning);
    private static readonly Brush CriticalBrush = Frozen(Critical);
    private static readonly Brush PrimaryBrush = Frozen(Primary);

    public static Color ColorFor(float percent) => percent switch
    {
        <= 15f => Critical,
        <= 40f => Warning,
        _ => Normal,
    };

    public static Brush MeterBrush(float percent) => percent switch
    {
        <= 15f => CriticalBrush,
        <= 40f => WarningBrush,
        _ => NormalBrush,
    };

    /// <summary>Only a warning or a critical figure is coloured; a healthy one stays quiet.</summary>
    public static Brush TextBrush(float percent) => percent switch
    {
        <= 15f => CriticalBrush,
        <= 40f => WarningBrush,
        _ => PrimaryBrush,
    };

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

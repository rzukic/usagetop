using System.Windows;
using System.Windows.Media;

namespace AiUsage.Ui;

/// <summary>
/// What is left of a window as a lit bar, with a tick where an even spread over
/// the cycle would be: the Mac panel's bar. The fill runs from the severity's
/// colour into a lighter one, with a soft glow; nothing is drawn at 0%.
/// </summary>
public sealed class PaceMeter : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(double), typeof(PaceMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EvenProperty = DependencyProperty.Register(
        nameof(Even), typeof(double?), typeof(PaceMeter),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>How much is left, 0-100.</summary>
    public double Percent
    {
        get => (double)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    /// <summary>Where an even spread would be now, 0-100; null for no tick.</summary>
    public double? Even
    {
        get => (double?)GetValue(EvenProperty);
        set => SetValue(EvenProperty, value);
    }

    private static readonly Brush Track = Frozen(new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush Tick = Frozen(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)));

    public PaceMeter()
    {
        Height = 7;
        SnapsToDevicePixels = true;
    }

    protected override void OnRender(DrawingContext context)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var radius = height / 2;
        context.DrawRoundedRectangle(Track, null, new Rect(0, 0, width, height), radius, radius);

        var percent = Math.Clamp(Percent, 0, 100);
        if (percent > 0)
        {
            var (dark, light) = Colours(percent);
            var filled = Math.Max(height, width * percent / 100);

            // A soft glow: the same shape, wider and faint, under the fill.
            var glow = new SolidColorBrush(Color.FromArgb(0x38, dark.R, dark.G, dark.B));
            glow.Freeze();
            context.DrawRoundedRectangle(glow, null, new Rect(-2, -2, filled + 4, height + 4), radius + 2, radius + 2);

            var fill = new LinearGradientBrush(dark, light, new Point(0, 0.5), new Point(1, 0.5));
            fill.Freeze();
            context.DrawRoundedRectangle(fill, null, new Rect(0, 0, filled, height), radius, radius);
        }

        if (Even is { } even)
        {
            var x = Math.Clamp(width * even / 100 - 1, 0, width - 2);
            context.DrawRoundedRectangle(Tick, null, new Rect(x, -3, 2, height + 6), 1, 1);
        }
    }

    /// <summary>Blue while healthy, amber under 40%, red under 15%: a colour and a lighter partner.</summary>
    public static (Color Dark, Color Light) Colours(double percent) => percent switch
    {
        <= 15 => (Severity.Critical, Color.FromRgb(0xF5, 0x90, 0x88)),
        <= 40 => (Severity.Warning, Color.FromRgb(0xF4, 0xCB, 0x7A)),
        _ => (Severity.Normal, Color.FromRgb(0x9C, 0xC0, 0xF0)),
    };

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}

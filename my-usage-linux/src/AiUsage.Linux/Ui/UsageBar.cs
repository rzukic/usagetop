using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AiUsage.Ui;

/// <summary>
/// What is left of a window as a lit bar, with a tick where an even spread over
/// the cycle would be.
/// </summary>
public sealed class UsageBar : Control
{
    private const double Bar = 7;
    private const double TickOver = 3;

    private readonly double _percent;
    private readonly double? _even;

    public UsageBar(double percent, double? even = null)
    {
        _percent = percent;
        _even = even;
        Height = Bar + TickOver * 2;
        if (even is { } level) ToolTip.SetTip(this, $"Even pace: {Format.Percent((float)level)} left");
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        if (width <= 0) return;
        var top = (Bounds.Height - Bar) / 2;
        var radius = Bar / 2;

        context.DrawRectangle(Look.Ink(0.14), null, new RoundedRect(new Rect(0, top, width, Bar), radius));

        if (_percent > 0)
        {
            var fraction = Math.Clamp(_percent / 100, 0, 1);
            var lit = new Rect(0, top, Math.Max(Bar, width * fraction), Bar);
            var colour = Look.ColorOf(_percent);
            var fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(colour, 0), new GradientStop(Look.LightOf(_percent), 1) },
            };
            var glow = new BoxShadows(new BoxShadow { Blur = 7, Color = Color.FromArgb(0x8C, colour.R, colour.G, colour.B) });
            context.DrawRectangle(fill, null, new RoundedRect(lit, radius), glow);
        }

        if (_even is { } even)
        {
            var x = Math.Clamp(width * even / 100 - 1, 0, width - 2);
            context.DrawRectangle(Look.Ink(0.75), null, new RoundedRect(new Rect(x, 0, 2, Bounds.Height), 1));
        }
    }
}

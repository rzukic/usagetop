using System.Windows;
using System.Windows.Controls;

namespace AiUsage.Ui;

/// <summary>
/// A border whose ends are always true half-circles. WPF draws a corner radius
/// larger than half the height as a squashed ellipse rather than clamping it, so
/// a pill asked for with a radius of 999 comes out as a lozenge; this one sets
/// its radius from its own height instead.
/// </summary>
public sealed class Capsule : Border
{
    public Capsule()
    {
        SizeChanged += (_, e) =>
        {
            var radius = e.NewSize.Height / 2;
            CornerRadius = new CornerRadius(radius);
        };
    }
}

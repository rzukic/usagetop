using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AiUsage.Ui;

/// <summary>
/// A ring showing how much is left, with whatever goes in the middle: the arc
/// starts at twelve o'clock, runs clockwise from the level's colour to its
/// brighter partner, and glows a little, as on the Mac.
/// </summary>
public sealed class Ring : Panel
{
    public Ring(double? percent, double size, double lineWidth, Control? centre = null)
    {
        Width = Height = size;
        // The glow is the arc again, blurred, underneath.
        if (percent is > 0)
        {
            Children.Add(new Arc(percent, lineWidth, track: false)
            {
                Opacity = 0.5,
                Effect = new BlurEffect { Radius = lineWidth * 1.6 },
            });
        }
        Children.Add(new Arc(percent, lineWidth, track: true));
        if (centre != null)
        {
            centre.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
            centre.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
            Children.Add(centre);
        }
    }

    private sealed class Arc(double? percent, double lineWidth, bool track) : Control
    {
        public override void Render(DrawingContext context)
        {
            var size = Math.Min(Bounds.Width, Bounds.Height);
            var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
            var radius = (size - lineWidth) / 2;
            if (radius <= 0) return;

            if (track) context.DrawEllipse(null, new Pen(Look.Ink(0.1), lineWidth), centre, radius, radius);
            if (percent is not > 0) return;

            var fraction = Math.Clamp(percent.Value / 100, 0.001, 1);
            var from = Look.ColorOf(percent.Value);
            var to = Look.LightOf(percent.Value);
            var brush = new ConicGradientBrush
            {
                Center = new RelativePoint(centre, RelativeUnit.Absolute),
                Angle = 0,
                GradientStops =
                {
                    new GradientStop(from, 0),
                    new GradientStop(to, fraction),
                    // Past the arc's end nothing is drawn; closing the circle on
                    // the first colour keeps a round cap at twelve from changing hue.
                    new GradientStop(from, 1),
                },
            };
            var pen = new Pen(brush, lineWidth, lineCap: PenLineCap.Round);

            if (fraction >= 0.999)
            {
                context.DrawEllipse(null, pen, centre, radius, radius);
                return;
            }

            var angle = fraction * 2 * Math.PI;
            var start = new Point(centre.X, centre.Y - radius);
            var end = new Point(centre.X + radius * Math.Sin(angle), centre.Y - radius * Math.Cos(angle));
            var arc = new StreamGeometry();
            using (var path = arc.Open())
            {
                path.BeginFigure(start, false);
                path.ArcTo(end, new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise);
                path.EndFigure(false);
            }
            context.DrawGeometry(null, pen, arc);
        }
    }
}

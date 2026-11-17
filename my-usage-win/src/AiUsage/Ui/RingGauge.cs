using System.Windows;
using System.Windows.Media;

namespace AiUsage.Ui;

/// <summary>
/// The level ring: a dim track, a severity-coloured arc for the share that is
/// left, and the provider's mark in the middle. The phone rasterises this into a
/// bitmap because RemoteViews cannot draw an arc; here it is just a drawing.
/// </summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent),
        typeof(double?),
        typeof(RingGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush),
        typeof(Brush),
        typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush),
        typeof(Brush),
        typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LogoProperty = DependencyProperty.Register(
        nameof(Logo),
        typeof(Geometry),
        typeof(RingGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LogoBrushProperty = DependencyProperty.Register(
        nameof(LogoBrush),
        typeof(Brush),
        typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>How much of the window is left, 0-100. Null draws the track alone.</summary>
    public double? Percent
    {
        get => (double?)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public Brush FillBrush
    {
        get => (Brush)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Geometry? Logo
    {
        get => (Geometry?)GetValue(LogoProperty);
        set => SetValue(LogoProperty, value);
    }

    public Brush LogoBrush
    {
        get => (Brush)GetValue(LogoBrushProperty);
        set => SetValue(LogoBrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = double.IsInfinity(availableSize.Width) || double.IsInfinity(availableSize.Height)
            ? 40.0
            : Math.Min(availableSize.Width, availableSize.Height);

        return side is > 0 and < double.PositiveInfinity ? new Size(side, side) : new Size(40, 40);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var side = Math.Min(width, height);
        var thickness = side * 0.10;
        var radius = (side - thickness) / 2;
        var centre = new Point(width / 2, height / 2);

        var track = new Pen(TrackBrush, thickness);
        dc.DrawEllipse(null, track, centre, radius, radius);

        var percent = Percent;
        if (percent is { } value && value > 0)
        {
            // A full ring would start and end on the same point, which draws
            // nothing, so the sweep stops a hair short of a full turn.
            var sweep = Math.Clamp(value, 0, 100) / 100 * 359.9;
            var geometry = Arc(centre, radius, sweep);

            // A soft glow in the arc's own colour under it, as the Mac ring has.
            if (FillBrush is SolidColorBrush { Color: var colour })
            {
                foreach (var (spread, alpha) in new[] { (2.6, (byte)0x14), (1.7, (byte)0x26) })
                {
                    var glow = new Pen(new SolidColorBrush(Color.FromArgb(alpha, colour.R, colour.G, colour.B)), thickness * spread)
                    {
                        StartLineCap = PenLineCap.Round,
                        EndLineCap = PenLineCap.Round,
                    };
                    dc.DrawGeometry(null, glow, geometry);
                }
            }

            var arc = new Pen(FillBrush, thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            dc.DrawGeometry(null, arc, geometry);
        }

        if (Logo is { } logo && logo.Bounds is { Width: > 0, Height: > 0 } bounds)
        {
            var target = side * 0.42;
            var scale = target / Math.Max(bounds.Width, bounds.Height);
            var offsetX = (width - bounds.Width * scale) / 2 - bounds.X * scale;
            var offsetY = (height - bounds.Height * scale) / 2 - bounds.Y * scale;

            dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, offsetX, offsetY));
            dc.DrawGeometry(LogoBrush, null, logo);
            dc.Pop();
        }
    }

    /// <summary>An arc from twelve o'clock, clockwise, sweeping the given degrees.</summary>
    private static Geometry Arc(Point centre, double radius, double sweep)
    {
        var start = new Point(centre.X, centre.Y - radius);
        var radians = (sweep - 90) * Math.PI / 180;
        var end = new Point(
            centre.X + radius * Math.Cos(radians),
            centre.Y + radius * Math.Sin(radians));

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(
                end,
                new Size(radius, radius),
                0,
                sweep > 180,
                SweepDirection.Clockwise,
                true,
                false);
        }
        geometry.Freeze();
        return geometry;
    }
}

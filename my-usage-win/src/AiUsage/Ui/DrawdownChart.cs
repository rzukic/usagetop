using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using AiUsage.Data;

namespace AiUsage.Ui;

/// <summary>
/// A window's remaining level over time -- the drawdown. Percent runs 0-100 up the
/// vertical axis, time along the horizontal one.
///
/// With a <see cref="Forecast"/> it is a single window's card, drawn as the server
/// page draws it: the readings up to now, the forecast running on past now to
/// where the window runs dry or refills, and the even spread through the cycle.
/// With a <see cref="Span"/> it becomes the combined view: the axis is the picked
/// range with now in the middle, the left half is the history with a dotted line
/// at every refill, and the right half carries every window's forecast forward
/// plus a marker at every reset still to come. With neither, the axis covers
/// exactly the readings it was given.
/// </summary>
public sealed class DrawdownChart : FrameworkElement
{
    /// <summary>A window reset still to come. <see cref="Providers"/> is who refills.</summary>
    public sealed record Reset(DateTimeOffset At, IReadOnlyList<ProviderId> Providers);

    private static readonly Brush GridBrush = Frozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly Brush HintBrush = Frozen(Color.FromRgb(0x7A, 0x7A, 0x7A));
    private static readonly Brush SecondaryBrush = Frozen(Color.FromRgb(0x9B, 0x9B, 0x9B));
    private static readonly Brush TertiaryBrush = Frozen(Color.FromRgb(0x7A, 0x7A, 0x7A));

    private static readonly Pen GridPen = FrozenPen(GridBrush, 1);
    private static readonly Pen RefillPen = FrozenPen(TertiaryBrush, 1, [2, 3]);
    private static readonly Pen ResetPen = FrozenPen(SecondaryBrush, 1, [4, 4]);
    private static readonly Pen NowPen = FrozenPen(TertiaryBrush, 1);
    private static readonly Pen TrendPen = FrozenPen(SecondaryBrush, 1.5, [5, 4]);
    private static readonly Pen EvenPen = FrozenPen(TertiaryBrush, 1, [3, 3]);
    private static readonly Brush PanelBrush = Frozen(Color.FromRgb(0x14, 0x14, 0x14));
    private static readonly Brush LabelBrush = Frozen(Color.FromRgb(0xF2, 0xF2, 0xF2));
    private static readonly Brush PlateBrush = Frozen(Color.FromArgb(0xE6, 0x16, 0x16, 0x1A));


    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points),
        typeof(IReadOnlyList<UsagePoint>),
        typeof(DrawdownChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineColorProperty = DependencyProperty.Register(
        nameof(LineColor),
        typeof(Color),
        typeof(DrawdownChart),
        new FrameworkPropertyMetadata(Severity.Normal, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SpanProperty = DependencyProperty.Register(
        nameof(Span),
        typeof(TimeSpan?),
        typeof(DrawdownChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ResetsProperty = DependencyProperty.Register(
        nameof(Resets),
        typeof(IReadOnlyList<Reset>),
        typeof(DrawdownChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ProjectionProperty = DependencyProperty.Register(
        nameof(Projection),
        typeof(IReadOnlyList<UsagePoint>),
        typeof(DrawdownChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForecastProperty = DependencyProperty.Register(
        nameof(Forecast),
        typeof(Data.Projection.Track),
        typeof(DrawdownChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AheadProperty = DependencyProperty.Register(
        nameof(Ahead),
        typeof(TimeSpan),
        typeof(DrawdownChart),
        new FrameworkPropertyMetadata(TimeSpan.FromDays(7), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AxisProperty = DependencyProperty.Register(
        nameof(Axis),
        typeof(bool),
        typeof(DrawdownChart),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// For a chart fitted to its readings: the first day and "now" along the
    /// bottom, in place of the length of history in the corner.
    /// </summary>
    public bool Axis
    {
        get => (bool)GetValue(AxisProperty);
        set => SetValue(AxisProperty, value);
    }

    public IReadOnlyList<UsagePoint>? Points
    {
        get => (IReadOnlyList<UsagePoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public Color LineColor
    {
        get => (Color)GetValue(LineColorProperty);
        set => SetValue(LineColorProperty, value);
    }

    public TimeSpan? Span
    {
        get => (TimeSpan?)GetValue(SpanProperty);
        set => SetValue(SpanProperty, value);
    }

    public IReadOnlyList<Reset>? Resets
    {
        get => (IReadOnlyList<Reset>?)GetValue(ResetsProperty);
        set => SetValue(ResetsProperty, value);
    }

    public IReadOnlyList<UsagePoint>? Projection
    {
        get => (IReadOnlyList<UsagePoint>?)GetValue(ProjectionProperty);
        set => SetValue(ProjectionProperty, value);
    }

    /// <summary>A single window's own forecast; switches the chart to the window card.</summary>
    public Data.Projection.Track? Forecast
    {
        get => (Data.Projection.Track?)GetValue(ForecastProperty);
        set => SetValue(ForecastProperty, value);
    }

    /// <summary>How far past now a window card may run: the span its history covers.</summary>
    public TimeSpan Ahead
    {
        get => (TimeSpan)GetValue(AheadProperty);
        set => SetValue(AheadProperty, value);
    }

    /// <summary>
    /// When the combined level reaches zero, or null when it stays above it for the
    /// whole range. The projection already carries every window's own burn and every
    /// refill, so this is just the first sample that is empty.
    /// </summary>
    public DateTimeOffset? ProjectedExhaustion() =>
        Projection?.FirstOrDefault(p => p.Percent <= 0f) is { } point && Projection!.Count > 0
            ? point.At
            : null;

    protected override void OnRender(DrawingContext context)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        if (Span is { } span) DrawSpan(context, width, height, span);
        else if (Forecast is { } forecast) DrawWindow(context, width, height, forecast);
        else DrawFitted(context, width, height);
    }

    /// <summary>A single window's card: the axis is exactly the readings it was given.</summary>
    private void DrawFitted(DrawingContext context, double width, double chartHeight)
    {
        var height = Axis ? chartHeight - 16 : chartHeight;
        DrawGrid(context, width, height);

        var points = Points ?? [];
        if (points.Count == 0)
        {
            DrawCollecting(context, width, height / 2);
            return;
        }

        var start = points[0].At;
        var end = points[^1].At;
        var total = Math.Max(1.0, (end - start).TotalMilliseconds);

        double XOf(DateTimeOffset at) => (at - start).TotalMilliseconds / total * width;
        double YOf(float percent) => height - Math.Clamp(percent, 0f, 100f) / 100.0 * height;

        // One reading cannot be a line, but it is still a reading: show it as a dot
        // instead of claiming there is nothing yet.
        if (points.Count == 1)
        {
            context.DrawEllipse(LineBrush(), null, new Point(width / 2, YOf(points[0].Percent)), 3, 3);
            return;
        }

        DrawSeries(context, points, XOf, YOf, height);

        if (Axis)
        {
            DrawText(context, Format.Day(start), 0, chartHeight - 2, TertiaryBrush, TextAlign.Left, 11);
            DrawText(context, "now", width, chartHeight - 2, TertiaryBrush, TextAlign.Right, 11);
            return;
        }

        // Without a fixed span the axis stretches to fit the readings, so say how
        // much history that actually is: 7m and 7d would otherwise look the same.
        DrawText(context, Format.Span(end - start), width, 14, TertiaryBrush, TextAlign.Right, 11);
    }

    /// <summary>
    /// A single window's card, as the server page draws it. The readings run from
    /// the oldest kept up to now; past now the forecast goes on half as far again
    /// as the history, or on to where the window runs dry, but never past its
    /// reset or the span the history covers.
    /// </summary>
    private void DrawWindow(DrawingContext context, double width, double height, Data.Projection.Track track)
    {
        const double axis = 16;
        var plot = height - axis;
        DrawGrid(context, width, plot);

        var points = Points ?? [];
        if (points.Count < 2)
        {
            if (points.Count == 1)
            {
                context.DrawEllipse(LineBrush(), null, new Point(width / 2, plot - points[0].Percent / 100.0 * plot), 3, 3);
            }
            else
            {
                DrawCollecting(context, width, plot / 2);
            }
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var from = points[0].At;
        var last = points[^1];
        var until = last.At > now ? last.At : now;

        var empty = Data.Projection.EmptyAt(track, now + Data.Projection.Horizon);
        var to = until;
        if (track.Measured)
        {
            var history = until - from;
            to = until + (history / 2 > TimeSpan.FromHours(1) ? history / 2 : TimeSpan.FromHours(1));
            if (empty is { } dry && dry + (dry - until) * 0.15 > to) to = dry + (dry - until) * 0.15;
            if (to > until + Ahead) to = until + Ahead;
            if (track.NextReset is { } reset && reset > until && reset < to) to = reset;
        }

        var millis = Math.Max(1.0, (to - from).TotalMilliseconds);
        double XOf(DateTimeOffset at) => (at - from).TotalMilliseconds / millis * width;
        double YOf(float percent) => plot - Math.Clamp(percent, 0f, 100f) / 100.0 * plot;

        // The even spread through this cycle: from full at the last refill to empty
        // at the next. Above it the window is being used faster than that.
        if (track.Period is { } length && track.NextReset is { } next && next - now <= length)
        {
            var start = next - length > from ? next - length : from;
            double Even(DateTimeOffset at) => (next - at).TotalMilliseconds / length.TotalMilliseconds * 100.0;
            context.DrawLine(
                EvenPen,
                new Point(XOf(start), YOf((float)Even(start))),
                new Point(XOf(to), YOf((float)Even(to))));
        }

        // The level holds until the next reading, and after the last one until now.
        var held = until > last.At ? points.Append(new UsagePoint(until, last.Percent)).ToList() : points.ToList();
        DrawSeries(context, held, XOf, YOf, plot);

        var nowX = XOf(until);
        var future = to > until;
        if (future)
        {
            context.DrawLine(NowPen, new Point(nowX, 0), new Point(nowX, plot));

            var path = Data.Projection.Path(track, until, to);
            if (path.Count >= 2)
            {
                var line = new StreamGeometry();
                using (var geometry = line.Open())
                {
                    geometry.BeginFigure(new Point(XOf(path[0].At), YOf(path[0].Percent)), false, false);
                    for (var i = 1; i < path.Count; i++)
                    {
                        geometry.LineTo(new Point(XOf(path[i].At), YOf(path[i].Percent)), true, false);
                    }
                }
                line.Freeze();
                context.DrawGeometry(null, FrozenPen(Frozen(WithAlpha(LineColor, 217)), 1.5, [5, 4]), line);
            }

            if (empty is { } dry && dry <= to)
            {
                var ex = XOf(dry);
                context.DrawEllipse(PanelBrush, FrozenPen(LineBrush(), 2), new Point(ex, YOf(0)), 3.5, 3.5);
                var nearRight = ex > width - 90;
                DrawText(
                    context,
                    "empty " + Format.When(dry),
                    nearRight ? ex - 9 : ex + 9,
                    YOf(0) - 7,
                    LabelBrush,
                    nearRight ? TextAlign.Right : TextAlign.Left,
                    11,
                    PlateBrush);
            }
        }

        // Along the bottom: where the history starts, now, and where the forecast ends.
        var shortRange = to - from <= TimeSpan.FromDays(2);
        string Mark(DateTimeOffset at) => shortRange ? Format.Clock(at) : Format.Day(at);
        DrawText(context, "now", nowX, height - 2, TertiaryBrush, future ? TextAlign.Center : TextAlign.Right, 11);
        if (nowX > 70) DrawText(context, Mark(from), 0, height - 2, TertiaryBrush, TextAlign.Left, 11);
        if (future && width - nowX > 70) DrawText(context, Mark(to), width, height - 2, TertiaryBrush, TextAlign.Right, 11);
    }

    /// <summary>
    /// The combined view: the axis is the range the user picked, now sits in the
    /// middle, and the right half is the future the fitted line projects into.
    /// </summary>
    private void DrawSpan(DrawingContext context, double width, double height, TimeSpan span)
    {
        const double axis = 16;
        var plot = height - axis;
        DrawGrid(context, width, plot);

        var now = DateTimeOffset.UtcNow;

        // The axis is exactly the picked range with now in the middle: the left half
        // is what happened, the right half is what the burn rate expects. "7d"
        // therefore means seven days on screen, not seven days plus a strip.
        var half = span / 2;
        var start = now - half;
        var end = now + half;
        var millis = Math.Max(1.0, span.TotalMilliseconds);

        double XOf(DateTimeOffset at) => (at - start).TotalMilliseconds / millis * width;
        double YOf(float percent) => plot - Math.Clamp(percent, 0f, 100f) / 100.0 * plot;

        var visible = (Points ?? [])
            .Where(p => p.At >= start && p.At <= now)
            .OrderBy(p => p.At)
            .ToList();

        if (visible.Count == 0)
        {
            DrawCollecting(context, width, plot / 2);
            return;
        }

        var fit = Trendline.Of(visible);

        // Refills go under the series, so the line stays readable over them.
        if (fit != null)
        {
            foreach (var refill in fit.Refills)
            {
                var x = XOf(refill.At);
                if (x < -2 || x > width + 2) continue;
                context.DrawLine(RefillPen, new Point(x, 0), new Point(x, plot));
            }
        }

        // Resets still to come. These are known dates, not a projection, so they get
        // a marker at the top as well as the line, and the provider's mark above it
        // so it reads as "this one refills" rather than a bare date.
        foreach (var reset in Resets ?? [])
        {
            if (reset.At < now || reset.At > end) continue;
            var x = XOf(reset.At);
            context.DrawLine(ResetPen, new Point(x, 0), new Point(x, plot));

            const double iconSize = 11;
            const double gap = 1;
            var total = reset.Providers.Count * iconSize + (reset.Providers.Count - 1) * gap;
            var left = x - total / 2;

            foreach (var provider in reset.Providers)
            {
                DrawMark(context, provider, left, 0, iconSize);
                left += iconSize + gap;
            }

            var top = iconSize + 1;
            var marker = new StreamGeometry();
            using (var geometry = marker.Open())
            {
                geometry.BeginFigure(new Point(x - 3, top), true, true);
                geometry.LineTo(new Point(x + 3, top), true, false);
                geometry.LineTo(new Point(x, top + 4), true, false);
            }
            marker.Freeze();
            context.DrawGeometry(SecondaryBrush, null, marker);
        }

        if (visible.Count == 1)
        {
            context.DrawEllipse(
                LineBrush(),
                null,
                new Point(XOf(visible[0].At), YOf(visible[0].Percent)),
                3,
                3);
        }
        else
        {
            DrawSeries(context, visible, XOf, YOf, plot);
        }

        DrawProjection(context, XOf, YOf);

        // Now splits history from projection.
        var nowX = XOf(now);
        context.DrawLine(NowPen, new Point(nowX, 0), new Point(nowX, plot));

        // Both ends of the axis, so the range on screen reads without a caption.
        DrawText(context, Format.Day(start), 0, height - 4, TertiaryBrush, TextAlign.Left, 11);
        DrawText(context, Format.Day(end), width, height - 4, TertiaryBrush, TextAlign.Right, 11);
    }

    private static void DrawGrid(DrawingContext context, double width, double bottom)
    {
        // Empty, half, full.
        foreach (var fraction in new[] { 0.0, 0.5, 1.0 })
        {
            var y = bottom - fraction * bottom;
            context.DrawLine(GridPen, new Point(0, y), new Point(width, y));
        }
    }

    private void DrawSeries(
        DrawingContext context,
        IReadOnlyList<UsagePoint> visible,
        Func<DateTimeOffset, double> xOf,
        Func<float, double> yOf,
        double bottom)
    {
        var line = new StreamGeometry();
        using (var geometry = line.Open())
        {
            geometry.BeginFigure(new Point(xOf(visible[0].At), yOf(visible[0].Percent)), false, false);
            for (var i = 1; i < visible.Count; i++)
            {
                geometry.LineTo(new Point(xOf(visible[i].At), yOf(visible[i].Percent)), true, false);
            }
        }
        line.Freeze();

        var area = new StreamGeometry();
        using (var geometry = area.Open())
        {
            geometry.BeginFigure(new Point(xOf(visible[0].At), yOf(visible[0].Percent)), true, true);
            for (var i = 1; i < visible.Count; i++)
            {
                geometry.LineTo(new Point(xOf(visible[i].At), yOf(visible[i].Percent)), true, false);
            }
            geometry.LineTo(new Point(xOf(visible[^1].At), bottom), true, false);
            geometry.LineTo(new Point(xOf(visible[0].At), bottom), true, false);
        }
        area.Freeze();

        // Fade the area out towards the floor so a high series does not read as a slab.
        var fill = new LinearGradientBrush(
            WithAlpha(LineColor, 96),
            WithAlpha(LineColor, 0),
            new Point(0, 0),
            new Point(0, 1));
        fill.Freeze();
        context.DrawGeometry(fill, null, area);

        context.DrawGeometry(null, FrozenPen(LineBrush(), 2), line);
    }

    /// <summary>
    /// The expectation, drawn only where it belongs: ahead of now. The readings are
    /// the history, so nothing is fitted back over them -- a dashed line tracing the
    /// past would just be the solid line again, and it made the future half look like
    /// it had already happened.
    /// </summary>
    private void DrawProjection(
        DrawingContext context,
        Func<DateTimeOffset, double> xOf,
        Func<float, double> yOf)
    {
        var projection = Projection ?? [];
        if (projection.Count < 2) return;

        var path = new StreamGeometry();
        using (var geometry = path.Open())
        {
            geometry.BeginFigure(new Point(xOf(projection[0].At), yOf(projection[0].Percent)), false, false);
            for (var i = 1; i < projection.Count; i++)
            {
                geometry.LineTo(new Point(xOf(projection[i].At), yOf(projection[i].Percent)), true, false);
            }
        }
        path.Freeze();

        context.DrawGeometry(null, TrendPen, path);
    }

    private void DrawCollecting(DrawingContext context, double width, double y)
    {
        DrawText(context, "Collecting readings…", width / 2, y, HintBrush, TextAlign.Center, 11);
    }

    private void DrawMark(DrawingContext context, ProviderId provider, double left, double top, double size)
    {
        var geometry = ProviderArt.Logo(provider);
        var bounds = geometry.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var scale = size / Math.Max(bounds.Width, bounds.Height);
        var transform = new TransformGroup();
        transform.Children.Add(new ScaleTransform(scale, scale));
        transform.Children.Add(new TranslateTransform(
            left + (size - bounds.Width * scale) / 2 - bounds.X * scale,
            top + (size - bounds.Height * scale) / 2 - bounds.Y * scale));

        context.PushTransform(transform);
        context.DrawGeometry(SecondaryBrush, null, geometry);
        context.Pop();
    }

    private enum TextAlign
    {
        Left,
        Center,
        Right,
    }

    private void DrawText(
        DrawingContext context,
        string text,
        double x,
        double y,
        Brush brush,
        TextAlign align,
        double size,
        Brush? plate = null)
    {
        // The labels take the typeface of wherever the chart sits.
        var face = new Typeface(
            (FontFamily)GetValue(TextElement.FontFamilyProperty),
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal);
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            face,
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var left = align switch
        {
            TextAlign.Center => x - formatted.Width / 2,
            TextAlign.Right => x - formatted.Width,
            _ => x,
        };

        // A label over the lines gets a dark plate, so it reads over whatever it lands on.
        if (plate != null)
        {
            var box = new Rect(left - 5, y - formatted.Height - 1, formatted.Width + 10, formatted.Height + 2);
            context.DrawRoundedRectangle(plate, null, box, box.Height / 2, box.Height / 2);
        }
        context.DrawText(formatted, new Point(left, y - formatted.Height));
    }

    private Brush LineBrush() => Frozen(LineColor);

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness, double[]? dashes = null)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        if (dashes != null) pen.DashStyle = new DashStyle(dashes, 0);
        pen.Freeze();
        return pen;
    }
}

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AiUsage.Data;

namespace AiUsage.Ui;

/// <summary>Where a level is heading, for a chart to draw past now.</summary>
public sealed record Forecast(
    /// <summary>The forecast level between two moments, refills as steps.</summary>
    Func<DateTimeOffset, DateTimeOffset, IReadOnlyList<UsagePoint>> Steps,
    /// <summary>True when it moves at all; a level that holds needs no line ahead.</summary>
    bool Draining,
    /// <summary>When it runs dry before its refill, if it does.</summary>
    DateTimeOffset? EmptyAt = null,
    /// <summary>The next refill: the forecast stops short of it.</summary>
    DateTimeOffset? Reset = null);

/// <summary>
/// A level over time on a fixed 0–100 axis, as the Mac draws it: a stepped line
/// in the level's colour over a fading fill, then the forecast from now as a
/// dashed line, with a dot where it runs dry. Hover to read a moment, ahead of
/// now as well.
/// </summary>
public sealed class HistoryChart : Control
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");

    /// <summary>Room for the axis labels when they are drawn.</summary>
    private const double LabelRight = 24;
    private const double LabelBelow = 15;

    private readonly IReadOnlyList<UsagePoint> _points;
    private readonly IReadOnlyList<UsagePoint> _ahead;
    private readonly Forecast? _forecast;
    private readonly DateTimeOffset _from;
    private readonly DateTimeOffset _to;
    private readonly DateTimeOffset _now;
    private readonly TimeSpan _span;
    private readonly bool _axes;
    private DateTimeOffset? _hovered;

    /// <summary>For `--snapshot` only: where along each chart the pointer rests, 0 to 1.</summary>
    public static double? SnapshotHover { get; set; }

    public HistoryChart(
        IReadOnlyList<UsagePoint> points,
        TimeSpan span,
        DateTimeOffset now,
        double height,
        Forecast? forecast = null,
        bool axes = false)
    {
        Height = height;
        _now = now;
        _span = span;
        _axes = axes;
        _forecast = forecast;
        ClipToBounds = false;

        // A short history fills the width instead of leaving the left side blank,
        // but never zooms in past a quarter of the range.
        var start = now - span;
        if (points.Count > 0 && points[0].At > start)
        {
            var quarter = now - span / 4;
            start = points[0].At < quarter ? points[0].At : quarter;
        }
        _from = start;

        var shown = points.Where(p => p.At >= start && p.At <= now).ToList();
        // The level going into the range, so the line starts at the left edge.
        var before = points.LastOrDefault(p => p.At < start);
        if (before != default) shown.Insert(0, new UsagePoint(start, before.Percent));
        if (shown.Count > 0 && shown[^1].At < now) shown.Add(new UsagePoint(now, shown[^1].Percent));
        _points = shown;

        // With a forecast the chart runs on past now: a quarter as far again,
        // further if it runs dry later, never past the refill.
        _to = now;
        if (forecast is { } ahead && (ahead.Draining || ahead.Reset != null))
        {
            var end = now + span / 4;
            if (ahead.EmptyAt is { } empty && empty > end)
            {
                var past = empty + (empty - now) * 0.15;
                end = past < now + span ? past : now + span;
            }
            if (ahead.Reset is { } reset && reset < end) end = reset;
            _to = end > now ? end : now;
        }

        // Stops short of the refill: drawn at the very edge it is a stray vertical line.
        _ahead = forecast is { } f && _to > now
            ? f.Steps(now, _to).Where(p => f.Reset is not { } r || p.At < r).ToList()
            : [];

        // Pictures show a moment read, as hovering would.
        if (SnapshotHover is { } fraction && _points.Count > 1) _hovered = _from + (_to - _from) * fraction;

        PointerMoved += (_, e) => Hover(e.GetPosition(this));
        PointerExited += (_, _) =>
        {
            _hovered = null;
            InvalidateVisual();
        };
    }

    private Rect Plot => new(0, 2, Math.Max(Bounds.Width - (_axes ? LabelRight : 0), 1),
        Math.Max(Bounds.Height - 4 - (_axes ? LabelBelow : 0), 1));

    private double X(DateTimeOffset at)
    {
        var range = Math.Max((_to - _from).TotalSeconds, 1);
        return Plot.X + Math.Clamp((at - _from).TotalSeconds / range, 0, 1) * Plot.Width;
    }

    /// <summary>A little room below zero, so a window sitting at 0% still shows its line.</summary>
    private double Y(double percent) => Plot.Y + (1 - (Math.Clamp(percent, 0, 100) + 4) / 104) * Plot.Height;

    private void Hover(Point at)
    {
        var plot = Plot;
        if (at.X < plot.Left || at.X > plot.Right || _points.Count < 2)
        {
            _hovered = null;
        }
        else
        {
            var fraction = (at.X - plot.X) / plot.Width;
            _hovered = _from + (_to - _from) * fraction;
        }
        InvalidateVisual();
    }

    /// <summary>The level at a moment and when that is: the nearest forecast step ahead of now.</summary>
    private (DateTimeOffset At, double Percent)? ValueAt(DateTimeOffset at)
    {
        if (at > _now && _ahead.Count > 0)
        {
            var nearest = _ahead.MinBy(p => Math.Abs((p.At - at).TotalSeconds));
            return (nearest.At, nearest.Percent);
        }
        // Before the first reading there is nothing to report.
        var held = _points.LastOrDefault(p => p.At <= at);
        return held == default ? null : (at, held.Percent);
    }

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var plot = Plot;

        // Rules at full, half and empty.
        var rule = new Pen(Look.Ink(0.16), 0.75, new DashStyle([2, 3], 0));
        foreach (var level in new[] { 100, 50, 0 })
        {
            var y = Math.Round(Y(level)) + 0.5;
            context.DrawLine(rule, new Point(plot.Left, y), new Point(plot.Right, y));
            if (_axes) Label(context, level.ToString(English), new Point(plot.Right + 5, y), alignRight: false, centreY: true);
        }

        if (_points.Count < 2)
        {
            var empty = Text("No history yet", 10, Look.Secondary);
            context.DrawText(empty, new Point((plot.Width - empty.Width) / 2, (plot.Height - empty.Height) / 2));
            return;
        }

        var colour = Look.ColorOf(_points[^1].Percent);
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var stroke = line.Open())
        using (var fill = area.Open())
        {
            var first = new Point(X(_points[0].At), Y(_points[0].Percent));
            stroke.BeginFigure(first, false);
            fill.BeginFigure(new Point(first.X, plot.Bottom), true);
            fill.LineTo(first);
            var last = first;
            for (var i = 1; i < _points.Count; i++)
            {
                var next = new Point(X(_points[i].At), Y(_points[i].Percent));
                // Held, then stepped: across at the old level, then to the new one.
                var corner = new Point(next.X, last.Y);
                stroke.LineTo(corner);
                stroke.LineTo(next);
                fill.LineTo(corner);
                fill.LineTo(next);
                last = next;
            }
            stroke.EndFigure(false);
            fill.LineTo(new Point(last.X, plot.Bottom));
            fill.EndFigure(true);
        }

        context.DrawGeometry(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x52, colour.R, colour.G, colour.B), 0),
                new GradientStop(Color.FromArgb(0x05, colour.R, colour.G, colour.B), 1),
            },
        }, null, area);
        context.DrawGeometry(null, new Pen(Look.Solid(colour), 1.6, lineJoin: PenLineJoin.Round), line);

        if (_ahead.Count > 1)
        {
            // Now, where the history ends and the forecast starts.
            var now = Math.Round(X(_now)) + 0.5;
            context.DrawLine(new Pen(Look.Ink(0.18), 1), new Point(now, plot.Top), new Point(now, plot.Bottom));

            var forecast = new StreamGeometry();
            using (var path = forecast.Open())
            {
                path.BeginFigure(new Point(X(_ahead[0].At), Y(_ahead[0].Percent)), false);
                foreach (var point in _ahead.Skip(1)) path.LineTo(new Point(X(point.At), Y(point.Percent)));
                path.EndFigure(false);
            }
            context.DrawGeometry(null, new Pen(Look.Ink(0.55), 1.3, new DashStyle([3 / 1.3, 3 / 1.3], 0), PenLineCap.Round), forecast);

            if (_forecast?.EmptyAt is { } dry && dry > _now && dry <= _to)
            {
                context.DrawEllipse(Look.Solid(Look.Critical), null, new Point(X(dry), Y(0)), 3, 3);
            }
        }

        if (_axes) XLabels(context, plot);

        if (_hovered is { } hovered && ValueAt(hovered) is { } hit)
        {
            var x = X(hit.At);
            var y = Y(hit.Percent);
            context.DrawLine(new Pen(Look.Ink(0.4), 1), new Point(x, plot.Top), new Point(x, plot.Bottom));
            context.DrawEllipse(hit.At > _now ? Look.Ink(0.6) : Look.Solid(colour), null, new Point(x, y), 3.2, 3.2);
            Readout(context, plot, hit.At, hit.Percent, x);
        }
    }

    /// <summary>"62%" and when, or "forecast Thu 21:54", in a capsule kept inside the plot and off the line.</summary>
    private void Readout(DrawingContext context, Rect plot, DateTimeOffset at, double percent, double x)
    {
        var figure = Text(Format.Percent((float)percent), 11, Look.Primary, FontWeight.SemiBold);
        var when = Text(at > _now ? $"forecast {Format.When(at)}" : Format.When(at), 10, Look.Secondary);
        var width = figure.Width + 5 + when.Width + 14;
        var height = Math.Max(figure.Height, when.Height) + 6;

        var left = Math.Clamp(x - width / 2, plot.Left, Math.Max(plot.Right - width, plot.Left));
        // Below the line when the line is high.
        var top = percent > 70 ? plot.Bottom - height - 2 : plot.Top + 2;
        var box = new Rect(left, top, width, height);

        var fill = Look.Dark ? Color.FromArgb(0xE0, 0x24, 0x26, 0x2C) : Color.FromArgb(0xE6, 0xFA, 0xFA, 0xFC);
        context.DrawRectangle(new SolidColorBrush(fill), new Pen(Look.Ink(0.15), 0.5), new RoundedRect(box, height / 2));
        context.DrawText(figure, new Point(left + 7, top + (height - figure.Height) / 2));
        context.DrawText(when, new Point(left + 7 + figure.Width + 5, top + (height - when.Height) / 2));
    }

    /// <summary>Four times along the bottom: hours for a day, weekdays for a week, dates beyond.</summary>
    private void XLabels(DrawingContext context, Rect plot)
    {
        var days = (_to - _from).TotalDays;
        var format = days > 8 ? "d MMM" : days < 2 ? "HH:mm" : "ddd";
        for (var i = 0; i < 4; i++)
        {
            var at = _from + (_to - _from) * ((i + 0.5) / 4);
            Label(context, at.ToLocalTime().ToString(format, English), new Point(X(at), plot.Bottom + 3), alignRight: false, centreX: true);
        }
    }

    private static void Label(DrawingContext context, string text, Point at, bool alignRight, bool centreY = false, bool centreX = false)
    {
        var label = Text(text, 10, Look.Ink(0.6));
        var x = centreX ? at.X - label.Width / 2 : alignRight ? at.X - label.Width : at.X;
        var y = centreY ? at.Y - label.Height / 2 : at.Y;
        context.DrawText(label, new Point(x, y));
    }

    private static FormattedText Text(string text, double size, IBrush brush, FontWeight? weight = null) => new(
        text,
        English,
        FlowDirection.LeftToRight,
        new Typeface(FontManager.Current.DefaultFontFamily, FontStyle.Normal, weight ?? FontWeight.Normal),
        size,
        brush);
}

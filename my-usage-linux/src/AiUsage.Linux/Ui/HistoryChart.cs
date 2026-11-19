using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AiUsage.Data;

namespace AiUsage.Ui;

/// <summary>
/// A level over time on a fixed 0–100 axis: a stepped line in the level's
/// colour over a fading fill, between two dashed rules for full and empty.
/// A level holds until the next reading, so the line is stepped, not sloped.
/// </summary>
public sealed class HistoryChart : Control
{
    private readonly IReadOnlyList<UsagePoint> _points;
    private readonly DateTimeOffset _from;
    private readonly DateTimeOffset _to;

    public HistoryChart(IReadOnlyList<UsagePoint> points, TimeSpan span, DateTimeOffset now, double height)
    {
        Height = height;
        _to = now;

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
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        var rule = new Pen(Look.Ink(0.16), 1, new DashStyle([2, 3], 0));
        context.DrawLine(rule, new Point(0, 0.5), new Point(width, 0.5));
        context.DrawLine(rule, new Point(0, height - 0.5), new Point(width, height - 0.5));
        if (_points.Count < 2) return;

        var range = (_to - _from).TotalSeconds;
        Point At(DateTimeOffset at, double percent) => new(
            Math.Clamp((at - _from).TotalSeconds / range, 0, 1) * width,
            1 + (1 - Math.Clamp(percent, 0, 100) / 100) * (height - 2));

        var colour = Look.ColorOf(_points[^1].Percent);
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var stroke = line.Open())
        using (var fill = area.Open())
        {
            var first = At(_points[0].At, _points[0].Percent);
            stroke.BeginFigure(first, false);
            fill.BeginFigure(new Point(first.X, height), true);
            fill.LineTo(first);
            var last = first;
            for (var i = 1; i < _points.Count; i++)
            {
                var next = At(_points[i].At, _points[i].Percent);
                // Held, then stepped: across at the old level, then to the new one.
                var corner = new Point(next.X, last.Y);
                stroke.LineTo(corner);
                stroke.LineTo(next);
                fill.LineTo(corner);
                fill.LineTo(next);
                last = next;
            }
            stroke.EndFigure(false);
            fill.LineTo(new Point(last.X, height));
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
    }
}

using System.Windows;
using System.Windows.Media;

namespace AiUsage.Ui;

/// <summary>
/// The thin bar under a window's label: a dim track with a coloured fill for how
/// much of the window is left.
/// </summary>
public sealed class Meter : FrameworkElement
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill),
        typeof(double),
        typeof(Meter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush),
        typeof(Brush),
        typeof(Meter),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush),
        typeof(Brush),
        typeof(Meter),
        new FrameworkPropertyMetadata(
            new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>How much of the window is left, 0-1.</summary>
    public double Fill
    {
        get => (double)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
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

    protected override void OnRender(DrawingContext context)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var radius = height / 2;
        context.DrawRoundedRectangle(TrackBrush, null, new Rect(0, 0, width, height), radius, radius);

        var fill = Math.Clamp(Fill, 0.0, 1.0);
        if (fill <= 0.0) return;

        // A sliver narrower than the corner radius would render as a dot, so the
        // fill keeps a minimum width once there is anything left at all.
        var filled = Math.Max(width * fill, height * 2);
        context.DrawRoundedRectangle(FillBrush, null, new Rect(0, 0, filled, height), radius, radius);
    }
}

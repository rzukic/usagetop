using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AiUsage.Ui;

namespace AiUsage;

/// <summary>
/// The tray icon: a ring of what is left, in the level's colour. A Linux tray
/// shows a picture and nothing beside it, so the figure itself is in the
/// tooltip and in `usagetop status`.
/// </summary>
public static class TrayArt
{
    private const int Size = 64;

    public static RenderTargetBitmap Draw(float? percent)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using var context = bitmap.CreateDrawingContext();

        // Trays are dark far more often than not, and a ring in the label
        // colour of a dark panel still reads on a light one.
        var ink = Color.FromRgb(0xF2, 0xF2, 0xF4);
        const double stroke = 9;
        var centre = new Point(Size / 2.0, Size / 2.0);
        var radius = (Size - stroke) / 2 - 3;
        context.DrawEllipse(null, new Pen(new SolidColorBrush(ink, 0.38), stroke), centre, radius, radius);

        if (percent is not > 0)
        {
            return bitmap;
        }

        var fraction = Math.Clamp(percent.Value / 100, 0.001, 1);
        var colour = percent <= 40 ? Look.LightOf(percent.Value) : ink;
        var pen = new Pen(new SolidColorBrush(colour), stroke, lineCap: PenLineCap.Round);
        if (fraction >= 0.999)
        {
            context.DrawEllipse(null, pen, centre, radius, radius);
            return bitmap;
        }

        var angle = fraction * 2 * Math.PI;
        var arc = new StreamGeometry();
        using (var path = arc.Open())
        {
            path.BeginFigure(new Point(centre.X, centre.Y - radius), false);
            path.ArcTo(
                new Point(centre.X + radius * Math.Sin(angle), centre.Y - radius * Math.Cos(angle)),
                new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise);
            path.EndFigure(false);
        }
        context.DrawGeometry(null, pen, arc);
        return bitmap;
    }

    public static WindowIcon Icon(float? percent)
    {
        using var bitmap = Draw(percent);
        var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        stream.Position = 0;
        return new WindowIcon(stream);
    }
}

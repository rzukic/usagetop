using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AiUsage.Ui;

namespace AiUsage;

/// <summary>
/// The tray icon: a ring of what is left, in the level's colour. A Linux tray
/// shows a picture and nothing beside it, so the figure itself is in the
/// tooltip and in `usagetop status`. Ringing one limit rather than the combined
/// level, it has that provider's mark inside, to say whose it is.
/// </summary>
public static class TrayArt
{
    private const int Size = 64;

    public static RenderTargetBitmap Draw(float? percent, Geometry? mark = null)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using var context = bitmap.CreateDrawingContext();

        // In the label colour of the desktop's light or dark setting, which a
        // tray's own colour follows on KDE, Xfce and the like: a light ring was
        // all but lost on their light panels. A tiling window manager's bar
        // (Waybar, i3bar, polybar) takes its colours from its own style, not the
        // desktop's, and is dark nearly always: there it stays light.
        var dark = Look.Dark || Desktop.IsTiling;
        var ink = dark ? Color.FromRgb(0xF2, 0xF2, 0xF4) : Look.InkColor;
        const double stroke = 9;
        var centre = new Point(Size / 2.0, Size / 2.0);
        var radius = (Size - stroke) / 2 - 3;
        context.DrawEllipse(null, new Pen(new SolidColorBrush(ink, 0.38), stroke), centre, radius, radius);

        if (mark != null)
        {
            // Inside the ring with room to spare: about half the icon's width.
            const double side = 30;
            var bounds = mark.Bounds;
            var scale = side / Math.Max(Math.Max(bounds.Width, bounds.Height), 1e-6);
            var shape = mark.Clone();
            shape.Transform = new MatrixTransform(
                Matrix.CreateTranslation(-bounds.X, -bounds.Y)
                * Matrix.CreateScale(scale, scale)
                * Matrix.CreateTranslation(centre.X - bounds.Width * scale / 2, centre.Y - bounds.Height * scale / 2));
            context.DrawGeometry(new SolidColorBrush(ink), null, shape);
        }

        if (percent is not > 0)
        {
            return bitmap;
        }

        var fraction = Math.Clamp(percent.Value / 100, 0.001, 1);
        var colour = percent <= 40 ? (dark ? Look.LightOf(percent.Value) : Look.ColorOf(percent.Value)) : ink;
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

    public static WindowIcon Icon(float? percent, Geometry? mark = null)
    {
        using var bitmap = Draw(percent, mark);
        var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        stream.Position = 0;
        return new WindowIcon(stream);
    }
}

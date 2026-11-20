using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AiUsage.Data;
using AiUsage.Ui;
using AiUsage.Views;

namespace AiUsage;

/// <summary>
/// `usagetop --snapshot <folder>`: every page of the panel to PNGs, light and
/// dark, without opening a window. What the README's pictures are made from,
/// and how a change to the panel is looked at on a machine with no Linux desktop.
/// </summary>
public static class Snapshots
{
    private const double Scale = 2;

    public static void Write(string folder, AppModel model)
    {
        Directory.CreateDirectory(folder);
        foreach (var dark in new[] { true, false })
        {
            Look.Dark = dark;
            var suffix = dark ? "dark" : "light";

            Save(Path.Combine(folder, $"overview-{suffix}.png"), model, new Pane.Overview(), dark);
            foreach (var usage in model.RailOrder().Where(u => u.State == UsageState.Ok).Take(2))
            {
                // The account's charts with a moment read, as a pointer resting on them shows it.
                HistoryChart.SnapshotHover = 0.82;
                Save(Path.Combine(folder, $"account-{usage.AccountKey}-{suffix}.png"), model, new Pane.Account(usage.AccountKey), dark);
                HistoryChart.SnapshotHover = null;
            }
            Save(Path.Combine(folder, $"settings-{suffix}.png"), model, new Pane.Settings(), dark);
        }

        // The rail's badges alone and large, to judge where their letters sit.
        if (Environment.GetEnvironmentVariable("USAGETOP_SNAPSHOT_BADGES") == "1")
        {
            Look.Dark = true;
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, Margin = new Thickness(6) };
            foreach (var text in new[] { "W", "RZ", "SG", "#2" })
            {
                var badge = PanelView.Badge(new SolidColorBrush(Color.FromRgb(0x3A, 0x3C, 0x44)),
                    Bits.Text(text, 7.5, FontWeight.Heavy, Look.Primary));
                badge.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
                badge.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
                badge.Margin = default;
                row.Children.Add(badge);
            }
            var stage = new Border { Background = new SolidColorBrush(Color.FromRgb(0x20, 0x21, 0x26)), Child = row };
            // At the screen's own scale (USAGETOP_SNAPSHOT_SCALE, 1 by default), as rounding and pixels fall there.
            var scale = double.TryParse(Environment.GetEnvironmentVariable("USAGETOP_SNAPSHOT_SCALE"), out var s) ? s : 1;
            var big = new LayoutTransformControl { LayoutTransform = new ScaleTransform(scale, scale), Child = stage };
            big.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            big.Arrange(new Rect(big.DesiredSize));
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)big.DesiredSize.Width, (int)big.DesiredSize.Height), new Vector(96, 96));
            bitmap.Render(big);
            bitmap.Save(Path.Combine(folder, "badges.png"), PngBitmapEncoderOptions.Default);
        }

        foreach (var percent in new float?[] { null, 8, 31, 51, 100 })
        {
            using var icon = TrayArt.Draw(percent);
            icon.Save(Path.Combine(folder, $"tray-{(percent is { } p ? ((int)p).ToString() : "none")}.png"), PngBitmapEncoderOptions.Default);
        }
    }

    private static void Save(string path, AppModel model, Pane pane, bool dark)
    {
        var view = new PanelView(model, flat: true);
        view.Show(pane);

        // On a backdrop, as it would sit on a desktop, with its shadow.
        var stage = new Border
        {
            Padding = new Thickness(36, 30, 36, 42),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = dark
                    ? [new GradientStop(Color.FromRgb(0x2B, 0x3A, 0x5C), 0), new GradientStop(Color.FromRgb(0x5A, 0x3F, 0x52), 1)]
                    : [new GradientStop(Color.FromRgb(0xB9, 0xCD, 0xEC), 0), new GradientStop(Color.FromRgb(0xE8, 0xCF, 0xD6), 1)],
            },
            Child = new Border
            {
                CornerRadius = new CornerRadius(PanelView.Radius),
                BoxShadow = new BoxShadows(new BoxShadow
                {
                    OffsetY = 10,
                    Blur = 28,
                    Color = Color.FromArgb(dark ? (byte)0x80 : (byte)0x40, 0, 0, 0),
                }),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = view,
            },
        };

        // Twice the size by laying it out twice as large: a bitmap that is told
        // it has twice the dots per inch scales what is nested inside twice over.
        var scaled = new LayoutTransformControl { LayoutTransform = new ScaleTransform(Scale, Scale), Child = stage };
        scaled.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        scaled.Arrange(new Rect(scaled.DesiredSize));

        var pixels = new PixelSize((int)Math.Ceiling(scaled.DesiredSize.Width), (int)Math.Ceiling(scaled.DesiredSize.Height));
        using var bitmap = new RenderTargetBitmap(pixels, new Vector(96, 96));
        bitmap.Render(scaled);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }
}

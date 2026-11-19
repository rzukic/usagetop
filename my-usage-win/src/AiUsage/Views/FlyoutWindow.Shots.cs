using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AiUsage.Views;

/// <summary>
/// Pictures of the real popup, glass and all (`AiUsage.exe --live-shot <folder>
/// --state <file>`). The window manager draws the blur, the rounded corners and
/// the shadow only on screen, so a picture of them has to be a capture of the
/// screen. The popup opens where it always does, over a wallpaper of our own that
/// covers the whole primary screen, taskbar included, so nothing of the user's
/// desktop is behind it or beside it.
/// </summary>
public partial class FlyoutWindow
{
    /// <summary>Set while posing: the popup stays open when focus goes elsewhere.</summary>
    private bool _posing;

    /// <summary>The overview, then Claude's page, each to `windows-&lt;pose&gt;.png` in <paramref name="folder"/>.</summary>
    public static async Task LiveShots(string folder)
    {
        Directory.CreateDirectory(folder);
        var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        var width = SystemParameters.PrimaryScreenWidth;
        var height = SystemParameters.PrimaryScreenHeight;
        var backdrop = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Left = 0,
            Top = 0,
            Width = width,
            Height = height,
            Content = Wallpaper(width, height),
        };
        backdrop.Show();

        var flyout = new FlyoutWindow { _posing = true };
        foreach (var (name, account) in new[] { ("overview", (string?)null), ("account", "claude") })
        {
            if (account == null) flyout._panel.ShowOverview();
            else flyout._panel.ShowAccount(account);
            // Opened from the bottom-right corner, as from a tray icon there.
            flyout.ShowNearTray(new System.Drawing.Point(screen.Right - 1, screen.Bottom - 1));
            await Task.Delay(2000);

            // The popup, with room for its shadow, out to the screen's edge.
            var scale = PresentationSource.FromVisual(flyout)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            var margin = 40 * scale;
            var left = (int)Math.Max(screen.Left, flyout.Left * scale - margin);
            var top = (int)Math.Max(screen.Top, flyout.Top * scale - margin);
            var bottom = (int)Math.Min(screen.Bottom, (flyout.Top + flyout.ActualHeight) * scale + margin);
            using var bitmap = new System.Drawing.Bitmap(screen.Right - left, bottom - top);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(left, top, 0, 0, bitmap.Size);
            }
            bitmap.Save(System.IO.Path.Combine(folder, $"windows-{name}.png"), System.Drawing.Imaging.ImageFormat.Png);
            flyout.Hide();
        }
        flyout.Close();
        backdrop.Close();
    }

    /// <summary>A dark gradient wallpaper: three soft glows over deep blue.</summary>
    private static UIElement Wallpaper(double width, double height)
    {
        var wall = new Grid { Background = new SolidColorBrush(Color.FromRgb(18, 20, 41)) };
        var glows = new[]
        {
            (Color: Color.FromRgb(89, 56, 158), X: 0.15, Y: 0.15, Alpha: 1.0, Reach: 0.8),
            (Color: Color.FromRgb(31, 107, 158), X: 0.90, Y: 0.30, Alpha: 1.0, Reach: 0.75),
            (Color: Color.FromRgb(217, 107, 77), X: 0.60, Y: 0.90, Alpha: 0.55, Reach: 0.7),
        };
        foreach (var glow in glows)
        {
            var center = new Point(glow.X * width, glow.Y * height);
            var brush = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                Center = center,
                GradientOrigin = center,
                RadiusX = glow.Reach * width,
                RadiusY = glow.Reach * width,
            };
            var color = glow.Color;
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(glow.Alpha * 255), color.R, color.G, color.B), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(glow.Alpha * 90), color.R, color.G, color.B), 0.45));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
            wall.Children.Add(new Rectangle { Fill = brush });
        }
        return wall;
    }
}

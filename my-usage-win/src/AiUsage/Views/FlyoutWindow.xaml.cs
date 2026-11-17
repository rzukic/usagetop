using System.Windows;
using System.Windows.Input;
using AiUsage.Ui;

namespace AiUsage.Views;

/// <summary>
/// The tray popup: the Mac panel's design on Windows (see <see cref="PanelView"/>),
/// on the blurred desktop (see <see cref="Backdrop.ApplyBlur"/>). It opens beside the tray icon and closes as
/// soon as it loses focus, or on Escape from the overview.
/// </summary>
public partial class FlyoutWindow : Window
{
    private readonly PanelView _panel = new();

    public FlyoutWindow()
    {
        InitializeComponent();
        Content = _panel;

        SourceInitialized += (_, _) => _panel.SetBackdrop(Backdrop.ApplyBlur(this));
        Deactivated += (_, _) =>
        {
            _panel.SetCtrl(false);
            // A sign-in sends the user to the browser and back; the panel waits
            // for them rather than closing on the step in progress.
            if (_panel.HoldsOpen) return;
            Hide();
        };
        PreviewKeyDown += (_, e) => e.Handled = _panel.HandleKey(e, down: true);
        PreviewKeyUp += (_, e) => _panel.HandleKey(e, down: false);

        _panel.CloseRequested += Hide;
        _panel.DashboardRequested += () =>
        {
            Hide();
            ((App)Application.Current).ShowMain();
        };

        // The panel's height follows its page; the popup stays anchored where it opened.
        SizeChanged += (_, _) => ClampToWorkArea();
    }

    /// <summary>
    /// Opens against the tray icon. The cursor is where the click landed, so the
    /// popup is placed above it and pulled back inside the work area -- the taskbar
    /// can be on any edge.
    /// </summary>
    public void ShowNearTray()
    {
        // What the glass will sit over, read before the window covers it.
        _panel.SetBackdropLuminance(LuminanceBehind());
        Show();
        _panel.Opened();
        UpdateLayout();

        var work = SystemParameters.WorkArea;
        var cursor = System.Windows.Forms.Cursor.Position;
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : 400;

        var left = cursor.X / scale - width + 16;
        var top = cursor.Y / scale - height - 12;

        // Above the cursor unless that would leave the screen; then below it.
        if (top < work.Top + 8) top = cursor.Y / scale + 16;

        Left = Math.Clamp(left, work.Left + 8, work.Right - width - 8);
        Top = Math.Clamp(top, work.Top + 8, work.Bottom - height - 8);

        Activate();
        Focus();
    }

    /// <summary>
    /// How bright the screen is where the popup opens, 0 (black) to 1 (white):
    /// the area beside the cursor that the popup will cover, sampled on a grid.
    /// The glass is tinted to match, so it is as dark over a white page as over a
    /// dark one, the job Acrylic's luminosity layer does.
    /// </summary>
    private double LuminanceBehind()
    {
        try
        {
            var cursor = System.Windows.Forms.Cursor.Position;
            var screen = System.Windows.Forms.Screen.FromPoint(cursor).Bounds;
            var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11
                ?? System.Windows.Forms.Screen.FromPoint(cursor).Bounds.Width / SystemParameters.PrimaryScreenWidth;
            var width = (int)(Width * scale);
            var height = (int)((ActualHeight > 0 ? ActualHeight : MaxHeight) * scale);

            var left = Math.Clamp(cursor.X - width, screen.Left, Math.Max(screen.Left, screen.Right - width));
            var top = Math.Clamp(cursor.Y - height, screen.Top, Math.Max(screen.Top, screen.Bottom - height));
            width = Math.Min(width, screen.Width);
            height = Math.Min(height, screen.Height);
            if (width <= 0 || height <= 0) return 0;

            using var bitmap = new System.Drawing.Bitmap(width, height);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(left, top, 0, 0, new System.Drawing.Size(width, height));
            }

            double total = 0;
            var count = 0;
            for (var y = 4; y < height; y += 16)
            {
                for (var x = 4; x < width; x += 16)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    total += (0.2126 * pixel.R + 0.7152 * pixel.G + 0.0722 * pixel.B) / 255;
                    count++;
                }
            }
            return count == 0 ? 0 : total / count;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException)
        {
            // A locked or secure desktop: no reading, so the glass takes its dark default.
            return 0;
        }
    }

    /// <summary>A page can be taller or shorter than the last; pull the popup back inside the work area.</summary>
    private void ClampToWorkArea()
    {
        if (!IsVisible || double.IsNaN(Left) || double.IsNaN(Top)) return;

        var work = SystemParameters.WorkArea;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : 400;

        Left = Math.Clamp(Left, work.Left + 8, Math.Max(work.Left + 8, work.Right - width - 8));
        Top = Math.Clamp(Top, work.Top + 8, Math.Max(work.Top + 8, work.Bottom - height - 8));
    }
}

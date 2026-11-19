using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Media;
using AiUsage.Ui;

namespace AiUsage.Views;

/// <summary>
/// The window the panel lives in. It has no frame of its own: where the
/// desktop can show through windows it is a rounded panel with a shadow,
/// elsewhere a plain rectangle. It tells the window manager it is a small
/// utility window, which tiling ones float instead of giving it a tile, and it
/// carries the class "usagetop" for anyone writing their own window rules.
/// </summary>
public sealed class PanelWindow : Window
{
    /// <summary>Room round the panel for its shadow to fall in.</summary>
    private const double ShadowRoom = 30;

    /// <summary>The gap kept to the screen's edges.</summary>
    private const double EdgeGap = 8;

    private readonly AppModel _model;
    private readonly bool _shaped = Desktop.IsComposited;
    private DateTimeOffset _hiddenByBlurAt = DateTimeOffset.MinValue;

    public PanelWindow(AppModel model)
    {
        _model = model;
        Title = "usagetop";
        WindowDecorations = WindowDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        X11Properties.SetNetWmWindowType(this, X11NetWmWindowType.Utility);
        X11Properties.SetWmClass(this, "usagetop");

        View = new PanelView(model);

        if (_shaped)
        {
            // KWin blurs behind a window that asks; other compositors blur
            // see-through windows by their own rules, or not at all.
            TransparencyLevelHint = Desktop.IsKde
                ? [WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent]
                : [WindowTransparencyLevel.Transparent];
            Background = Brushes.Transparent;
            Width = PanelView.PanelWidth + ShadowRoom * 2;
            Content = new Border
            {
                Margin = new Thickness(ShadowRoom, ShadowRoom - 8, ShadowRoom, ShadowRoom + 8),
                CornerRadius = new CornerRadius(PanelView.Radius),
                BoxShadow = new BoxShadows(new BoxShadow
                {
                    OffsetY = 10,
                    Blur = 28,
                    Color = Color.FromArgb(Look.Dark ? (byte)0x80 : (byte)0x40, 0, 0, 0),
                }),
                Child = View,
            };
        }
        else
        {
            TransparencyLevelHint = [WindowTransparencyLevel.None];
            View.CornerRadius = default;
            View.BorderThickness = default;
            Background = Look.Solid(Look.Panel.Bottom);
            Width = PanelView.PanelWidth;
            Content = View;
        }

        Opened += (_, _) => Place();
        SizeChanged += (_, _) => Place();
        Deactivated += (_, _) =>
        {
            if (!CloseOnBlur || !IsVisible) return;
            _hiddenByBlurAt = DateTimeOffset.UtcNow;
            Hide();
        };
        KeyDown += OnKey;
    }

    public PanelView View { get; }

    /// <summary>
    /// Put away as soon as another window is clicked, as a menu is. Off where
    /// the focus follows the pointer: there the panel would vanish the moment
    /// the pointer left it.
    /// </summary>
    public bool CloseOnBlur { get; set; } = !Desktop.IsTiling;

    /// <summary>Shown if hidden, hidden if shown: a click on the tray icon, or `usagetop toggle`.</summary>
    public void Toggle()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }
        // The click that asks for it also took the focus away, which has just
        // hidden the panel: that click meant "close", not "close and reopen".
        if (DateTimeOffset.UtcNow - _hiddenByBlurAt < TimeSpan.FromMilliseconds(300)) return;
        Reveal();
    }

    public void Reveal()
    {
        View.Rebuild();
        FitScreen();
        Show();
        Activate();
    }

    /// <summary>Never taller than the screen: the page scrolls for whatever does not fit.</summary>
    private void FitScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null) return;
        MaxHeight = screen.WorkingArea.Height / screen.Scaling - EdgeGap * 2 + (_shaped ? ShadowRoom : 0);
    }

    /// <summary>
    /// Into the corner the tray is in, as far as that can be known: the bottom
    /// right where the desktop's panel is at the bottom, the top right
    /// otherwise. A tiling window manager places its floating windows itself.
    /// </summary>
    private void Place()
    {
        if (Desktop.IsTiling) return;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null) return;

        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        // The shadow's room may hang over the edge; the panel itself keeps the gap.
        var room = _shaped ? ShadowRoom : 0;
        var width = (int)Math.Round(Bounds.Width * scale);
        var height = (int)Math.Round(Bounds.Height * scale);
        var inset = (int)Math.Round((EdgeGap - room) * scale);

        var x = area.Right - width - inset;
        var y = Desktop.PanelAtBottom ? area.Bottom - height - inset : area.Y + inset;
        Position = new PixelPoint(x, Math.Max(y, area.Y - (int)Math.Round(room * scale)));
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Hide();
            return;
        }
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        switch (e.Key)
        {
            case Key.R:
                _ = _model.RefreshAsync(force: true);
                break;
            case Key.Q:
                QuitRequested?.Invoke();
                break;
            case Key.OemComma:
                View.Show(View.Pane is Pane.Settings ? new Pane.Overview() : new Pane.Settings());
                break;
            case Key.D0 or Key.NumPad0:
                View.Show(new Pane.Overview());
                break;
            case >= Key.D1 and <= Key.D9:
                ShowAccount(e.Key - Key.D1);
                break;
            case >= Key.NumPad1 and <= Key.NumPad9:
                ShowAccount(e.Key - Key.NumPad1);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    public event Action? QuitRequested;

    private void ShowAccount(int index)
    {
        var accounts = _model.RailOrder();
        if (index < accounts.Count) View.Show(new Pane.Account(accounts[index].AccountKey));
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using AiUsage.Data;
using AiUsage.Ui;

namespace AiUsage.Views;

/// <summary>
/// The tray flyout's content, the Mac panel's design on Windows: a rail of
/// accounts down the left (each its mark inside a ring of what is left), and the
/// page on the right: the overview, an account with its history and forecast,
/// or settings. Everything sits on one sheet of blurred desktop under its own tint; controls
/// and tiles are soft fills on it, never a second layer of glass.
///
/// Built in code, as the app's other pages are, and rebuilt from the reading
/// whenever it changes: there are a handful of accounts, and a rebuild is the
/// simplest way to be sure the screen says what the reading says.
/// </summary>
public sealed partial class PanelView : UserControl
{
    private enum PageKind { Overview, Account, Settings }

    private PageKind _page = PageKind.Overview;
    private string? _account;
    private bool _ctrlHeld;

    private readonly Border _glass = new();
    private readonly Grid _root = new();
    private readonly TranslateTransform _rise = new();
    private readonly StackPanel _rail = new();
    private readonly StackPanel _railBottom = new();
    private readonly TextBlock _title = new();
    private readonly TextBlock _subtitle = new();
    private readonly ScrollViewer _scroller = new();
    private readonly StackPanel _content = new();
    private readonly Ellipse _statusDot = new() { Width = 6, Height = 6 };
    private readonly TextBlock _status = new();
    private readonly Button _refresh;
    private readonly RotateTransform _spin = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };

    /// <summary>The tiles' solid layer; its opacity is the Tiles setting, so a slider moves it live.</summary>
    private static readonly SolidColorBrush SolidTile = new(Color.FromRgb(0x2A, 0x2A, 0x2E));
    private static readonly SolidColorBrush FaintTile = Frozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush HoverTile = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));

    /// <summary>A tile's edge, lit from above as the Mac's is: brighter along the top, fading down.</summary>
    private static readonly Brush Edge = FrozenEdge(0x2E, 0x08);
    private static readonly Brush HoverEdge = FrozenEdge(0x45, 0x10);
    private static readonly SolidColorBrush Primary = Frozen(Color.FromRgb(0xF2, 0xF2, 0xF2));
    private static readonly SolidColorBrush Secondary = Frozen(Color.FromRgb(0xA8, 0xA8, 0xA8));
    private static readonly SolidColorBrush Faint = Frozen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    private static readonly FontFamily Ui = PanelFonts.Text;

    /// <summary>
    /// The panel's type ramp, after Windows 11's rather than the Mac's. The Mac
    /// sizes (10-11.5pt for most lines) read well in San Francisco on a Retina
    /// screen; in Segoe at 100% they are small and soft, so nothing here goes
    /// under 11, and a line has one of these sizes, not one of its own.
    /// </summary>
    private const double Micro = 11;    // badges, chips, the footnote
    private const double Caption = 12;  // the quieter line: resets, plans, hints, status
    private const double Body = 13;     // a label: a window's name, a setting
    private const double Heading = 14;  // an account's name on its card
    private const double Page = 20;     // the page title

    // Text stays on WPF's ideal (unhinted) layout. Hinted ("Display") text is
    // crisper at 100%, but it draws Segoe's Semibold close to Regular, and the
    // panel's hierarchy is carried by weight.

    /// <summary>The panel's width; the window and the snapshots both take it.</summary>
    public const double PanelWidth = 440;
    private static readonly FontFamily Display = PanelFonts.Display;
    private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>Asks the window to close (Escape on the overview, the Dashboard button).</summary>
    public event Action? CloseRequested;

    /// <summary>Asks the app for the dashboard window.</summary>
    public event Action? DashboardRequested;

    public PanelView()
    {
        // The Glass setting: the window colour over the backdrop, from not at all
        // to fully. Without a backdrop to show (before Windows 11 22H2) it is solid.
        _glass.Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x18));

        var root = _root;
        root.RenderTransform = _rise;
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // The rail: overview, the accounts, and settings at the foot.
        var rail = new DockPanel { Margin = new Thickness(0, 12, 0, 12) };
        _railBottom.HorizontalAlignment = HorizontalAlignment.Center;
        DockPanel.SetDock(_railBottom, Dock.Bottom);
        rail.Children.Add(_railBottom);
        _rail.HorizontalAlignment = HorizontalAlignment.Center;
        rail.Children.Add(_rail);
        var railEdge = new Border
        {
            BorderBrush = Faint,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Margin = new Thickness(0, 14, 0, 14),
        };
        root.Children.Add(railEdge);
        root.Children.Add(rail);

        // The page: header, scrolling content, footer.
        var page = new DockPanel();
        Grid.SetColumn(page, 1);

        _refresh = new Button
        {
            Style = Res("SoftCircle"),
            Content = new TextBlock
            {
                Text = "", // Sync: two arrows round a circle, so it spins true.
                FontFamily = Icons,
                FontSize = 12,
                RenderTransform = _spin,
                RenderTransformOrigin = new Point(0.5, 0.5),
            },
            ToolTip = "Refresh now  (Ctrl+R)",
        };
        _refresh.Click += (_, _) => _ = RefreshAsync(force: true);

        var header = new Grid { Margin = new Thickness(14, 14, 14, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel();
        _title.FontFamily = Display;
        _title.FontSize = Page;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = Primary;
        _subtitle.FontFamily = Ui;
        _subtitle.FontSize = Caption;
        _subtitle.Foreground = Secondary;
        titles.Children.Add(_title);
        titles.Children.Add(_subtitle);
        header.Children.Add(titles);
        Grid.SetColumn(_refresh, 1);
        header.Children.Add(_refresh);
        DockPanel.SetDock(header, Dock.Top);
        page.Children.Add(header);

        var footer = new Grid { Margin = new Thickness(14, 10, 14, 12) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _statusDot.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(_statusDot);
        _status.FontFamily = Ui;
        _status.FontSize = Caption;
        _status.Foreground = Secondary;
        _status.Margin = new Thickness(8, 0, 8, 0);
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(_status, 1);
        footer.Children.Add(_status);
        var dashboard = SoftButton("Dashboard", "Open the dashboard window", "");
        dashboard.Margin = new Thickness(0, 0, 8, 0);
        dashboard.Click += (_, _) => DashboardRequested?.Invoke();
        Grid.SetColumn(dashboard, 2);
        footer.Children.Add(dashboard);
        var quit = new Button { Style = Res("SoftCircle"), Content = "", Width = 26, Height = 26, ToolTip = "Quit usagetop" };
        quit.Click += (_, _) => ((App)Application.Current).QuitApp();
        Grid.SetColumn(quit, 3);
        footer.Children.Add(quit);
        DockPanel.SetDock(footer, Dock.Bottom);
        page.Children.Add(footer);

        _content.Margin = new Thickness(12, 0, 12, 4);
        _scroller.Content = _content;
        _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroller.PanningMode = PanningMode.VerticalOnly;
        _scroller.ScrollChanged += (_, _) => UpdateFade();
        page.Children.Add(_scroller);
        root.Children.Add(page);

        _glass.Child = root;
        Content = _glass;

        // Everything inside inherits Inter, the chart labels included, with
        // figures of one width so a countdown does not shuffle as it ticks.
        FontFamily = Ui;
        Typography.SetNumeralAlignment(this, FontNumeralAlignment.Tabular);

        PanelPrefs.Current.Changed += ApplyLook;
        App.Repository.Changed += _ => Dispatcher.BeginInvoke(Refresh);
        _clock.Tick += (_, _) => Refresh();
        Updater.Current.Changed += OnUpdaterChanged;
        _clock.Start();

        ApplyLook();
        Render();
    }

    // ---- Look -------------------------------------------------------------

    private bool _backdrop;

    /// <summary>Tells the panel whether the window got its backdrop; without one the glass is solid.</summary>
    public void SetBackdrop(bool available)
    {
        _backdrop = available;
        ApplyLook();
    }

    private double _luminance;

    /// <summary>How bright the screen behind the panel is, 0-1; the window reads it as it opens.</summary>
    public void SetBackdropLuminance(double luminance)
    {
        _luminance = Math.Clamp(luminance, 0, 1);
        ApplyLook();
    }

    /// <summary>The panel colour's brightness, and how bright the glass should come out over anything.</summary>
    private const double TintLuminance = 0.08;
    private const double TargetLuminance = 0.15;

    private void ApplyLook()
    {
        var prefs = PanelPrefs.Current;
        _glass.Background.Opacity = _backdrop ? 1 - prefs.Glass * (1 - TintFor(_luminance)) : 1;
        SolidTile.Opacity = prefs.Tiles;
    }

    /// <summary>
    /// How much of the window colour goes over the blur at full glass: enough to
    /// bring whatever is behind down to the same darkness, so a white page does
    /// not turn the panel grey and wash out its text; never less than half, so a
    /// dark desktop still reads as glass rather than a hole.
    /// </summary>
    private static double TintFor(double behind)
    {
        if (behind <= TargetLuminance) return 0.5;
        var tint = (behind - TargetLuminance) / (behind - TintLuminance);
        return Math.Clamp(tint, 0.5, 0.9);
    }

    /// <summary>
    /// The scrolling page fades out at an edge with more beyond it, so a card cut
    /// off by the footer reads as "scroll for more" rather than as a clipped panel.
    /// </summary>
    private void UpdateFade()
    {
        var height = _scroller.ActualHeight;
        if (height <= 0) return;
        var fade = Math.Min(28 / height, 0.25);
        var top = _scroller.VerticalOffset > 0.5;
        var bottom = _scroller.VerticalOffset < _scroller.ScrollableHeight - 0.5;
        if (!top && !bottom)
        {
            _scroller.OpacityMask = null;
            return;
        }

        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        mask.GradientStops.Add(new GradientStop(top ? Colors.Transparent : Colors.Black, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, top ? fade : 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, bottom ? 1 - fade : 1));
        mask.GradientStops.Add(new GradientStop(bottom ? Colors.Transparent : Colors.Black, 1));
        mask.Freeze();
        _scroller.OpacityMask = mask;
    }

    /// <summary>The panel rises into place as it opens, as Windows' own flyouts do.</summary>
    private void PlayOpen()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _rise.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        _root.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
    }

    /// <summary>A new page fades in under the header, so a switch reads as a switch.</summary>
    private void PlayPage()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var shift = new TranslateTransform();
        _content.RenderTransform = shift;
        shift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        _content.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
    }

    // ---- Navigation and keys ----------------------------------------------

    public void ShowOverview() => Go(PageKind.Overview, null);

    /// <summary>
    /// Every page drawn to a PNG, for checking the layout without a screen
    /// (`AiUsage.exe --snapshot <folder>`). The backdrop is the window manager's,
    /// so it is stood in for by the solid panel colour.
    /// </summary>
    public static void Snapshots(string folder)
    {
        System.IO.Directory.CreateDirectory(folder);
        var panel = new PanelView();
        var pages = new List<(string Name, PageKind Page, string? Account)>
        {
            ("overview", PageKind.Overview, null),
            ("settings", PageKind.Settings, null),
        };
        pages.AddRange(RailOrder().Select(u => (u.AccountKey, PageKind.Account, (string?)u.AccountKey)));

        foreach (var (name, page, account) in pages)
        {
            panel._page = page;
            panel._account = account;
            panel.Render();
            foreach (var held in new[] { false, true })
            {
                if (held && page != PageKind.Overview) continue;
                panel._ctrlHeld = held;
                panel.RenderRail(App.Repository.Cached());

                panel.Measure(new Size(PanelWidth, double.PositiveInfinity));
                var height = Math.Min(Math.Ceiling(panel.DesiredSize.Height), 2400);
                panel.Measure(new Size(PanelWidth, height));
                panel.Arrange(new Rect(0, 0, PanelWidth, height));
                panel.UpdateLayout();

                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(PanelWidth * 2), (int)(height * 2), 192, 192, PixelFormats.Pbgra32);
                bitmap.Render(panel);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var file = System.IO.File.Create(System.IO.Path.Combine(folder, $"{name}{(held ? "-ctrl" : "")}.png"));
                encoder.Save(file);
            }
            panel._ctrlHeld = false;
        }
        TraySnapshots(folder);
    }

    /// <summary>The tray icon in each of its forms, beside the pages.</summary>
    private static void TraySnapshots(string folder)
    {
        foreach (var (name, percent, number) in new[] { ("ring-62", 62f, false), ("ring-22", 22f, false), ("number-62", 62f, true), ("number-22", 22f, true), ("number-100", 100f, true), ("number-8", 8f, true) })
        {
            using var image = AiUsage.Tray.TrayIcon.Draw(percent, attention: false, number);
            image.Save(System.IO.Path.Combine(folder, $"tray-{name}.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    private void Go(PageKind page, string? account)
    {
        var changed = _page != page || _account != account;
        _page = page;
        _account = account;
        _scroller.ScrollToTop();
        Render();
        if (changed) PlayPage();
    }

    /// <summary>Called by the window: Ctrl+R, Ctrl+0-9, Ctrl+comma, Escape, and Ctrl held for the badges.</summary>
    public bool HandleKey(KeyEventArgs e, bool down)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            SetCtrl(down);
            return false;
        }
        if (!down) return false;

        if (e.Key == Key.Escape)
        {
            // A step or an unsaved address goes first; the page after.
            if (CancelInProgress()) return true;
            if (_page != PageKind.Overview) ShowOverview();
            else CloseRequested?.Invoke();
            return true;
        }

        if (Keyboard.Modifiers != ModifierKeys.Control) return false;
        switch (e.Key)
        {
            case Key.R:
                _ = RefreshAsync(force: true);
                return true;
            case Key.OemComma:
                Go(_page == PageKind.Settings ? PageKind.Overview : PageKind.Settings, null);
                return true;
            case Key.D0 or Key.NumPad0:
                ShowOverview();
                return true;
        }

        var digit = e.Key is >= Key.D1 and <= Key.D9 ? e.Key - Key.D1
            : e.Key is >= Key.NumPad1 and <= Key.NumPad9 ? e.Key - Key.NumPad1 : -1;
        var order = RailOrder();
        if (digit >= 0 && digit < order.Count)
        {
            Go(PageKind.Account, order[digit].AccountKey);
            return true;
        }
        return false;
    }

    public void SetCtrl(bool held)
    {
        if (_ctrlHeld == held) return;
        _ctrlHeld = held;
        RenderRail(App.Repository.Cached());
    }

    // ---- Refresh ----------------------------------------------------------

    private async Task RefreshAsync(bool force)
    {
        _refresh.IsEnabled = false;
        var spin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(1100)) { RepeatBehavior = RepeatBehavior.Forever };
        _spin.BeginAnimation(RotateTransform.AngleProperty, spin);
        try
        {
            await ((App)Application.Current).RefreshAsync(force);
        }
        finally
        {
            _spin.BeginAnimation(RotateTransform.AngleProperty, null);
            _spin.Angle = 0;
            _refresh.IsEnabled = true;
        }
    }

    /// <summary>Opening the panel asks for a current reading, unless the one on screen is fresh.</summary>
    public void Opened()
    {
        if (HoldsOpen) Refresh();
        else Render();
        PlayOpen();
        if (!App.Repository.IsFresh()) _ = RefreshAsync(force: false);
    }

    // ---- Data -------------------------------------------------------------

    private static UsageWindow? Tightest(ProviderUsage usage)
    {
        var counted = usage.Windows.Where(w => w.CountsTowardCombined && w.RemainingPercent != null).ToList();
        var pool = counted.Count > 0 ? counted : usage.Windows.Where(w => w.RemainingPercent != null).ToList();
        return pool.OrderBy(w => w.RemainingPercent).FirstOrDefault();
    }

    /// <summary>A limit and whose it is.</summary>
    public sealed record Limit(ProviderUsage Usage, UsageWindow Window);

    /// <summary>
    /// The one limit closest to running out across the working accounts: each
    /// account's tightest, then the lowest of those. What the tray shows when
    /// Settings asks for it instead of the combined level.
    /// </summary>
    public static Limit? Closest(Snapshot snapshot) => snapshot.Usages
        .Where(u => u.State == UsageState.Ok)
        .Select(u => Tightest(u) is { } window ? new Limit(u, window) : null)
        .OfType<Limit>()
        .OrderBy(l => l.Window.RemainingPercent)
        .FirstOrDefault();

    /// <summary>The order set in Settings; accounts it does not mention follow, by provider.</summary>
    private static List<ProviderUsage> RailOrder()
    {
        var order = PanelPrefs.Current.Order;
        return (App.Repository.Cached()?.Usages ?? [])
            .OrderBy(u => order.IndexOf(u.AccountKey) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(u => (int)u.Id)
            .ThenBy(u => u.AccountKey, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The overview: working accounts first, the one about to run out at the top; or the rail's order.</summary>
    private static List<ProviderUsage> OverviewOrder()
    {
        if (!PanelPrefs.Current.OverviewByRisk) return RailOrder();
        return (App.Repository.Cached()?.Usages ?? [])
            .OrderBy(u => u.State == UsageState.Ok ? 0 : 1)
            .ThenBy(u => Tightest(u)?.RemainingPercent ?? 101f)
            .ThenBy(u => u.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }

    // ---- Rendering --------------------------------------------------------

    /// <summary>
    /// A new reading or the clock: every page is drawn again, except Settings,
    /// whose fields and steps would lose what is being typed and where the caret
    /// is. There only the rail, header and footer follow.
    /// </summary>
    private void Refresh()
    {
        if (_page != PageKind.Settings)
        {
            Render();
            return;
        }
        var snapshot = App.Repository.Cached();
        RenderRail(snapshot);
        RenderHeader(snapshot);
        RenderFooter(snapshot);
        // "checked 2h ago" ages with the clock; the rest of the section is left alone.
        ShowUpdateState();
    }

    private void Render()
    {
        var snapshot = App.Repository.Cached();
        if (_page == PageKind.Account && snapshot?.Usages.Any(u => u.AccountKey == _account) != true)
        {
            _page = PageKind.Overview;
            _account = null;
        }

        RenderRail(snapshot);
        RenderHeader(snapshot);
        RenderFooter(snapshot);

        _content.Children.Clear();
        switch (_page)
        {
            case PageKind.Overview:
                RenderOverview(snapshot);
                break;
            case PageKind.Account:
                RenderAccount(snapshot!.Usages.First(u => u.AccountKey == _account));
                break;
            case PageKind.Settings:
                RenderSettings();
                break;
        }
    }

    private void RenderRail(Snapshot? snapshot)
    {
        _rail.Children.Clear();
        _railBottom.Children.Clear();

        _rail.Children.Add(RailButton(RailIcon(RailIcons.Grid), _page == PageKind.Overview, "Ctrl+0", "Overview",
            () => Go(PageKind.Overview, null)));
        _rail.Children.Add(new Border
        {
            Width = 18,
            Height = 1.5,
            CornerRadius = new CornerRadius(1),
            Background = Faint,
            Margin = new Thickness(0, 6, 0, 6),
        });

        var order = RailOrder();
        for (var i = 0; i < order.Count; i++)
        {
            var usage = order[i];
            var key = usage.AccountKey;
            var level = usage.State == UsageState.Ok ? Tightest(usage)?.RemainingPercent : null;
            var ring = Ring(usage.Id, level, 32, usage.State != UsageState.Ok, usage.AccountName);
            _rail.Children.Add(RailButton(ring, _page == PageKind.Account && _account == key,
                i < 9 ? $"Ctrl+{i + 1}" : null, usage.DisplayName, () => Go(PageKind.Account, key)));
        }

        // A downloaded update is a dot on the gear, nothing louder: it goes in by itself anyway.
        _railShowsUpdate = UpdateWaiting;
        _railBottom.Children.Add(RailButton(RailIcon(RailIcons.Gear), _page == PageKind.Settings, "Ctrl+,",
            _railShowsUpdate ? "Settings · an update is ready" : "Settings",
            () => Go(PageKind.Settings, null), dot: _railShowsUpdate));
    }

    private FrameworkElement RailButton(UIElement content, bool selected, string? shortcut, string name, Action go, bool dot = false)
    {
        var holder = new Grid { Width = 42, Height = 42, Margin = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent };
        var hover = new Ellipse { Fill = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), Opacity = 0 };
        holder.Children.Add(hover);
        if (selected)
        {
            holder.Children.Add(new Ellipse
            {
                Fill = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                Stroke = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1,
            });
        }
        if (content is FrameworkElement element)
        {
            element.HorizontalAlignment = HorizontalAlignment.Center;
            element.VerticalAlignment = VerticalAlignment.Center;
            if (!selected && element is TextBlock glyph) glyph.Foreground = Secondary;
            if (!selected && element is System.Windows.Shapes.Path mark) mark.Fill = Secondary;
        }
        holder.Children.Add(content);
        if (dot)
        {
            // At the icon's top-right corner, ringed in the panel's colour so it
            // reads as a mark of its own rather than part of the gear.
            holder.Children.Add(new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = new SolidColorBrush(Severity.Normal),
                Stroke = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x18)),
                StrokeThickness = 1.5,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 10, 10, 0),
                IsHitTestVisible = false,
            });
        }

        // Held Ctrl shows what each item's shortcut is.
        if (_ctrlHeld && shortcut != null)
        {
            holder.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2A)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(4, 0, 4, 1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, -6),
                IsHitTestVisible = false,
                Child = Text(shortcut, 10, FontWeights.SemiBold, Primary),
            });
        }

        var button = new Button
        {
            Style = Res("BareButton"),
            Content = holder,
            ToolTip = shortcut == null ? name : $"{name}  ({shortcut})",
        };
        AutomationName(button, name);
        button.Click += (_, _) => go();
        if (!selected)
        {
            button.MouseEnter += (_, _) => hover.Opacity = 1;
            button.MouseLeave += (_, _) => hover.Opacity = 0;
        }
        return button;
    }

    private void RenderHeader(Snapshot? snapshot)
    {
        switch (_page)
        {
            case PageKind.Overview:
                _title.Text = "Usage";
                var usages = snapshot?.Usages ?? [];
                var broken = usages.Count(u => u.State != UsageState.Ok);
                _subtitle.Text = usages.Count == 0 ? ""
                    : $"{usages.Count} subscription{(usages.Count == 1 ? "" : "s")}"
                      + (broken == 0 ? "" : $" · {broken} need{(broken == 1 ? "s" : "")} attention");
                break;
            case PageKind.Settings:
                _title.Text = "Settings";
                _subtitle.Text = "Server, accounts, tray, look, updates";
                break;
            default:
                var usage = snapshot?.Usages.FirstOrDefault(u => u.AccountKey == _account);
                _title.Text = usage?.DisplayName ?? "Usage";
                _subtitle.Text = usage == null ? ""
                    : usage.State != UsageState.Ok ? "Needs attention" : $"Read {Format.Ago(usage.FetchedAt)}";
                break;
        }
        _subtitle.Visibility = _subtitle.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenderFooter(Snapshot? snapshot)
    {
        var problem = App.Repository.ServerProblem;
        // The footer shares its row with two buttons, so it says when; where the
        // reading came from is in the tooltip. A problem is shown whole.
        var full = Format.Status(snapshot, problem, App.Repository.UsesServer);
        _status.Text = problem == null ? Format.Status(snapshot, null, viaServer: false) : full;
        _status.ToolTip = full;
        var colour = problem != null ? Severity.Critical
            : snapshot == null ? Color.FromRgb(0x80, 0x80, 0x80)
            : DateTimeOffset.UtcNow - snapshot.FetchedAt > TimeSpan.FromMinutes(20) ? Severity.Warning
            : Color.FromRgb(0x5B, 0xC4, 0x8A);
        _statusDot.Fill = new SolidColorBrush(colour);
    }

    // ---- Overview ---------------------------------------------------------

    private void RenderOverview(Snapshot? snapshot)
    {
        if (snapshot == null || snapshot.Usages.Count == 0)
        {
            // A reading with nothing in it means nothing has been added, and
            // nothing will arrive until something is.
            var empty = snapshot != null && App.Repository.ServerProblem == null;
            _content.Children.Add(Card(Text(
                App.Repository.ServerProblem
                    ?? (empty ? "No accounts yet. Add the subscriptions you have in Settings." : "Waiting for the first reading"),
                Body, FontWeights.Normal, Secondary)));
            return;
        }

        var combined = CombinedUsage.Of(snapshot);
        if (combined.Percent is { } percent) _content.Children.Add(CombinedCard(snapshot, percent, combined.Providers));

        foreach (var usage in OverviewOrder())
        {
            _content.Children.Add(AccountCard(usage));
        }
    }

    private FrameworkElement CombinedCard(Snapshot snapshot, float percent, int providers)
    {
        var stack = new StackPanel();

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var ring = new Grid { Width = 66, Height = 66 };
        ring.Children.Add(new RingGauge
        {
            Percent = percent,
            FillBrush = new SolidColorBrush(Severity.ColorFor(percent)),
            TrackBrush = Faint,
            Width = 66,
            Height = 66,
        });
        ring.Children.Add(new TextBlock
        {
            Text = "Σ",
            FontFamily = Display,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = Secondary,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        top.Children.Add(ring);

        var figures = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        // WPF cannot track capitals apart, and untracked small caps look cramped,
        // so the eyebrow is in sentence case.
        figures.Children.Add(Text("Combined", Caption, FontWeights.SemiBold, Secondary));
        var big = new TextBlock { FontFamily = Display, Foreground = Severity.TextBrush(percent) };
        big.Inlines.Add(new Run(((int)Math.Round(percent)).ToString()) { FontSize = 44, FontWeight = FontWeights.SemiBold });
        big.Inlines.Add(new Run("%") { FontSize = 24, FontWeight = FontWeights.SemiBold });
        big.Inlines.Add(new Run(" left") { FontSize = 15, Foreground = Secondary });
        figures.Children.Add(big);
        figures.Children.Add(Text($"across {providers} subscription{(providers == 1 ? "" : "s")}", Caption, FontWeights.Normal, Secondary));
        Grid.SetColumn(figures, 1);
        top.Children.Add(figures);
        stack.Children.Add(top);

        var week = App.Repository.History.Combined().Where(p => p.At >= DateTimeOffset.UtcNow.AddDays(-7)).ToList();
        if (week.Count > 1)
        {
            stack.Children.Add(new DrawdownChart
            {
                Points = week,
                LineColor = Severity.ColorFor(percent),
                Axis = true,
                Height = 78,
                Margin = new Thickness(0, 10, 0, 0),
            });
        }

        // When the next long window refills, which is when the figure next lifts.
        var now = DateTimeOffset.UtcNow;
        var next = snapshot.Usages
            .Where(u => u.State == UsageState.Ok)
            .SelectMany(u => CombinedUsage.LongWindows(u).Select(w => (Usage: u, Window: w)))
            .Where(c => c.Window.ResetAt > now)
            .OrderBy(c => c.Window.ResetAt)
            .FirstOrDefault();
        if (next.Usage != null)
        {
            stack.Children.Add(Text(
                $"Next lift {Format.When(next.Window.ResetAt!.Value)} — {next.Usage.DisplayName} {WindowName.Display(next.Window.Label)} resets",
                Caption, FontWeights.Normal, Secondary, new Thickness(0, 8, 0, 0)));
        }

        return Card(stack, radius: 18);
    }

    private FrameworkElement AccountCard(ProviderUsage usage)
    {
        var stack = new StackPanel();

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(MarkDisc(usage.Id, 28));
        var names = new StackPanel { Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(Text(usage.DisplayName, Heading, FontWeights.SemiBold, usage.State == UsageState.Ok ? Primary : Secondary));
        if (usage.Plan is { Length: > 0 } plan) names.Children.Add(Text(plan, Caption, FontWeights.Normal, Secondary));
        Grid.SetColumn(names, 1);
        head.Children.Add(names);
        var chevron = Glyph("", Micro);
        chevron.Foreground = Secondary;
        Grid.SetColumn(chevron, 2);
        head.Children.Add(chevron);
        stack.Children.Add(head);

        if (usage.State != UsageState.Ok)
        {
            stack.Children.Add(Text(
                usage.State == UsageState.NotConfigured ? "Not set up" : usage.Message ?? "Unavailable",
                Caption, FontWeights.Normal, new SolidColorBrush(Severity.Warning), new Thickness(37, 8, 0, 0)));
        }
        else
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var window in usage.Windows)
            {
                stack.Children.Add(WindowRow(window, now));
            }
        }

        var card = Card(stack);
        card.Cursor = Cursors.Hand;
        var face = (Border)((Border)card).Child;
        card.MouseEnter += (_, _) =>
        {
            face.Background = HoverTile;
            face.BorderBrush = HoverEdge;
            chevron.Foreground = Primary;
        };
        card.MouseLeave += (_, _) =>
        {
            face.Background = FaintTile;
            face.BorderBrush = Edge;
            chevron.Foreground = Secondary;
        };
        card.MouseLeftButtonUp += (_, _) => Go(PageKind.Account, usage.AccountKey);
        card.ToolTip = $"Show {usage.DisplayName}'s history and forecast";
        return card;
    }

    /// <summary>A window in a card: its name and when it refills, then the bar.</summary>
    private FrameworkElement WindowRow(UsageWindow window, DateTimeOffset now)
    {
        var row = new StackPanel { Margin = new Thickness(0, 11, 0, 0) };
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock { FontFamily = Ui, VerticalAlignment = VerticalAlignment.Bottom, TextTrimming = TextTrimming.CharacterEllipsis };
        name.Inlines.Add(new Run(WindowName.Display(window.Label))
        {
            FontSize = Body,
            FontWeight = FontWeights.SemiBold,
            Foreground = window.CountsTowardCombined ? Primary : Secondary,
        });
        if (Format.Reset(window.ResetAt) is { } reset)
        {
            name.Inlines.Add(new Run($"   resets in {reset}") { FontSize = Caption, Foreground = Secondary });
        }
        line.Children.Add(name);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        switch (Pace.Of(window, now))
        {
            case Pace.Kind.Empty:
                right.Children.Add(PaceBadge(Pace.Kind.Empty, compact: false));
                break;
            case Pace.Kind.Ahead:
                right.Children.Add(PaceBadge(Pace.Kind.Ahead, compact: true));
                right.Children.Add(Level(window.RemainingPercent, Heading));
                break;
            default:
                right.Children.Add(Level(window.RemainingPercent, Heading));
                break;
        }
        Grid.SetColumn(right, 1);
        line.Children.Add(right);
        row.Children.Add(line);

        if (window.RemainingPercent is { } percent)
        {
            row.Children.Add(new PaceMeter
            {
                Percent = percent,
                Even = Pace.EvenPercent(window, now),
                Margin = new Thickness(0, 7, 0, 0),
            });
        }
        return row;
    }

    // ---- An account -------------------------------------------------------

    private void RenderAccount(ProviderUsage usage)
    {
        var tightest = usage.State == UsageState.Ok ? Tightest(usage) : null;

        var summary = new Grid();
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        summary.Children.Add(Ring(usage.Id, tightest?.RemainingPercent, 58, usage.State != UsageState.Ok));
        var side = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var chips = new StackPanel { Orientation = Orientation.Horizontal };
        if (usage.Plan is { Length: > 0 } plan) chips.Children.Add(Chip(plan));
        if (usage.Stale) chips.Children.Add(Chip("Earlier reading", Severity.Warning));
        if (chips.Children.Count > 0) side.Children.Add(chips);
        if (tightest is { RemainingPercent: { } level })
        {
            var figure = new TextBlock { FontFamily = Display, Margin = new Thickness(0, 4, 0, 0) };
            figure.Inlines.Add(new Run(Format.Percent(level)) { FontSize = 30, FontWeight = FontWeights.SemiBold, Foreground = Severity.TextBrush(level) });
            figure.Inlines.Add(new Run($"  left of {WindowName.Display(tightest.Label)}") { FontSize = Body, Foreground = Secondary });
            side.Children.Add(figure);
        }
        else
        {
            side.Children.Add(Text(
                usage.State == UsageState.NotConfigured ? "Not set up" : usage.Message ?? "Unavailable",
                Body, FontWeights.Normal, Secondary, new Thickness(0, 4, 0, 0)));
        }
        Grid.SetColumn(side, 1);
        summary.Children.Add(side);
        _content.Children.Add(Card(summary, radius: 18));

        if (usage.State != UsageState.Ok) return;
        foreach (var window in usage.Windows) _content.Children.Add(WindowCard(usage, window));
    }

    /// <summary>A window: the figure, the bar with its pace tick, the chart and the burn.</summary>
    private FrameworkElement WindowCard(ProviderUsage usage, UsageWindow window)
    {
        var now = DateTimeOffset.UtcNow;
        var stack = new StackPanel();

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var names = new StackPanel();
        var title = Text(WindowName.Display(window.Label), 15, FontWeights.SemiBold, Primary);
        title.FontFamily = Display;
        names.Children.Add(title);
        names.Children.Add(Text(ResetText(window, now), Caption, FontWeights.Normal, Secondary, new Thickness(0, 1, 0, 0)));
        top.Children.Add(names);
        var level = Level(window.RemainingPercent, 24);
        Grid.SetColumn(level, 1);
        top.Children.Add(level);
        stack.Children.Add(top);

        if (window.RemainingPercent is { } percent)
        {
            stack.Children.Add(new PaceMeter
            {
                Percent = percent,
                Even = Pace.EvenPercent(window, now),
                Height = 8,
                Margin = new Thickness(0, 10, 0, 0),
            });
        }

        var badges = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        badges.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        badges.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        if (Pace.Of(window, now) is { } pace) left.Children.Add(PaceBadge(pace, compact: false));
        if (!window.CountsTowardCombined) left.Children.Add(Chip("Not combined"));
        badges.Children.Add(left);
        if (Pace.EvenPercent(window, now) is { } even)
        {
            var hint = Text($"even pace {Format.Percent((float)even)}", Caption, FontWeights.Normal, Secondary);
            hint.ToolTip = "What an even spread over the cycle would leave now: the tick on the bar.";
            Grid.SetColumn(hint, 1);
            badges.Children.Add(hint);
        }
        stack.Children.Add(badges);

        // The same forecast the server page draws, from this window's own readings.
        var key = usage.AccountKey;
        // A window that refills within a day charts half a day, a couple of its
        // cycles, as the Mac panel does: a week of 5-hour cycles is a comb.
        var span = Projection.PeriodOf(window.Label) is { } period && period < TimeSpan.FromDays(1)
            ? TimeSpan.FromHours(12)
            : UsageHistory.ChartSpan(window);
        var series = App.Repository.History.Series(key, window.Label, span);
        var track = Projection.For(window, App.Repository.History.Series(key, window.Label, Projection.UsualBasis), now);
        stack.Children.Add(new DrawdownChart
        {
            Points = series,
            Forecast = track,
            Ahead = span,
            LineColor = window.RemainingPercent is { } p ? Severity.ColorFor(p) : Severity.Normal,
            Height = 96,
            Margin = new Thickness(0, 10, 0, 0),
        });

        if (track is { Measured: true })
        {
            var (rates, outcome, empties) = Format.Burn(track, Projection.EmptyAt(track, now + Projection.Horizon));
            var burn = new TextBlock
            {
                FontFamily = Ui,
                FontSize = Caption,
                Foreground = Secondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
            };
            burn.Inlines.Add(new Run(rates));
            burn.Inlines.Add(new Run(outcome.Replace(' ', ' ')) // wraps as one phrase, not mid-date
            {
                Foreground = empties ? new SolidColorBrush(Severity.Critical) : Secondary,
                FontWeight = empties ? FontWeights.SemiBold : FontWeights.Normal,
            });
            stack.Children.Add(burn);
        }

        if (window.Detail is { Length: > 0 } detail)
        {
            stack.Children.Add(Text(detail, Caption, FontWeights.Normal, Secondary, new Thickness(0, 6, 0, 0)));
        }

        return Card(stack);
    }

    private static string ResetText(UsageWindow window, DateTimeOffset now)
    {
        if (window.ResetAt is not { } reset) return "No reset reported";
        if (reset <= now) return "Resetting now";
        return $"Resets in {Format.Span(reset - now)} · {Format.When(reset)}";
    }

    // ---- Settings ---------------------------------------------------------

    private void RenderSettings()
    {
        var prefs = PanelPrefs.Current;

        // Server and its accounts first, as on the Mac: without them nothing else has data.
        _content.Children.Add(ServerSection());
        if (App.Repository.UsesServer) _content.Children.Add(AccountsSection());

        // Order: the rail and Ctrl+1-9 follow it.
        var order = new StackPanel();
        var accounts = RailOrder();
        for (var i = 0; i < accounts.Count; i++)
        {
            var usage = accounts[i];
            var index = i;
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(MarkDisc(usage.Id, 24));
            var name = Text(usage.DisplayName, Body, FontWeights.Normal, Primary, new Thickness(10, 0, 0, 0));
            name.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            if (i < 9)
            {
                var shortcut = Text($"Ctrl+{i + 1}", Caption, FontWeights.Normal, Secondary, new Thickness(0, 0, 10, 0));
                shortcut.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(shortcut, 2);
                row.Children.Add(shortcut);
            }
            var arrows = new StackPanel { Orientation = Orientation.Horizontal };
            var up = new Button { Style = Res("SoftCircle"), Content = "", Width = 24, Height = 24, FontSize = 10, IsEnabled = i > 0, ToolTip = "Move up" };
            var down = new Button { Style = Res("SoftCircle"), Content = "", Width = 24, Height = 24, FontSize = 10, Margin = new Thickness(4, 0, 0, 0), IsEnabled = i < accounts.Count - 1, ToolTip = "Move down" };
            up.Click += (_, _) => Move(accounts, index, -1);
            down.Click += (_, _) => Move(accounts, index, 1);
            arrows.Children.Add(up);
            arrows.Children.Add(down);
            Grid.SetColumn(arrows, 3);
            row.Children.Add(arrows);
            order.Children.Add(row);
        }
        order.Children.Add(Text("The rail and Ctrl+1–9 follow this order.", Caption, FontWeights.Normal, Secondary, new Thickness(0, 6, 0, 0)));
        var byRisk = new CheckBox
        {
            Style = Res("Switch"),
            Content = "Most at risk first on the overview",
            IsChecked = prefs.OverviewByRisk,
            Margin = new Thickness(0, 12, 0, 0),
        };
        byRisk.Checked += (_, _) => prefs.Update(p => p.OverviewByRisk = true);
        byRisk.Unchecked += (_, _) => prefs.Update(p => p.OverviewByRisk = false);
        order.Children.Add(byRisk);
        if (prefs.Order.Count > 0)
        {
            var reset = SoftButton("Reset order", "Back to the default order");
            reset.HorizontalAlignment = HorizontalAlignment.Left;
            reset.Margin = new Thickness(0, 10, 0, 0);
            reset.Click += (_, _) =>
            {
                prefs.Update(p => p.Order = []);
                Render();
            };
            order.Children.Add(reset);
        }
        _content.Children.Add(Section("Order", "", order));

        _content.Children.Add(TraySection(prefs));

        // Look: how much glass, how solid the tiles.
        var look = new StackPanel();
        look.Children.Add(SliderRow("Glass", prefs.Glass,
            value => value < 0.02 ? "Flat" : value > 0.98 ? "Clear" : $"{Math.Round(value * 100)}% glass",
            value => prefs.Update(p => p.Glass = value)));
        look.Children.Add(SliderRow("Tiles", prefs.Tiles,
            value => value < 0.02 ? "See-through" : value > 0.98 ? "Solid" : $"{Math.Round(value * 100)}% solid",
            value => prefs.Update(p => p.Tiles = value), new Thickness(0, 12, 0, 0)));
        look.Children.Add(Text("How solid the tiles are, apart from the panel behind them.", Caption, FontWeights.Normal, Secondary, new Thickness(0, 6, 0, 0)));
        _content.Children.Add(Section("Look", "", look));

        // General: startup; without a server, the app's own sign-ins, which live in the dashboard.
        var general = new StackPanel();
        var startup = new CheckBox
        {
            Style = Res("Switch"),
            Content = "Start with Windows",
            IsChecked = Autostart.Enabled,
        };
        startup.Checked += (_, _) => Autostart.Set(true);
        startup.Unchecked += (_, _) => Autostart.Set(false);
        general.Children.Add(startup);
        if (!App.Repository.UsesServer)
        {
            var signIns = SoftButton("This app's own sign-ins…", "Without a server, the app signs in to each provider itself; that is set up in the dashboard", "\uE8A7");
            signIns.HorizontalAlignment = HorizontalAlignment.Left;
            signIns.Margin = new Thickness(0, 12, 0, 0);
            signIns.Click += (_, _) => DashboardRequested?.Invoke();
            general.Children.Add(signIns);
        }
        _content.Children.Add(Section("General", "\uE713", general));

        _content.Children.Add(UpdatesSection());

        // The version is in the Updates section; with a beta's it would not fit this line.
        _content.Children.Add(Text("Ctrl+R refresh · Ctrl+1–9 accounts · Esc back", Micro, FontWeights.Normal, Secondary, new Thickness(0, 4, 0, 6)));
        ((TextBlock)_content.Children[^1]).HorizontalAlignment = HorizontalAlignment.Center;
    }

    /// <summary>
    /// The tray icon, the Windows side of the Mac's menu bar setting. The Mac can
    /// show several limits beside its ring; a tray icon is a 16px square, so here
    /// it is one figure, the combined level or the closest limit, as a ring or a number.
    /// </summary>
    private FrameworkElement TraySection(PanelPrefs prefs)
    {
        var stack = new StackPanel();

        var showRow = new Grid();
        showRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        showRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var showLabel = Text("Shows", Body, FontWeights.Normal, Primary);
        showLabel.VerticalAlignment = VerticalAlignment.Center;
        showRow.Children.Add(showLabel);
        var segments = new Capsule
        {
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
        };
        var pair = new StackPanel { Orientation = Orientation.Horizontal };
        var hint = Text("", Caption, FontWeights.Normal, Secondary, new Thickness(0, 6, 0, 0));
        void Describe(bool closest) => hint.Text = closest
            ? "The one limit closest to running out; its owner is in the tooltip."
            : "Every subscription's long windows, averaged.";
        foreach (var (label, closest) in new[] { ("Combined", false), ("Closest limit", true) })
        {
            var option = new RadioButton
            {
                Style = Res("PanelSegment"),
                Content = label,
                GroupName = "TrayShows",
                IsChecked = prefs.TrayShowsClosest == closest,
            };
            option.Checked += (_, _) =>
            {
                prefs.Update(p => p.TrayShowsClosest = closest);
                Describe(closest);
            };
            pair.Children.Add(option);
        }
        segments.Child = pair;
        Grid.SetColumn(segments, 1);
        showRow.Children.Add(segments);
        stack.Children.Add(showRow);
        Describe(prefs.TrayShowsClosest);
        stack.Children.Add(hint);

        var number = new CheckBox
        {
            Style = Res("Switch"),
            Content = "As a number instead of a ring",
            IsChecked = prefs.TrayShowsNumber,
            Margin = new Thickness(0, 12, 0, 0),
        };
        number.Checked += (_, _) => prefs.Update(p => p.TrayShowsNumber = true);
        number.Unchecked += (_, _) => prefs.Update(p => p.TrayShowsNumber = false);
        stack.Children.Add(number);

        return Section("Tray icon", "\uE7F4", stack);
    }

    private void Move(List<ProviderUsage> accounts, int index, int offset)
    {
        var keys = accounts.Select(u => u.AccountKey).ToList();
        var to = Math.Clamp(index + offset, 0, keys.Count - 1);
        (keys[index], keys[to]) = (keys[to], keys[index]);
        PanelPrefs.Current.Update(p => p.Order = keys);
        Render();
    }

    private FrameworkElement SliderRow(string title, double value, Func<double, string> describe, Action<double> set, Thickness? margin = null)
    {
        var stack = new StackPanel { Margin = margin ?? new Thickness(0) };
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Text(title, Body, FontWeights.Normal, Primary));
        var reading = Text(describe(value), Caption, FontWeights.Normal, Secondary);
        Grid.SetColumn(reading, 1);
        head.Children.Add(reading);
        stack.Children.Add(head);
        var slider = new Slider { Style = Res("SoftSlider"), Value = value, Margin = new Thickness(0, 6, 0, 0) };
        slider.ValueChanged += (_, e) =>
        {
            reading.Text = describe(e.NewValue);
            set(e.NewValue);
        };
        stack.Children.Add(slider);
        return stack;
    }

    // ---- Pieces -----------------------------------------------------------

    /// <summary>A tile on the panel: a solid layer (the Tiles setting), a faint one, a hairline.</summary>
    private static FrameworkElement Card(UIElement content, double radius = 16)
    {
        var inner = new Border
        {
            Background = FaintTile,
            BorderBrush = Edge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radius),
            Padding = new Thickness(14, 12, 14, 12),
            Child = content,
        };
        return new Border
        {
            Background = SolidTile,
            CornerRadius = new CornerRadius(radius),
            Margin = new Thickness(0, 0, 0, 10),
            Child = inner,
        };
    }

    private static FrameworkElement Section(string title, string glyph, UIElement content)
    {
        var stack = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var icon = Glyph(glyph, Caption);
        icon.Foreground = Secondary;
        head.Children.Add(icon);
        head.Children.Add(Text(title, Caption, FontWeights.SemiBold, Secondary, new Thickness(7, 0, 0, 0)));
        stack.Children.Add(head);
        stack.Children.Add(content);
        return Card(stack);
    }

    /// <summary>A provider's mark inside a ring of what is left; a red dot when it needs attention.</summary>
    /// <summary>
    /// A provider's mark inside a ring of what is left, with a badge at its
    /// foot as the Mac's rail has it: a red "!" when the account needs
    /// attention, or else the first letter of the account's own name, so two
    /// accounts of one provider ("Claude · Work", "Claude · Home") tell apart.
    /// </summary>
    private static FrameworkElement Ring(ProviderId id, float? percent, double size, bool broken, string? name = null)
    {
        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(new RingGauge
        {
            Percent = percent,
            FillBrush = new SolidColorBrush(percent is { } p ? Severity.ColorFor(p) : Severity.Normal),
            TrackBrush = Faint,
            Logo = ProviderArt.Logo(id),
            LogoBrush = Primary,
            Width = size,
            Height = size,
        });
        if (broken) grid.Children.Add(RingBadge("!", Severity.Critical, Colors.White));
        else if (Initial(name) is { } letter) grid.Children.Add(RingBadge(letter, Color.FromRgb(0x2E, 0x2E, 0x33), Color.FromRgb(0xF2, 0xF2, 0xF2)));
        return grid;
    }

    /// <summary>The first letter or digit of a name, as the name writes it; null for no name.</summary>
    private static string? Initial(string? name) =>
        name?.FirstOrDefault(char.IsLetterOrDigit) is { } c and not '\0' ? c.ToString() : null;

    /// <summary>A small disc overhanging a ring's lower right, with one character on it.</summary>
    private static FrameworkElement RingBadge(string text, Color fill, Color ink)
    {
        var badge = new Grid
        {
            Width = 13,
            Height = 13,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -3, -3),
            IsHitTestVisible = false,
        };
        badge.Children.Add(new Ellipse
        {
            Fill = new SolidColorBrush(fill),
            Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 0.75,
        });
        badge.Children.Add(new TextBlock
        {
            Text = text,
            FontFamily = Ui,
            FontSize = 8,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(ink),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 0.5),
        });
        return badge;
    }

    private static FrameworkElement MarkDisc(ProviderId id, double size)
    {
        var disc = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        disc.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)) });
        disc.Children.Add(new System.Windows.Shapes.Path
        {
            Data = ProviderArt.Logo(id),
            Fill = Primary,
            Stretch = Stretch.Uniform,
            Width = size * 0.52,
            Height = size * 0.52,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return disc;
    }

    /// <summary>"62%" and, quieter, "left".</summary>
    private static FrameworkElement Level(float? percent, double size)
    {
        var text = new TextBlock { FontFamily = Display, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6, 0, 0, 0) };
        text.Inlines.Add(new Run(percent is { } p ? Format.Percent(p) : "—")
        {
            FontSize = size,
            FontWeight = FontWeights.SemiBold,
            Foreground = percent is { } q ? Severity.TextBrush(q) : Secondary,
        });
        if (percent != null) text.Inlines.Add(new Run(" left") { FontSize = size * 0.78, Foreground = Secondary });
        return text;
    }

    /// <summary>The hue on the icon and capsule; the words in the label colour, so they read on any backdrop.</summary>
    private static FrameworkElement PaceBadge(Pace.Kind kind, bool compact)
    {
        var tint = kind switch
        {
            Pace.Kind.Empty => Severity.Critical,
            Pace.Kind.Ahead => Severity.Warning,
            Pace.Kind.On => Color.FromRgb(0xA8, 0xA8, 0xA8),
            _ => Severity.Normal,
        };
        var glyph = kind switch
        {
            Pace.Kind.Empty => "",
            Pace.Kind.Ahead => "", // a flame where the font has one
            Pace.Kind.On => "",
            _ => "",
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = Glyph(glyph, 10);
        icon.Foreground = new SolidColorBrush(tint);
        content.Children.Add(icon);
        // Compact, in an overview row, it is one word: an icon alone reads as a blob.
        var words = compact && kind == Pace.Kind.Ahead ? "Ahead" : Pace.Text(kind);
        content.Children.Add(Text(words, Micro, FontWeights.SemiBold, Primary, new Thickness(5, 0, 0, 0)));
        return new Capsule
        {
            Background = new SolidColorBrush(Color.FromArgb(0x3D, tint.R, tint.G, tint.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x73, tint.R, tint.G, tint.B)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(7, 2, 8, 2),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = Pace.Help(kind),
            Child = content,
        };
    }

    private static FrameworkElement Chip(string text, Color? tint = null) => new Capsule
    {
        Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
        BorderBrush = Faint,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(7, 1, 7, 2),
        Margin = new Thickness(0, 0, 6, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = Text(text, Micro, FontWeights.SemiBold, tint is { } t ? new SolidColorBrush(t) : Secondary),
    };

    private static TextBlock Text(string text, double size, FontWeight weight, Brush brush, Thickness? margin = null) => new()
    {
        Text = text,
        FontFamily = Ui,
        FontSize = size,
        FontWeight = weight,
        Foreground = brush,
        Margin = margin ?? new Thickness(0),
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>One of the rail's drawn marks, at the size the icon font's glyphs had.</summary>
    private static System.Windows.Shapes.Path RailIcon(Geometry geometry) => new()
    {
        Data = geometry,
        Fill = Primary,
        Stretch = Stretch.Uniform,
        Width = 15,
        Height = 15,
    };

    private static TextBlock Glyph(string glyph, double size) => new()
    {
        Text = glyph,
        FontFamily = Icons,
        FontSize = size,
        Foreground = Primary,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A soft capsule with its words and, before them, an icon from the system's icon font.</summary>
    private Button SoftButton(string text, string tip, string? glyph = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph != null)
        {
            var icon = Glyph(glyph, Caption);
            icon.Margin = new Thickness(0, 1, 7, 0);
            content.Children.Add(icon);
        }
        content.Children.Add(new TextBlock
        {
            Text = text,
            FontFamily = Ui,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 1),
        });
        return new Button { Style = Res("SoftButton"), Content = content, ToolTip = tip.Length == 0 ? null : tip };
    }

    private Style Res(string key) => (Style)FindResource(key);

    private static void AutomationName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);

    private static Brush FrozenEdge(byte top, byte bottom)
    {
        var brush = new LinearGradientBrush(
            Color.FromArgb(top, 0xFF, 0xFF, 0xFF),
            Color.FromArgb(bottom, 0xFF, 0xFF, 0xFF),
            new Point(0, 0),
            new Point(0, 1));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

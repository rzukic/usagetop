using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using AiUsage.Data;
using AiUsage.Ui;

namespace AiUsage.Views;

/// <summary>Which page of the panel is showing.</summary>
public abstract record Pane
{
    public sealed record Overview : Pane;

    public sealed record Account(string Key) : Pane;

    public sealed record Settings : Pane;
}

/// <summary>
/// The panel, as on the Mac: a rail of subscriptions down the left, the page on
/// the right between a header and a footer. Drawn again whenever a reading
/// arrives, a preference changes or the clock moves on.
/// </summary>
public sealed class PanelView : Border
{
    public const double PanelWidth = 424;
    public const double Radius = 26;
    private const double RailWidth = 58;

    private readonly AppModel _model;
    private readonly bool _flat;
    private ScrollViewer? _scroller;
    private SettingsPage? _settings;
    private Control? _header;
    private Control? _page;
    private Control? _footer;

    /// <summary>
    /// <paramref name="flat"/> lays the page out in full, without scrolling: for
    /// drawing it to a file, where a scroll view would come out empty.
    /// </summary>
    public PanelView(AppModel model, bool flat = false)
    {
        _model = model;
        _flat = flat;
        Width = PanelWidth;
        CornerRadius = new CornerRadius(Radius);
        ClipToBounds = true;
        BorderThickness = new Thickness(1);
        Rebuild();
    }

    public Pane Pane { get; private set; } = new Pane.Overview();

    public event Action? QuitRequested;

    /// <summary>
    /// The panel's own surface and its tiles as Settings › Look has them. On
    /// KDE, which blurs behind it, the panel may be clear; elsewhere nothing
    /// blurs, so "glass" lets only a little through and text stays readable.
    /// Applied in place, without a redraw, so a slider can be dragged.
    /// </summary>
    public void ApplyLook()
    {
        var prefs = PanelPrefs.Current;
        Look.Tiles = prefs.Tiles;
        var (top, bottom) = Look.Panel;
        var through = Desktop.IsKde ? 0.45 : 0.18;
        Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            Opacity = _flat ? 1 : 1 - Math.Clamp(prefs.Glass, 0, 1) * through,
            GradientStops = { new GradientStop(top, 0), new GradientStop(bottom, 1) },
        };
        BorderBrush = Look.Ink(Look.Dark ? 0.13 : 0.1);
        foreach (var card in this.GetVisualDescendants().OfType<Border>().Where(b => Equals(b.Tag, Bits.CardTag)))
        {
            card.Background = Look.Tile();
        }
    }

    public void Show(Pane pane)
    {
        // Settings opened afresh shows what is saved, not what was typed last time.
        if (pane is Pane.Settings && Pane is not Pane.Settings) _settings = null;
        Pane = pane;
        Rebuild();
        _scroller?.ScrollToHome();
        if (pane is Pane.Settings) Avalonia.Threading.Dispatcher.UIThread.Post(() => _settings?.FocusFirstEmpty());
    }

    /// <summary>The panel shows Settings, where a new reading should not redraw what is being typed.</summary>
    public bool Editing => Pane is Pane.Settings;

    /// <summary>Drawn again: the window may want another height.</summary>
    public event Action? Redrawn;

    /// <summary>
    /// How tall the panel would like to be: everything shown without scrolling,
    /// and never shorter than the rail, as on the Mac. The window takes this,
    /// capped to its screen, and the page scrolls for whatever does not fit.
    /// Each part is measured on its own, never the window, so the number does
    /// not depend on the height it is asking for.
    /// </summary>
    public double WantedHeight()
    {
        if (_header == null || _page == null || _footer == null) return 200;
        var width = PanelWidth - RailWidth - BorderThickness.Left - BorderThickness.Right;
        double Tall(Control part)
        {
            part.Measure(new Size(width, double.PositiveInfinity));
            return part.DesiredSize.Height;
        }

        var rail = 150 + 48 * _model.RailOrder().Count;
        var parts = Tall(_header) + Math.Max(Tall(_page), 60) + Tall(_footer);
        return Math.Ceiling(Math.Max(parts, rail) + BorderThickness.Top + BorderThickness.Bottom);
    }

    public void Rebuild()
    {
        // An account that went away takes its page with it.
        if (Pane is Pane.Account { Key: var key } && Usage(key) == null) Pane = new Pane.Overview();

        Look.Tiles = PanelPrefs.Current.Tiles;

        var now = DateTimeOffset.UtcNow;
        var right = new Grid { RowDefinitions = new RowDefinitions(_flat ? "Auto,Auto,Auto" : "Auto,*,Auto") };

        var header = Header();
        header.Margin = new Thickness(14, 14, 14, 10);
        right.Children.Add(header);

        var page = Page(now);
        page.Margin = new Thickness(12, 0, 12, 4);
        if (_flat)
        {
            Grid.SetRow(page, 1);
            right.Children.Add(page);
        }
        else
        {
            // One scroll view for the panel's whole life: a new one on every
            // redraw (every half minute) would jump back to the top each time.
            if (_scroller == null)
            {
                _scroller = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                };
            }
            else if (_scroller.Parent is Panel old)
            {
                old.Children.Remove(_scroller);
            }
            _scroller.Content = page;
            Grid.SetRow(_scroller, 1);
            right.Children.Add(_scroller);
        }

        var footer = Footer();
        footer.Margin = new Thickness(14, 12);
        Grid.SetRow(footer, 2);
        right.Children.Add(footer);
        _header = header;
        _page = page;
        _footer = footer;

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions($"{RailWidth},*") };
        body.Children.Add(Rail());
        Grid.SetColumn(right, 1);
        body.Children.Add(right);
        Child = body;
        ApplyLook();
        Redrawn?.Invoke();
    }

    private ProviderUsage? Usage(string key) => _model.Snapshot?.Usages.FirstOrDefault(u => u.AccountKey == key);

    // ---- Rail ----

    private Control Rail()
    {
        var accounts = _model.RailOrder();

        var items = new StackPanel { Spacing = 6, Margin = new Thickness(0, 12) };
        items.Children.Add(RailButton(new Pane.Overview(), "Overview  Ctrl+0", Icons.Glyph(Icons.Grid, 15, Look.Primary)));
        items.Children.Add(new Border
        {
            Width = 18,
            Height = 1.5,
            CornerRadius = new CornerRadius(1),
            Background = Look.Ink(0.12),
            Margin = new Thickness(0, 3),
        });
        for (var i = 0; i < accounts.Count; i++)
        {
            var usage = accounts[i];
            var help = i < 9 ? $"{usage.DisplayName}  Ctrl+{i + 1}" : usage.DisplayName;
            items.Children.Add(RailButton(new Pane.Account(usage.AccountKey), help, RailRing(usage)));
        }

        var settings = RailButton(new Pane.Settings(), "Settings  Ctrl+,", Icons.Glyph(Icons.Gear, 16, Look.Primary));
        settings.VerticalAlignment = VerticalAlignment.Bottom;
        settings.Margin = new Thickness(0, 12, 0, 12);

        var rail = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        rail.Children.Add(items);
        Grid.SetRow(settings, 1);
        rail.Children.Add(settings);

        // A hairline between the rail and the page, short of both ends.
        var edge = new Border
        {
            Width = 0.75,
            Background = Look.Ink(0.08),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14),
        };
        Grid.SetRowSpan(edge, 2);
        rail.Children.Add(edge);
        return rail;
    }

    private Control RailButton(Pane target, string help, Control label)
    {
        var selected = Pane == target;
        label.Opacity = selected ? 1 : 0.62;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;

        // No highlight of its own at rest: the soft disc is the selection.
        var disc = new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(21),
            Background = selected ? Look.Ink(0.13) : Brushes.Transparent,
            BorderBrush = selected ? Look.Ink(0.14) : Brushes.Transparent,
            BorderThickness = new Thickness(0.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = label,
        };
        if (selected) Avalonia.Automation.AutomationProperties.SetItemStatus(disc, "Selected");
        return Bits.Clickable(disc, help, () => Show(target), over =>
        {
            if (!selected) disc.Background = over ? Look.Ink(0.07) : Brushes.Transparent;
            label.Opacity = selected || over ? 1 : 0.62;
        });
    }

    /// <summary>A subscription in the rail: its mark inside a ring of what is left.</summary>
    private Control RailRing(ProviderUsage usage)
    {
        var ok = usage.State == UsageState.Ok;
        var level = ok ? _model.Tightest(usage)?.RemainingPercent : null;
        var ring = new Ring(level, 32, 2.6, Icons.Glyph(ProviderArt.Logo(usage.Id), 14, Look.Primary))
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var box = new Panel { Width = 38, Height = 38 };
        box.Children.Add(ring);
        if (!ok)
        {
            box.Children.Add(Badge(Look.Solid(Look.Critical), Bits.Text("!", 9, FontWeight.Heavy, Brushes.White)));
        }
        else if (Account.LabelOf(usage.AccountName, usage.Slot) is { } label)
        {
            // The account's initials in a pill, as on the top bar and the Mac: "RZ", "W".
            var tile = new SolidColorBrush(Look.Dark ? Color.FromRgb(0x3A, 0x3C, 0x44) : Color.FromRgb(0xFF, 0xFF, 0xFF));
            box.Children.Add(Badge(tile, Bits.Text(Account.Initials(label), 7.5, FontWeight.Heavy, Look.Primary)));
        }
        return box;
    }

    /// <summary>A small pill on a ring's lower right; one character keeps it round.</summary>
    internal static Border Badge(IBrush fill, TextBlock mark)
    {
        // Big enough to read on a screen at its own scale, not only on a sharp
        // one; centred on the letters' ink, not on the line they sit on, which
        // keeps room below for descenders capitals do not have. Measured at the
        // screen's scale (USAGETOP_SNAPSHOT_BADGES=1): within a fifth of a unit.
        mark.FontSize = BadgeFont;
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        mark.VerticalAlignment = VerticalAlignment.Center;
        mark.Margin = new Thickness(0, BadgeLift, 0, 0);
        var wide = (mark.Text ?? "").Length > 1;
        return new Border
        {
            MinWidth = BadgeHeight,
            Height = BadgeHeight,
            Padding = new Thickness(wide ? 3.5 : 0, 0),
            CornerRadius = new CornerRadius(BadgeHeight / 2),
            Background = fill,
            BorderBrush = Look.Ink(0.14),
            BorderThickness = new Thickness(0.5),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = mark,
        };
    }

    /// <summary>
    /// 9 on 15 reads at a screen's own scale; 7.5 on 13 did not. The letters
    /// are lowered 2 to sit on their ink's centre: at 1x 1 to 3 round to the
    /// same pixel, within a quarter of one.
    /// </summary>
    private const double BadgeFont = 9;
    private const double BadgeHeight = 15;
    private const double BadgeLift = 2;

    // ---- Header and footer ----

    private Grid Header()
    {
        var (title, subtitle) = Pane switch
        {
            Pane.Settings => ("Settings", _model.UsesServer ? "Server" : "Connect to your server"),
            Pane.Account { Key: var key } when Usage(key) is { } usage => (
                usage.DisplayName,
                usage.State != UsageState.Ok ? "Needs attention" : $"Read {Format.Ago(usage.FetchedAt)}"),
            _ => ("Usage", OverviewSubtitle()),
        };

        var names = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(Bits.Text(title, 17, FontWeight.Bold, Look.Primary));
        if (subtitle != null) names.Children.Add(Bits.Text(subtitle, 11, FontWeight.Normal, Look.Secondary));

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        header.Children.Add(names);

        if (_model.IsSample)
        {
            var chip = Bits.Chip("Sample", Look.Warning);
            chip.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(chip, 1);
            header.Children.Add(chip);
        }

        if (Pane is not Pane.Settings)
        {
            var refresh = Bits.IconButton(
                Icons.Refresh,
                _model.UsesServer ? "Ask the server to poll every provider now  Ctrl+R" : "Ask every provider now  Ctrl+R",
                () => _ = _model.RefreshAsync(force: true));
            refresh.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(refresh, 2);
            header.Children.Add(refresh);
        }
        return header;
    }

    private string? OverviewSubtitle()
    {
        var usages = _model.Snapshot?.Usages ?? [];
        if (usages.Count == 0) return null;
        var broken = usages.Count(u => u.State != UsageState.Ok);
        var count = $"{usages.Count} subscription{(usages.Count == 1 ? "" : "s")}";
        return broken == 0 ? count : $"{count} · {broken} need{(broken == 1 ? "s" : "")} attention";
    }

    private Grid Footer()
    {
        var colour = StatusColour();
        var dot = new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = Look.Solid(colour),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Effect = new DropShadowEffect { BlurRadius = 5, OffsetX = 0, OffsetY = 0, Color = colour, Opacity = 0.8 },
        };

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        footer.Children.Add(dot);

        var status = Bits.Text(_model.StatusLine, 11, FontWeight.Normal, Look.Secondary);
        Grid.SetColumn(status, 1);
        footer.Children.Add(status);

        var ends = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(6, 0, 0, 0) };
        if (_model.ServerUrl is { } url)
        {
            // Accounts and sign-ins live on the server's page.
            ends.Children.Add(Bits.SoftButton("Dashboard ↗", "Open the server's page in the browser", () =>
            {
                _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri(url));
                DashboardOpened?.Invoke();
            }));
        }
        ends.Children.Add(Bits.IconButton(Icons.Power, "Quit usagetop  Ctrl+Q", () => QuitRequested?.Invoke(), size: 26));
        Grid.SetColumn(ends, 2);
        footer.Children.Add(ends);
        return footer;
    }

    private Color StatusColour()
    {
        if (_model.IsSample) return Look.Warning;
        if (_model.Problem != null) return Look.Critical;
        if (_model.Snapshot is not { } snapshot) return Look.InkColor;
        return DateTimeOffset.UtcNow - snapshot.FetchedAt > TimeSpan.FromMinutes(15) ? Look.Warning : Look.Fresh;
    }

    // ---- Pages ----

    private Control Page(DateTimeOffset now)
    {
        switch (Pane)
        {
            case Pane.Settings:
                if (_settings == null)
                {
                    _settings = new SettingsPage(_model, Rebuild, ApplyLook, Show);
                }
                else if (_settings.Parent is ContentControl holder)
                {
                    // Kept from the last drawing: out of the old scroll view first.
                    holder.Content = null;
                }
                else if (_settings.Parent is Panel old)
                {
                    old.Children.Remove(_settings);
                }
                return _settings;
            case Pane.Account { Key: var key } when Usage(key) is { } usage:
                return AccountPage.Build(_model, usage, now);
        }

        if ((_model.Snapshot?.Usages.Count ?? 0) == 0)
        {
            return _model.Problem is { } problem ? Notice(problem) : Onboarding();
        }
        return OverviewPage.Build(_model, now, key => Show(new Pane.Account(key)));
    }

    /// <summary>Before any account has been added.</summary>
    private Control Onboarding()
    {
        var stack = new StackPanel { Spacing = 14 };
        stack.Children.Add(new Ring(72, 54, 4.5) { HorizontalAlignment = HorizontalAlignment.Left });

        var words = new StackPanel { Spacing = 5 };
        words.Children.Add(Bits.Text("Connect to your server", 15, FontWeight.SemiBold, Look.Primary));
        words.Children.Add(Bits.Paragraph(
            "usagetop shows what is left of Claude, ChatGPT, Mistral, Kimi, Muse and Antigravity. " +
            "On Linux it reads from your usagetop server for now: give it the address and the token the other apps use.",
            12, Look.Secondary));
        stack.Children.Add(words);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(Bits.SoftButton("Connect", "Enter your server's address and token", () => Show(new Pane.Settings()), prominent: true));
        buttons.Children.Add(Bits.SoftButton("Try with sample data", "Show made-up usage", () => SampleRequested?.Invoke()));
        stack.Children.Add(buttons);
        return Bits.Card(stack, padding: 16);
    }

    /// <summary>The onboarding card asks for the made-up usage.</summary>
    public event Action? SampleRequested;

    /// <summary>The server's page was opened in the browser: the panel can step aside.</summary>
    public event Action? DashboardOpened;

    private static Control Notice(string problem)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var mark = Icons.Glyph(Icons.Warning, 13, Look.Solid(Look.Warning));
        mark.Margin = new Thickness(0, 1, 10, 0);
        mark.VerticalAlignment = VerticalAlignment.Top;
        row.Children.Add(mark);
        var words = Bits.Paragraph(problem, 12, Look.Secondary);
        Grid.SetColumn(words, 1);
        row.Children.Add(words);
        return Bits.Card(row, padding: 14);
    }
}

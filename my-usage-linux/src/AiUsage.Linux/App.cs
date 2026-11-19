using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using AiUsage.Ui;
using AiUsage.Views;

namespace AiUsage;

/// <summary>How this copy was asked to start.</summary>
public sealed record StartOptions(bool Hidden = false, bool Sample = false, string? StateFile = null);

/// <summary>
/// The app is a tray icon and one panel. It stays running with the panel put
/// away, answers the `usagetop` command line over its socket, and is the only
/// copy in the session.
/// </summary>
public sealed class App : Application
{
    public static StartOptions Options { get; set; } = new();

    private AppModel _model = AppModel.Empty();
    private PanelWindow? _panel;
    private TrayIcon? _tray;
    private float? _trayPercent = -1;
    private IDisposable? _listener;
    private DispatcherTimer? _clock;

    public override void Initialize()
    {
        Name = "usagetop";
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            if (!Ipc.TryListen(AnswerAsync, out _listener))
            {
                // Another copy got there first: it shows its panel, this one leaves.
                Ipc.Send("show");
                Dispatcher.UIThread.Post(() => desktop.Shutdown());
                base.OnFrameworkInitializationCompleted();
                return;
            }
            desktop.Exit += (_, _) => _listener?.Dispose();

            Look.Dark = ActualThemeVariant == ThemeVariant.Dark;
            ActualThemeVariantChanged += (_, _) =>
            {
                Look.Dark = ActualThemeVariant == ThemeVariant.Dark;
                // The window's shadow and backdrop are set once, so it is made again.
                var visible = _panel?.IsVisible ?? false;
                ReplacePanel();
                if (visible) Panel().Reveal();
            };

            Use(Options.Sample ? AppModel.Sample(Options.StateFile) : AppModel.Empty());

            // Countdowns and "3m ago" move on without a new reading.
            _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _clock.Tick += (_, _) => OnChanged();
            _clock.Start();

            if (!Options.Hidden) Panel().Reveal();
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Takes a model in place of the current one: at start, and for "Try with sample data".</summary>
    private void Use(AppModel model)
    {
        _model = model;
        _model.Changed += () => Dispatcher.UIThread.Post(OnChanged);
        ReplacePanel();
        UpdateTray();
    }

    private void OnChanged()
    {
        UpdateTray();
        if (_panel is { IsVisible: true }) _panel.View.Rebuild();
    }

    // ---- Panel ----

    private PanelWindow Panel()
    {
        if (_panel != null) return _panel;
        _panel = new PanelWindow(_model);
        _panel.QuitRequested += Quit;
        _panel.View.QuitRequested += Quit;
        _panel.View.SampleRequested += () =>
        {
            Use(AppModel.Sample());
            Panel().Reveal();
        };
        return _panel;
    }

    private void ReplacePanel()
    {
        _panel?.Close();
        _panel = null;
    }

    private void Quit()
    {
        _tray?.Dispose();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    // ---- Tray ----

    private void UpdateTray()
    {
        if (_tray == null)
        {
            var open = new NativeMenuItem("Open usagetop");
            open.Click += (_, _) => Panel().Reveal();
            var refresh = new NativeMenuItem("Refresh now");
            refresh.Click += (_, _) => _ = _model.RefreshAsync(force: true);
            var quit = new NativeMenuItem("Quit");
            quit.Click += (_, _) => Quit();

            _tray = new TrayIcon
            {
                Menu = new NativeMenu { Items = { open, refresh, new NativeMenuItemSeparator(), quit } },
                // Set before it is attached: a tray with no tooltip at all cannot be.
                ToolTipText = "usagetop",
                Icon = TrayArt.Icon(null),
                IsVisible = true,
            };
            // Where the desktop passes a click on, it toggles the panel; where a
            // click only ever opens the menu (GNOME's AppIndicator), "Open" does.
            _tray.Clicked += (_, _) => Panel().Toggle();
            TrayIcon.SetIcons(this, [_tray]);
        }

        var percent = Status.Headline(_model);
        var rounded = percent is { } p ? MathF.Round(p) : (float?)null;
        if (rounded != _trayPercent)
        {
            // Drawn again only when the figure it shows has moved.
            _trayPercent = rounded;
            _tray.Icon = TrayArt.Icon(percent);
        }
        _tray.ToolTipText = "usagetop — " + Status.Text(_model);
    }

    // ---- The command line, from another copy ----

    private async Task<string> AnswerAsync(string command) =>
        await Dispatcher.UIThread.InvokeAsync(() => Answer(command));

    private string Answer(string command)
    {
        switch (command)
        {
            case "ping":
                return "pong\n";
            case "show":
                Panel().Reveal();
                return "";
            case "hide":
                _panel?.Hide();
                return "";
            case "toggle":
                Panel().Toggle();
                return "";
            case "refresh":
                _ = _model.RefreshAsync(force: true);
                return "";
            case "quit":
                Dispatcher.UIThread.Post(Quit);
                return "";
            case "status":
                return Status.Text(_model) + "\n";
            case "status --json":
                return Status.AsJson(_model) + "\n";
            case "status --waybar":
                return Status.Waybar(_model) + "\n";
            default:
                return $"usagetop: unknown command '{command}'\n";
        }
    }
}

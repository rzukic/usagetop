using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AiUsage.Auth;
using AiUsage.Data;
using AiUsage.Tray;
using AiUsage.Ui;
using AiUsage.Views;

namespace AiUsage;

/// <summary>
/// The app is a tray icon first and a window second: closing a window leaves it
/// running, and only Quit ends the process.
/// </summary>
public partial class App : Application
{
    /// <summary>How often the tray refreshes on its own.</summary>
    private static readonly TimeSpan BackgroundPeriod = TimeSpan.FromMinutes(15);

    /// <summary>
    /// A hidden window the second copy can find, so a shortcut clicked while the
    /// app is already running opens the window instead of doing nothing.
    /// </summary>
    private const string MessageWindowTitle = "AiUsage.SingleInstance.7F3A";

    private const int ActivateMessage = 0x0400 + 1;

    private const int ShowMainArg = 1;
    private const int ShowFlyoutArg = 2;

    private Mutex? _singleInstance;
    private HwndSource? _messageWindow;
    private TrayIcon? _tray;
    private FlyoutWindow? _flyout;
    private MainWindow? _main;
    private DispatcherTimer? _timer;

    public static UsageRepository Repository { get; } =
        new(new Credentials(AppPaths.Credentials, new DpapiSealer()));

    protected override void OnStartup(StartupEventArgs e)
    {
        // `--snapshot <folder>`: every page of the panel to PNGs, then out. Before
        // the single-instance check, so it works beside a running copy.
        var snapshot = Array.FindIndex(e.Args, a => a.Equals("--snapshot", StringComparison.OrdinalIgnoreCase));
        if (snapshot >= 0 && snapshot + 1 < e.Args.Length)
        {
            try
            {
                PanelView.Snapshots(e.Args[snapshot + 1]);
            }
            catch (Exception exception)
            {
                Log.Write(exception.ToString());
            }
            Shutdown();
            return;
        }

        _singleInstance = new Mutex(true, @"Local\AiUsage.SingleInstance", out var first);
        if (!first)
        {
            // A second copy would mean two tray icons and two refresh timers, so it
            // hands its request to the copy already running and leaves.
            ForwardToRunningInstance(e.Args);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        CreateMessageWindow();

        DispatcherUnhandledException += OnUnhandledException;

        // Anyone already signed in to Claude Code, Codex or the Muse CLI gets a
        // working app on first launch instead of five empty cards.
        // With a server set there is nothing here to sign in for.
        if (!Repository.UsesServer
            && ProviderCatalog.All.All(info => Repository.Credentials.Token(info.Id) == null))
        {
            CliImport.Run(Repository.Credentials);
        }

        _tray = new TrayIcon();
        _tray.LeftClick += ToggleFlyout;
        _tray.OpenRequested += ShowMain;
        _tray.RefreshRequested += () => _ = RefreshAsync(force: true);
        _tray.QuitRequested += QuitApp;

        Repository.Changed += OnSnapshotChanged;
        // A tray setting changed in the panel shows at once, not at the next reading.
        PanelPrefs.Current.Changed += () => OnSnapshotChanged(Repository.Cached() ?? new Snapshot([], DateTimeOffset.UtcNow));

        // The cached reading is on screen immediately; the fetch replaces it.
        OnSnapshotChanged(Repository.Cached() ?? new Snapshot([], DateTimeOffset.UtcNow));
        _ = RefreshAsync();

        _timer = new DispatcherTimer { Interval = BackgroundPeriod };
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();

        // An installed copy keeps itself current; a build run from its folder does not.
        Updater.Current.Start();

        // `AiUsage.exe --open` starts with the window up and `--flyout` with the
        // popup, for a shortcut or the Run key; without either the app is only the
        // tray icon.
        if (e.Args.Contains("--open", StringComparer.OrdinalIgnoreCase)) ShowMain();
        else if (e.Args.Contains("--flyout", StringComparer.OrdinalIgnoreCase)) ToggleFlyout();
    }

    private void OnSnapshotChanged(Snapshot snapshot)
    {
        // The combined level, or, as Settings > Tray icon can ask, the one limit
        // closest to running out, named in the tooltip so the figure has an owner.
        var prefs = PanelPrefs.Current;
        var percent = CombinedUsage.Of(snapshot).Percent;
        var head = percent == null ? "usagetop" : $"usagetop — {Format.Percent(percent.Value)} left";
        if (prefs.TrayShowsClosest && PanelView.Closest(snapshot) is { Window.RemainingPercent: { } closest } limit)
        {
            percent = closest;
            head = $"{limit.Usage.DisplayName} {WindowName.Display(limit.Window.Label)}: {Format.Percent(closest)} left";
            if (head.Length > 40) head = $"{limit.Usage.DisplayName}: {Format.Percent(closest)} left";
        }

        // A provider that has stopped answering is otherwise invisible until a
        // window is opened: the ring just keeps showing the others' average.
        // A failure that will pass by itself -- a rate limit, Meta going quiet
        // while a window is idle -- is left out: the dot is for things only the
        // user can fix.
        var broken = snapshot.Usages
            .Where(u => u.State == UsageState.Error && !u.Transient)
            .Select(u => u.DisplayName)
            .ToList();

        // A server that cannot be reached leaves the ring on an old reading.
        var problem = Repository.ServerProblem;
        var tooltip = problem == null ? Tooltip(head, broken) : Tooltip(head, problem);

        _tray?.Update(percent, tooltip, attention: broken.Count > 0 || Repository.ServerNeedsUser, number: prefs.TrayShowsNumber);
    }

    // ---- Single instance --------------------------------------------------

    private void CreateMessageWindow()
    {
        var parameters = new HwndSourceParameters(MessageWindowTitle)
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        };

        _messageWindow = new HwndSource(parameters);
        _messageWindow.AddHook(OnMessage);
    }

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != ActivateMessage) return IntPtr.Zero;

        handled = true;
        if (wParam.ToInt32() == ShowFlyoutArg) ToggleFlyout();
        else ShowMain();

        return IntPtr.Zero;
    }

    private static void ForwardToRunningInstance(string[] args)
    {
        var handle = FindWindow(null, MessageWindowTitle);
        if (handle == IntPtr.Zero) return;

        var request = args.Contains("--flyout", StringComparer.OrdinalIgnoreCase)
            ? ShowFlyoutArg
            : ShowMainArg;

        PostMessage(handle, ActivateMessage, request, IntPtr.Zero);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int message, int wParam, IntPtr lParam);

    /// <summary>The shell cuts a tray tooltip off at 63 characters, so this stays under it.</summary>
    private static string Tooltip(string head, IReadOnlyList<string> broken)
    {
        if (broken.Count == 0) return head;

        var text = $"{head}\n{string.Join(", ", broken)}: needs attention";
        return text.Length <= 63 ? text : $"{head}\n{broken.Count} providers need attention";
    }

    private static string Tooltip(string head, string problem) => $"{head}\n{problem}";

    /// <summary><paramref name="force"/> is a person asking, not a timer or a window opening.</summary>
    public async Task RefreshAsync(bool force = false)
    {
        try
        {
            await Repository.RefreshAsync(force);
        }
        catch
        {
            // Every provider already reports its own failure; a throw here would
            // only be the tray losing its temper.
        }
    }

    private void ToggleFlyout()
    {
        _flyout ??= new FlyoutWindow();

        if (_flyout.IsVisible)
        {
            _flyout.Hide();
            return;
        }

        _flyout.ShowNearTray();
    }

    public void ShowMain()
    {
        _flyout?.Hide();

        if (_main == null)
        {
            _main = new MainWindow();
            _main.Closed += (_, _) => _main = null;
        }

        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    /// <summary>An account's page ("claude", "claude-2"); null opens the combined view.</summary>
    public void ShowDetail(string? id)
    {
        var detail = new DetailWindow(id) { Owner = _main };
        detail.Show();
    }

    public void QuitApp()
    {
        // A downloaded update goes in as the app leaves, unless the
        // "Restart to update" button already handed it over.
        Updater.Current.ApplyOnQuit();
        _timer?.Stop();
        _flyout?.Close();
        _main?.Close();
        _tray?.Dispose();
        Shutdown();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A tray app that dies on a stray exception is worse than one that logs it.
        Log.Write(e.Exception.ToString());
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _messageWindow?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}

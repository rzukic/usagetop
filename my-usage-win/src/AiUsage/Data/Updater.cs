using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows.Threading;
using AiUsage.Ui;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace AiUsage.Data;

/// <summary>
/// Keeps an installed copy current from the GitHub releases, with Velopack.
///
/// It looks a minute after start and then every six hours, or when asked in
/// Settings; never more often on its own, because GitHub allows an address 60
/// unauthenticated API calls an hour and each look costs one. A newer version is
/// downloaded in the background and then waits: "Restart to update" puts it in
/// at once, Quit puts it in as the app leaves, and failing both, Velopack itself
/// puts it in the next time the app starts (VelopackApp's auto-apply, on by
/// default). Nothing here ever pops up: a failed look is a line in Settings.
///
/// A copy run from a build folder is not installed and does nothing at all.
/// USAGETOP_UPDATE_SOURCE, a folder or an http(s) address holding the release
/// files, stands in for GitHub, to try an update end to end without publishing.
/// </summary>
public sealed class Updater
{
    public static Updater Current { get; } = new();

    public enum Phase { Off, Waiting, Checking, UpToDate, Downloading, Ready, Failed }

    private const string Repository = "https://github.com/rzukic/usagetop";
    private const string SourceVariable = "USAGETOP_UPDATE_SOURCE";
    private static readonly TimeSpan FirstLook = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Period = TimeSpan.FromHours(6);

    /// <summary>Null unless this is an installed copy; everything else keys off it.</summary>
    private readonly UpdateManager? _installed;
    private Dispatcher? _dispatcher;
    private DispatcherTimer? _timer;
    private bool _busy;
    private bool _again;
    private bool _handedOver;
    private bool _beta;
    private VelopackAsset? _ready;

    public Phase State { get; private set; }

    /// <summary>The version being downloaded, or downloaded and waiting.</summary>
    public string? Target { get; private set; }

    /// <summary>0-100 while downloading.</summary>
    public int Progress { get; private set; }

    /// <summary>When a look last got an answer.</summary>
    public DateTimeOffset? CheckedAt { get; private set; }

    /// <summary>What a failure was, in a line short enough for the status; why, for its tooltip.</summary>
    public string? Failure { get; private set; }
    public string? FailureDetail { get; private set; }

    /// <summary>The testing source in place of GitHub, if one is set.</summary>
    public string? SourceOverride { get; }

    /// <summary>Raised on the UI thread whenever any of the above changes.</summary>
    public event Action? Changed;

    /// <summary>The running version, as the release names it ("1.2.1-beta.1"), or the build's.</summary>
    public string Version { get; }

    private Updater()
    {
        SourceOverride = Environment.GetEnvironmentVariable(SourceVariable) is { } value && value.Trim().Length > 0
            ? value.Trim()
            : null;
        try
        {
            var manager = new UpdateManager(Source(beta: false));
            if (manager.IsInstalled) _installed = manager;
        }
        catch (Exception exception)
        {
            // No locator (VelopackApp did not run) or a source that will not parse:
            // either way there is nothing to update with.
            Log.Write($"Updater unavailable: {exception.Message}");
        }
        State = _installed == null ? Phase.Off : Phase.Waiting;
        Version = _installed?.CurrentVersion?.ToString() ?? BuildVersion();
    }

    public bool IsInstalled => _installed != null;

    /// <summary>A look or a download is under way.</summary>
    public bool Busy => _busy;

    /// <summary>Starts the schedule. Called once, on the UI thread, by the app at startup.</summary>
    public void Start()
    {
        if (_installed == null || _timer != null) return;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _beta = PanelPrefs.Current.BetaUpdates;

        // Downloaded by an earlier run and, for whatever reason, not yet put in
        // (Velopack declines to apply on the start that follows its own restart).
        if (Pending() is { } pending) MarkReady(pending);

        _timer = new DispatcherTimer { Interval = FirstLook };
        _timer.Tick += (_, _) =>
        {
            _timer.Interval = Period;
            _ = CheckAsync();
        };
        _timer.Start();

        // Turning beta versions on or off is a different question, so it is asked again.
        PanelPrefs.Current.Changed += () =>
        {
            if (PanelPrefs.Current.BetaUpdates == _beta) return;
            _beta = PanelPrefs.Current.BetaUpdates;
            _ = CheckAsync();
        };
    }

    /// <summary>
    /// One look, and a download if it finds something. A request while one is
    /// running is folded into a second look after it, so the beta switch is
    /// always answered with the setting it ended on.
    /// </summary>
    public async Task CheckAsync()
    {
        if (_installed == null || _handedOver) return;
        if (_busy)
        {
            _again = true;
            return;
        }
        _busy = true;
        Changed?.Invoke();
        try
        {
            do
            {
                _again = false;
                await CheckOnceAsync();
            } while (_again);
        }
        finally
        {
            _busy = false;
            Changed?.Invoke();
        }
    }

    private async Task CheckOnceAsync()
    {
        var beta = PanelPrefs.Current.BetaUpdates;

        // A beta fetched before the switch was turned off is not put in behind the user's back.
        if (!beta && _ready is { Version.IsPrerelease: true }) Discard(_ready);

        // With an update already waiting, its button stays up while the look runs.
        if (_ready == null) Set(Phase.Checking);
        UpdateManager manager;
        UpdateInfo? found;
        try
        {
            manager = new UpdateManager(Source(beta));
            found = await manager.CheckForUpdatesAsync();
        }
        catch (Exception exception)
        {
            Fail("Couldn't check for updates", exception);
            return;
        }
        CheckedAt = DateTimeOffset.UtcNow;
        Failure = FailureDetail = null;

        if (found == null || (_ready != null && _ready.Version >= found.TargetFullRelease.Version))
        {
            Set(_ready != null ? Phase.Ready : Phase.UpToDate);
            return;
        }

        Target = found.TargetFullRelease.Version.ToString();
        Progress = 0;
        Set(Phase.Downloading);
        try
        {
            await manager.DownloadUpdatesAsync(found, percent => _dispatcher?.BeginInvoke(() =>
            {
                if (State != Phase.Downloading || percent == Progress) return;
                Progress = percent;
                Changed?.Invoke();
            }));
        }
        catch (Exception exception)
        {
            Fail($"Couldn't download {Target}", exception);
            return;
        }
        MarkReady(Pending() ?? found.TargetFullRelease);
    }

    /// <summary>
    /// Hands the waiting update to Velopack's updater, which waits for this
    /// process to end, puts it in and starts the app again. True when the
    /// caller should now quit.
    /// </summary>
    public bool RestartToUpdate()
    {
        if (_installed == null || _ready == null || _handedOver) return false;
        try
        {
            _installed.WaitExitThenApplyUpdates(_ready, silent: false, restart: true);
            _handedOver = true;
            return true;
        }
        catch (Exception exception)
        {
            Fail("Couldn't start the update", exception);
            return false;
        }
    }

    /// <summary>
    /// Quit with an update waiting: it goes in as the app leaves, quietly and
    /// without starting it again, so the next start is already the new version.
    /// Velopack would otherwise do it at that start, with a progress window.
    /// </summary>
    public void ApplyOnQuit()
    {
        if (_installed == null || _ready == null || _handedOver) return;
        try
        {
            _installed.WaitExitThenApplyUpdates(_ready, silent: true, restart: false);
            _handedOver = true;
        }
        catch (Exception exception)
        {
            Log.Write($"Update on quit failed: {exception.Message}");
        }
    }

    // ---- Pieces -----------------------------------------------------------

    /// <summary>
    /// GitHub, or the testing source. GithubSource reads the ten newest
    /// releases (one API call), drops pre-releases unless asked for them, and
    /// offers the highest version among the rest: a beta user gets a release as
    /// soon as it is newer than their beta. The prerelease flag means nothing to
    /// a plain folder or address, which offer whatever their feed lists.
    /// </summary>
    private IUpdateSource Source(bool beta)
    {
        if (SourceOverride == null) return new GithubSource(Repository, accessToken: null, prerelease: beta);
        return Uri.TryCreate(SourceOverride, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? new SimpleWebSource(uri)
            : new SimpleFileSource(new DirectoryInfo(SourceOverride));
    }

    /// <summary>A downloaded package newer than the running version, as Velopack sees it.</summary>
    private VelopackAsset? Pending()
    {
        try
        {
            return _installed?.UpdatePendingRestart;
        }
        catch (Exception exception)
        {
            Log.Write($"Update: could not read the packages folder: {exception.Message}");
            return null;
        }
    }

    private void MarkReady(VelopackAsset asset)
    {
        _ready = asset;
        Target = asset.Version.ToString();
        Progress = 100;
        Set(Phase.Ready);
    }

    /// <summary>Deletes a downloaded package, so Velopack's start-up does not put it in.</summary>
    private void Discard(VelopackAsset asset)
    {
        try
        {
            if (VelopackLocator.Current.PackagesDir is { } folder)
            {
                File.Delete(Path.Combine(folder, asset.FileName));
            }
        }
        catch (Exception exception)
        {
            Log.Write($"Update: could not remove {asset.FileName}: {exception.Message}");
        }
        _ready = null;
        Target = null;
    }

    private void Fail(string failure, Exception exception)
    {
        Log.Write($"{failure}: {exception.GetType().Name}: {exception.Message}");
        Failure = failure;
        FailureDetail = Explain(exception);
        // An update already downloaded is still good; a failed look does not hide it.
        if (_ready != null)
        {
            Target = _ready.Version.ToString();
            Set(Phase.Ready);
        }
        else
        {
            Set(Phase.Failed);
        }
    }

    private static string Explain(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } => "No releases were found where the app looks for them.",
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } =>
            "GitHub's limit on update checks was reached; the next one is in a few hours.",
        HttpRequestException { StatusCode: null } or TaskCanceledException => "No connection to the update server.",
        HttpRequestException { StatusCode: { } status } => $"The update server answered {(int)status}.",
        _ => exception.Message,
    };

    private void Set(Phase state)
    {
        State = state;
        Changed?.Invoke();
    }

    /// <summary>
    /// A build that is not installed: its informational version, which the SDK
    /// suffixes with the commit ("1.1.0+3f96fb9..."); shortened to seven characters.
    /// </summary>
    private static string BuildVersion()
    {
        var info = typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info)) return typeof(Updater).Assembly.GetName().Version?.ToString(3) ?? "dev";
        var plus = info.IndexOf('+');
        return plus < 0 ? info : info[..Math.Min(info.Length, plus + 8)];
    }
}

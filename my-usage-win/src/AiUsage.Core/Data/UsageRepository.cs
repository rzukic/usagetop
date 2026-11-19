using AiUsage.Auth;

namespace AiUsage.Data;

/// <summary>
/// The one place a reading comes from. Every screen asks this rather than calling a
/// provider itself, so the flyout, the main window and the tray icon always show the
/// same numbers.
/// </summary>
public sealed class UsageRepository
{
    /// <summary>How old a reading may be before opening a screen refetches it.</summary>
    public static readonly TimeSpan AutoRefreshWindow = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly Credentials _credentials;
    private readonly UsageHistory _history;
    private readonly UsageFetcher _fetcher = new();
    private Snapshot? _cached;
    private Task<Snapshot>? _inFlight;
    private DateTimeOffset _syncedAt = DateTimeOffset.MinValue;

    /// <summary>The server the history was last synced with, and up to when.</summary>
    private string? _serverSynced;
    private DateTimeOffset? _serverSyncedTo;

    /// <summary>The hidden limits the server last sent, and which server sent them.</summary>
    private IReadOnlyList<string>? _serverHidden;
    private string? _serverHiddenFrom;

    /// <summary>Set for a repository that only shows a state it was given.</summary>
    private readonly bool _fixed;

    public UsageRepository(Credentials credentials)
    {
        _credentials = credentials;
        _history = new UsageHistory();
        _cached = LoadCached();
    }

    /// <summary>
    /// A repository that shows <paramref name="state"/> and nothing else: no file
    /// is read or written and nothing is fetched. `--snapshot --state` draws the
    /// sample data in the README's pictures with it.
    /// </summary>
    public UsageRepository(ServerState state, Credentials credentials)
    {
        _credentials = credentials;
        _history = new UsageHistory(state);
        _cached = state.Snapshot;
        _syncedAt = state.ServerTime;
        _fixed = true;
    }

    public Credentials Credentials => _credentials;

    public UsageHistory History => _history;

    /// <summary>Raised on the thread that finished the refresh, after the cache is written.</summary>
    public event Action<Snapshot>? Changed;

    /// <summary>True while the readings come from a server rather than from the providers.</summary>
    public bool UsesServer => _credentials.ServerUrl() != null;

    /// <summary>
    /// Why the last refresh could not reach the server, or null. The reading on
    /// screen is then the last one that did arrive, and the screen should say so.
    /// </summary>
    public string? ServerProblem { get; private set; }

    /// <summary>True when <see cref="ServerProblem"/> is one only the user can fix.</summary>
    public bool ServerNeedsUser { get; private set; }

    /// <summary>
    /// The limits the server keeps hidden for every app, as it last sent them;
    /// null without a server, before its first answer, or from a server too old
    /// to keep the list.
    /// </summary>
    public IReadOnlyList<string>? ServerHidden
    {
        get
        {
            var url = _credentials.ServerUrl();
            lock (_gate) return url != null && url == _serverHiddenFrom ? _serverHidden : null;
        }
    }

    /// <summary>True while the server's list is the one shown and changed, not this PC's.</summary>
    public bool HidesOnServer => ServerHidden != null;

    /// <summary>
    /// The limits left out of the overview: the server's list while there is
    /// one, so a limit hidden on one device is hidden on all; otherwise
    /// <paramref name="local"/>, this PC's own. The two are never merged.
    /// </summary>
    public IReadOnlyList<string> HiddenLimits(IReadOnlyList<string> local) => ServerHidden ?? local;

    /// <summary>
    /// Hides a limit on the server, or shows it again. The change shows at once;
    /// the list the server answers with then replaces it. If the server cannot
    /// be told, its last list stays and <see cref="ServerProblem"/> says why.
    /// Only for <see cref="HidesOnServer"/>; this PC's own list is the panel's.
    /// </summary>
    public async Task SetHiddenOnServerAsync(string key, bool hide, CancellationToken cancellation = default)
    {
        if (_credentials.ServerUrl() is not { } url || ServerHidden is not { } before) return;

        KeepHidden(url, Limits.With(before, key, hide));
        try
        {
            KeepHidden(url, await ServerClient.SetHiddenAsync(url, _credentials.ServerToken(), key, hide, cancellation));
        }
        catch (ServerException problem)
        {
            KeepHidden(url, before);
            ServerProblem = problem.Message;
            ServerNeedsUser = problem.NeedsUser;
        }
    }

    /// <summary>What a server sent as its hidden limits; null is a server that keeps none.</summary>
    internal void KeepHidden(string url, IReadOnlyList<string>? hidden)
    {
        lock (_gate)
        {
            _serverHidden = hidden;
            _serverHiddenFrom = url;
        }
    }

    /// <summary>
    /// Fetches every provider in parallel, then caches the result. Concurrent callers
    /// share one fetch: a screen opening while the timer fires should not double the
    /// requests, and both callers want the same reading.
    ///
    /// <paramref name="force"/> is the refresh button: with a server set, it has
    /// the server ask the providers now instead of handing over what it has.
    /// </summary>
    public Task<Snapshot> RefreshAsync(bool force = false, CancellationToken cancellation = default)
    {
        if (_fixed) return Task.FromResult(_cached!);

        lock (_gate)
        {
            if (_inFlight is { } running) return running;

            var task = RefreshCoreAsync(force, cancellation);
            _inFlight = task;
            return task;
        }
    }

    private async Task<Snapshot> RefreshCoreAsync(bool force, CancellationToken cancellation)
    {
        try
        {
            var snapshot = _credentials.ServerUrl() is { } url
                ? await FromServerAsync(url, force, cancellation)
                : await FromProvidersAsync(cancellation);

            lock (_gate) _syncedAt = DateTimeOffset.UtcNow;
            Changed?.Invoke(snapshot);
            return snapshot;
        }
        finally
        {
            lock (_gate) _inFlight = null;
        }
    }

    private async Task<Snapshot> FromProvidersAsync(CancellationToken cancellation)
    {
        ServerProblem = null;
        ServerNeedsUser = false;

        // A token lent by a CLI is renewed by that CLI, so its file is read
        // again before anything is asked of the provider.
        CliImport.Sync(_credentials);

        var snapshot = await _fetcher.FetchAsync(_credentials, Cached(), cancellation);
        Store(snapshot);
        _history.Record(snapshot);
        return snapshot;
    }

    /// <summary>
    /// The server's reading in place of our own. Nothing here touches a provider
    /// or a token: while a server is set, it is the one holder of every session.
    /// </summary>
    private async Task<Snapshot> FromServerAsync(string url, bool force, CancellationToken cancellation)
    {
        try
        {
            // History already synced with this server only needs topping up. The
            // overlap covers a reading the server stored just after it answered.
            var since = _serverSynced == url ? _serverSyncedTo - TimeSpan.FromMinutes(10) : null;

            var state = await ServerClient.FetchAsync(url, _credentials.ServerToken(), since, force, cancellation);

            Store(state.Snapshot);
            _history.Merge(state, DateTimeOffset.UtcNow);
            _serverSynced = url;
            _serverSyncedTo = state.ServerTime;
            KeepHidden(url, state.HiddenLimits);
            ServerProblem = null;
            ServerNeedsUser = false;
            return state.Snapshot;
        }
        catch (ServerException problem)
        {
            ServerProblem = problem.Message;
            ServerNeedsUser = problem.NeedsUser;
            return Cached() ?? new Snapshot([], DateTimeOffset.UtcNow);
        }
    }

    public Snapshot? Cached()
    {
        lock (_gate) return _cached;
    }

    /// <summary>
    /// True when the cached reading is younger than <paramref name="maxAge"/>. A screen
    /// opening asks this before fetching: a reading from the last minute is already
    /// what the user came to see, and refetching it would only make the numbers
    /// flicker as they walk between screens.
    /// </summary>
    public bool IsFresh(TimeSpan? maxAge = null)
    {
        DateTimeOffset? at;
        lock (_gate)
        {
            // The server's reading can be minutes old the moment it arrives, so
            // what counts is when we last asked, not when it was taken.
            at = _cached == null ? null : _cached.FetchedAt > _syncedAt ? _cached.FetchedAt : _syncedAt;
        }
        if (at == null) return false;
        return DateTimeOffset.UtcNow - at.Value < (maxAge ?? AutoRefreshWindow);
    }

    private void Store(Snapshot snapshot)
    {
        lock (_gate) _cached = snapshot;

        try
        {
            AtomicFile.WriteAllText(AppPaths.Snapshot, SnapshotCodec.Encode(snapshot));
        }
        catch
        {
            // A cache that cannot be written is not worth failing a refresh over.
        }
    }

    private static Snapshot? LoadCached()
    {
        try
        {
            if (!File.Exists(AppPaths.Snapshot)) return null;
            return SnapshotCodec.Decode(File.ReadAllText(AppPaths.Snapshot));
        }
        catch
        {
            return null;
        }
    }
}

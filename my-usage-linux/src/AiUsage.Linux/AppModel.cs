using System.Reflection;
using AiUsage.Data;
using AiUsage.Ui;

namespace AiUsage;

/// <summary>
/// What the panel and the tray draw from: the reading, its history and the
/// panel's preferences, with the orderings and forecasts worked out as the
/// Mac and Windows apps work them out.
/// </summary>
public sealed class AppModel
{
    private Credentials? _credentials;
    private UsageRepository? _real;

    private AppModel(UsageRepository? repository, bool sample)
    {
        Show(repository, sample);
    }

    /// <summary>
    /// What is on screen: the user's own (from a server, once one is set) or
    /// sample data. Null before the user's own has been looked at.
    /// </summary>
    public UsageRepository? Repository { get; private set; }

    /// <summary>Made-up usage is on screen, and everything says so.</summary>
    public bool IsSample { get; private set; }

    /// <summary>A new reading, a preference, or the clock moving on.</summary>
    public event Action? Changed;

    public void RaiseChanged() => Changed?.Invoke();

    public Snapshot? Snapshot => Repository?.Cached();

    public string? Problem => Repository?.ServerProblem;

    public bool UsesServer => !IsSample && (Repository?.UsesServer ?? false);

    /// <summary>The user's own files are in play: not a picture of the first start.</summary>
    public bool HasOwnData => _real != null || IsSample;

    /// <summary>The server's address while one is set, for the dashboard button.</summary>
    public string? ServerUrl => IsSample || _real == null ? null : Credentials.ServerUrl();

    /// <summary>
    /// The user's own credentials file, whatever is on screen: Settings saves
    /// the server into it even while sample data is showing.
    /// </summary>
    public Credentials Credentials => _credentials ??= new Credentials(AppPaths.Credentials, SecretKey.Sealer());

    /// <summary>Nothing to show and nothing read or written: for pictures of the first start.</summary>
    public static AppModel Blank() => new(null, sample: false);

    /// <summary>The user's own: the server's reading, once a server is set.</summary>
    public static AppModel Load()
    {
        var model = new AppModel(null, sample: false);
        model.ShowOwn();
        return model;
    }

    /// <summary>
    /// A server's /api/state answer in place of anything of the user's, moved up
    /// to now: the README's made-up usage unless <paramref name="stateFile"/> names another.
    /// </summary>
    public static AppModel Sample(string? stateFile = null) => new(SampleRepository(stateFile), sample: true);

    /// <summary>Back to the user's own after sample data, or after the server changed.</summary>
    public void ShowOwn()
    {
        _real ??= new UsageRepository(Credentials);
        if (IsSample) PanelPrefs.Reload();
        Show(_real, sample: false);
    }

    /// <summary>
    /// Reads the credentials file again. The core reads it once and keeps it,
    /// so a server set from the command line reaches a running app only this way.
    /// </summary>
    public void Reload()
    {
        _credentials = null;
        _real = null;
        if (!IsSample) ShowOwn();
    }

    /// <summary>"Try with sample data".</summary>
    public void ShowSample() => Show(SampleRepository(null), sample: true);

    private void Show(UsageRepository? repository, bool sample)
    {
        if (Repository != null) Repository.Changed -= OnRepositoryChanged;
        Repository = repository;
        IsSample = sample;
        if (repository != null) repository.Changed += OnRepositoryChanged;
        RaiseChanged();
    }

    private void OnRepositoryChanged(Snapshot snapshot) => RaiseChanged();

    private static UsageRepository SampleRepository(string? stateFile)
    {
        string json;
        if (stateFile != null)
        {
            json = File.ReadAllText(stateFile);
        }
        else
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("sample-state.json")
                ?? throw new InvalidOperationException("The sample data is missing from this build");
            using var reader = new StreamReader(stream);
            json = reader.ReadToEnd();
        }

        var state = ServerState.Parse(json) ?? throw new FormatException("The state has no reading");
        // Pictures should not depend on this machine's saved look.
        PanelPrefs.UseDefaults();
        return new UsageRepository(
            state.ShiftedBy(DateTimeOffset.UtcNow - state.ServerTime),
            // Nothing is stored: the key only has to exist.
            Credentials.InMemory(new AesSealer(new byte[32])));
    }

    /// <summary>
    /// A new reading. Only from a server for now: this app does not yet ask the
    /// providers itself, so without one there is nothing to fetch.
    /// </summary>
    public Task RefreshAsync(bool force = false) =>
        Repository == null || IsSample || !Repository.UsesServer ? Task.CompletedTask : Repository.RefreshAsync(force);

    // ---- What is shown, and in what order ----

    public IReadOnlyList<string> Hidden => Repository?.HiddenLimits(PanelPrefs.Current.HiddenLimits) ?? [];

    public IReadOnlyList<UsageWindow> Shown(ProviderUsage usage) => Limits.Shown(usage, Hidden);

    public UsageWindow? Tightest(ProviderUsage usage) => Limits.Tightest(usage, Hidden);

    /// <summary>The rail: the order the user set, then by provider.</summary>
    public List<ProviderUsage> RailOrder()
    {
        var order = PanelPrefs.Current.Order;
        return (Snapshot?.Usages ?? [])
            .OrderBy(u => order.IndexOf(u.AccountKey) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(u => (int)u.Id)
            .ThenBy(u => u.AccountKey, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The overview: working accounts first, the one about to run out at the top; or the rail's order.</summary>
    public List<ProviderUsage> OverviewOrder()
    {
        if (!PanelPrefs.Current.OverviewByRisk) return RailOrder();
        return (Snapshot?.Usages ?? [])
            .OrderBy(u => u.State == UsageState.Ok ? 0 : 1)
            .ThenBy(u => Tightest(u)?.RemainingPercent ?? 101f)
            .ThenBy(u => u.DisplayName, StringComparer.Ordinal)
            .ToList();
    }

    // ---- The combined figure and where it is heading ----

    public CombinedUsage.Combined? Combined => Snapshot is { } snapshot ? CombinedUsage.Of(snapshot) : null;

    public IReadOnlyList<UsagePoint> CombinedWeek(DateTimeOffset now) =>
        Repository?.History.Combined().Where(p => p.At >= now.AddDays(-7)).ToList() ?? [];

    public IReadOnlyList<UsagePoint> Series(ProviderUsage usage, UsageWindow window, TimeSpan span) =>
        Repository?.History.Series(usage.AccountKey, window.Label, span) ?? [];

    public Projection.Track? Track(ProviderUsage usage, UsageWindow window, DateTimeOffset now) =>
        Projection.For(window, Series(usage, window, Projection.UsualBasis), now);

    /// <summary>How fast everything together is being spent, and when the figure next lifts.</summary>
    public sealed record Outlook(double? Recent, double? Usual, string? NextName, DateTimeOffset? NextReset)
    {
        public bool Draining => (Recent ?? 0) > Projection.Idle || (Usual ?? 0) > Projection.Idle;

        /// <summary>"Burning 1.0%/h · usually 0.4%/h", as every app words it.</summary>
        public string Rates
        {
            get
            {
                if (!Draining) return "Not draining lately";
                var parts = new List<string>();
                if (Recent is { } recent)
                {
                    parts.Add(recent > Projection.Idle
                        ? $"Burning {Format.Rate(recent)}"
                        : $"Idle for the last {Format.Number(Projection.RecentBasis.TotalHours)}h");
                }
                if (Usual is { } usual) parts.Add($"{(parts.Count == 0 ? "Usually" : "usually")} {Format.Rate(usual)}");
                return string.Join(" · ", parts);
            }
        }
    }

    /// <summary>
    /// Each subscription's long windows averaged, then the subscriptions: the
    /// same weighting as the combined figure itself. A window that is already
    /// empty is not being spent, whatever its past says. Null with nothing measured.
    /// </summary>
    public Outlook? CombinedOutlook(DateTimeOffset now)
    {
        if (Snapshot is not { } snapshot) return null;

        var parts = snapshot.Usages
            .Where(u => u.State == UsageState.Ok)
            .Select(u => CombinedUsage.LongWindows(u)
                .Select(w => (Usage: u, Window: w, Track: Track(u, w, now)))
                .Where(t => t.Track != null)
                .ToList())
            .Where(p => p.Count > 0)
            .ToList();
        var all = parts.SelectMany(p => p).ToList();
        if (!all.Any(t => t.Track!.Measured)) return null;

        double? Rate(Func<Projection.Track, double?> pick)
        {
            if (!all.Any(t => pick(t.Track!) != null)) return null;
            return parts.Average(p => p.Average(t => t.Track!.Percent > 0 ? pick(t.Track!) ?? 0 : 0));
        }

        var next = all
            .Where(t => t.Window.ResetAt > now)
            .OrderBy(t => t.Window.ResetAt)
            .FirstOrDefault();
        return new Outlook(
            Rate(t => t.Recent),
            Rate(t => t.Usual),
            next.Usage == null ? null : $"{next.Usage.DisplayName} {WindowName.Display(next.Window.Label)}",
            next.Usage == null ? null : next.Window.ResetAt);
    }

    public string StatusLine => IsSample ? "Sample data" : Format.Status(Snapshot, Problem, UsesServer);
}

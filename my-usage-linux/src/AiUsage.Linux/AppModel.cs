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
    private AppModel(UsageRepository? repository, bool sample)
    {
        Repository = repository;
        IsSample = sample;
        if (repository != null) repository.Changed += _ => RaiseChanged();
    }

    /// <summary>Null until accounts can be added here: there is nothing to read from yet.</summary>
    public UsageRepository? Repository { get; }

    /// <summary>Made-up usage is on screen, and everything says so.</summary>
    public bool IsSample { get; }

    /// <summary>A new reading, a preference, or the clock moving on.</summary>
    public event Action? Changed;

    public void RaiseChanged() => Changed?.Invoke();

    public Snapshot? Snapshot => Repository?.Cached();

    public string? Problem => Repository?.ServerProblem;

    public bool UsesServer => Repository?.UsesServer ?? false;

    /// <summary>The app with nothing to show: no account has been added.</summary>
    public static AppModel Empty() => new(null, sample: false);

    /// <summary>
    /// A server's /api/state answer in place of anything of the user's, moved up
    /// to now: the README's made-up usage unless <paramref name="stateFile"/> names another.
    /// </summary>
    public static AppModel Sample(string? stateFile = null)
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
        var repository = new UsageRepository(
            state.ShiftedBy(DateTimeOffset.UtcNow - state.ServerTime),
            // Nothing is stored: the key only has to exist.
            Credentials.InMemory(new AesSealer(new byte[32])));
        PanelPrefs.UseDefaults();
        return new AppModel(repository, sample: true);
    }

    public Task RefreshAsync(bool force = false) =>
        Repository == null || IsSample ? Task.CompletedTask : Repository.RefreshAsync(force);

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

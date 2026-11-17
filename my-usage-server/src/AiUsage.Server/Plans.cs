using AiUsage.Data;

namespace AiUsage.Server;

/// <summary>
/// One cycle of a plan window that has finished: how much of it went, and when
/// it ran. Only the long windows count -- a five-hour window is a rate limit,
/// not a plan, and it refills four times a day.
/// </summary>
public sealed record PlanPeriod(
    string Account,
    string Window,
    DateTimeOffset Start,
    DateTimeOffset End,
    float Used,
    float Lowest,
    int Readings,
    bool Complete);

/// <summary>An account's plan, what it costs a month, and every cycle that has finished.</summary>
public sealed record PlanAccount(
    string Key,
    string Provider,
    string DisplayName,
    string? Plan,
    double? Monthly,
    bool Assumed,
    IReadOnlyList<PlanPeriod> Periods);

/// <summary>
/// What the plans have actually been worth. Every reading carries the reset it
/// belongs to, so a cycle is the run of readings that share one: the level only
/// falls inside a cycle, and the lowest it reached is what the cycle used. A
/// cycle that has not reset yet is left out -- it is still being spent.
/// </summary>
public static class Plans
{
    /// <summary>Below a day a window is a rate limit rather than a plan.</summary>
    private static readonly TimeSpan Shortest = TimeSpan.FromDays(1);

    /// <summary>
    /// Some providers give the reset as "seconds from now", so the same reset
    /// comes back a few seconds apart. A move this large is a new cycle.
    /// </summary>
    private static readonly TimeSpan Drift = TimeSpan.FromMinutes(10);

    /// <summary>A cycle whose last reading is this far from its reset was not watched to the end.</summary>
    private static readonly TimeSpan Blind = TimeSpan.FromHours(2);

    /// <summary>
    /// What a plan is usually sold for, in euro a month. Only a starting point:
    /// a price set on the page wins, and a plan not listed here has none until
    /// one is set.
    /// </summary>
    private static readonly Dictionary<string, double> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude|pro"] = 20,
        ["claude|max"] = 100,
        ["chatgpt|plus"] = 20,
        ["chatgpt|pro"] = 200,
        ["mistral|education"] = 0,
    };

    /// <summary>How long one cycle of a window lasts, from its label. Null when the label does not say.</summary>
    public static TimeSpan? CycleOf(string label) => Projection.PeriodOf(label);

    /// <summary>
    /// Every account that has a plan window, with the cycles that have finished.
    /// The accounts come from the history as well as from the latest snapshot: an
    /// account that has been removed, or whose provider is failing right now, has
    /// still spent what it spent.
    /// </summary>
    public static IReadOnlyList<PlanAccount> Of(Store store, Snapshot? snapshot, DateTimeOffset now)
    {
        var periods = new List<PlanPeriod>();
        foreach (var (account, window) in store.SeriesKeys(DateTimeOffset.MinValue))
        {
            if (CycleOf(window) is not { } cycle || cycle < Shortest) continue;
            periods.AddRange(PeriodsOf(
                store.Series(account, window, DateTimeOffset.MinValue), account, window, cycle, now));
        }

        var current = (snapshot?.Usages ?? [])
            .Where(usage => usage.Windows.Any(window => CycleOf(window.Label) is { } cycle && cycle >= Shortest))
            .ToDictionary(usage => usage.AccountKey);

        var keys = current.Keys
            .Union(periods.Select(period => period.Account))
            .OrderBy(key => Account.Parse(key) is { } parsed ? (int)parsed.Provider : int.MaxValue)
            .ThenBy(key => key, StringComparer.Ordinal);

        var accounts = new List<PlanAccount>();
        foreach (var key in keys)
        {
            current.TryGetValue(key, out var usage);
            var parsed = Account.Parse(key);
            var provider = usage is { } known
                ? ProviderCatalog.Of(known.Id).Key
                : parsed is { } account ? ProviderCatalog.Of(account.Provider).Key : key;
            var plan = usage?.Plan ?? store.Plan(key);

            var set = store.Price(key);
            var assumed = set == null && plan != null && Known.ContainsKey($"{provider}|{plan}");

            accounts.Add(new PlanAccount(
                key,
                provider,
                usage?.DisplayName ?? NameOf(parsed),
                plan,
                set ?? (assumed ? Known[$"{provider}|{plan}"] : null),
                assumed,
                periods.Where(period => period.Account == key)
                    .OrderByDescending(period => period.End)
                    .ToList()));
        }

        return accounts;
    }

    /// <summary>"Claude", or "Claude · 2" for a second account the snapshot no longer names.</summary>
    private static string NameOf((ProviderId Provider, string Slot)? account) =>
        account is { } parsed
            ? Account.DisplayNameOf(parsed.Provider, parsed.Slot.Length == 0 ? null : parsed.Slot)
            : "Unknown";

    /// <summary>
    /// The finished cycles of one series. Readings are grouped by the reset they
    /// carry rather than by a jump in the level: a refill is what a reset means,
    /// and the reset is what the provider actually said.
    /// </summary>
    private static IEnumerable<PlanPeriod> PeriodsOf(
        IReadOnlyList<Reading> readings,
        string account,
        string window,
        TimeSpan cycle,
        DateTimeOffset now)
    {
        var cycles = new List<(DateTimeOffset Reset, List<Reading> Readings)>();
        var group = new List<Reading>();
        DateTimeOffset? reset = null;

        foreach (var reading in readings)
        {
            if (reading.ResetAt is not { } at) continue;
            if (reset is { } current && (at - current).Duration() > Drift)
            {
                cycles.Add((current, group));
                group = [];
            }

            reset = at;
            group.Add(reading);
        }

        if (reset is { } last) cycles.Add((last, group));

        foreach (var (end, run) in cycles)
        {
            if (end > now || run.Count == 0) continue;

            var lowest = run.Min(reading => reading.Percent);
            yield return new PlanPeriod(
                account,
                window,
                StartOf(window, end, cycle),
                end,
                Math.Clamp(100f - lowest, 0f, 100f),
                lowest,
                run.Count,
                end - run[^1].At <= Blind);
        }
    }

    /// <summary>
    /// Where a cycle began. A monthly window resets on the same day of the month,
    /// so its start is a calendar month back rather than thirty days.
    /// </summary>
    private static DateTimeOffset StartOf(string window, DateTimeOffset end, TimeSpan cycle) =>
        window.Trim().Equals("Monthly", StringComparison.OrdinalIgnoreCase)
            ? end.AddMonths(-1)
            : end - cycle;
}

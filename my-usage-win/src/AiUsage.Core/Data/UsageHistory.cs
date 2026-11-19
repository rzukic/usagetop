using System.Text.Json;

namespace AiUsage.Data;

/// <summary>
/// A rolling record of how each window's remaining level has moved, so the detail
/// screen can draw a drawdown. Plain JSON: it holds percentages, no secrets.
///
/// Two kinds of series live here. Per-window ones keep five weeks, so a monthly
/// allowance can show a whole cycle. The combined one keeps a quarter. Both thin
/// out with age, because every reading kept at full resolution would be thousands
/// of points no chart can show.
/// </summary>
public sealed class UsageHistory
{
    /// <summary>Five weeks: one full cycle of the longest window anyone sells, plus slack.</summary>
    public static readonly TimeSpan Retain = TimeSpan.FromDays(35);

    public static readonly TimeSpan RetainCombined = TimeSpan.FromDays(90);

    /// <summary>
    /// Shorter than the 15-minute refresh period, so a window that never moves
    /// still gains a reading on every refresh instead of waiting for a change that
    /// may never come.
    /// </summary>
    public static readonly TimeSpan SampleGap = TimeSpan.FromMinutes(5);

    /// <summary>A ceiling on any one series: 2016 + 552 + 240 at the tiers below.</summary>
    public const int MaxPoints = 3000;

    public const string CombinedKey = "combined";

    private readonly object _gate = new();
    private Dictionary<string, List<UsagePoint>> _series;

    /// <summary>False for a history that only shows a state it was given.</summary>
    private readonly bool _stored = true;

    public UsageHistory()
    {
        _series = Load();
    }

    /// <summary>The history in <paramref name="state"/> and nothing else: the file is neither read nor written.</summary>
    public UsageHistory(ServerState state)
    {
        _stored = false;
        _series = state.Series.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.Ordinal);
        _series[CombinedKey] = state.Combined.ToList();
    }

    /// <summary>Appends the snapshot's readings, skipping ones that add no information.</summary>
    public void Record(Snapshot snapshot)
    {
        var now = snapshot.FetchedAt;

        lock (_gate)
        {
            foreach (var usage in snapshot.Usages.Where(u => u.State == UsageState.Ok))
            {
                foreach (var window in usage.Windows)
                {
                    if (window.RemainingPercent == null) continue;
                    var key = Key(usage.AccountKey, window.Label);
                    _series[key] = Append(Read(key), now, window.RemainingPercent.Value, Retain);
                }
            }

            var combined = CombinedUsage.Of(snapshot).Percent;
            if (combined != null)
            {
                _series[CombinedKey] = AppendCombined(Read(CombinedKey), now, combined.Value);
            }

            Save();
        }
    }

    /// <summary>
    /// Takes in what a server sent. Its readings replace ours from where they
    /// start, and whatever we hold from before that is kept: a server set up
    /// last week should not cost the month the app had already drawn.
    /// </summary>
    public void Merge(ServerState state, DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var (key, points) in state.Series)
            {
                _series[key] = Splice(Read(key), points, now, Retain);
            }
            _series[CombinedKey] = Splice(Read(CombinedKey), state.Combined, now, RetainCombined);

            Save();
        }
    }

    /// <summary>
    /// <paramref name="existing"/> up to where <paramref name="incoming"/> begins,
    /// then <paramref name="incoming"/>. The same rule serves a first call that
    /// brings weeks and a later one that brings the last few minutes.
    /// </summary>
    public static List<UsagePoint> Splice(
        IReadOnlyList<UsagePoint> existing,
        IReadOnlyList<UsagePoint> incoming,
        DateTimeOffset now,
        TimeSpan retain)
    {
        if (incoming.Count == 0) return existing.ToList();

        var from = incoming[0].At;
        return Compact(existing.Where(p => p.At < from).Concat(incoming).ToList(), now, retain);
    }

    /// <summary>One window's readings, optionally only the most recent <paramref name="span"/>.</summary>
    public IReadOnlyList<UsagePoint> Series(ProviderId id, string label, TimeSpan? span = null) =>
        Series(ProviderCatalog.Of(id).Key, label, span);

    /// <summary>One account's window, by its key: "claude" or "claude-2".</summary>
    public IReadOnlyList<UsagePoint> Series(string accountKey, string label, TimeSpan? span = null)
    {
        List<UsagePoint> series;
        lock (_gate) series = Read(Key(accountKey, label));

        if (span is not { } within) return series;
        var since = DateTimeOffset.UtcNow - within;
        return series.Where(p => p.At >= since).ToList();
    }

    /// <summary>
    /// How far back one window's own chart looks. A window that refills within a
    /// week shows a week; five weeks of a five-hour sawtooth would be a solid
    /// block. Anything slower, or with no period in its label, shows everything
    /// kept.
    /// </summary>
    public static TimeSpan ChartSpan(UsageWindow window) =>
        Projection.PeriodOf(window.Label) is { } period && period <= TimeSpan.FromDays(7)
            ? TimeSpan.FromDays(7)
            : Retain;

    /// <summary>Every subscription's long windows averaged, up to a quarter back.</summary>
    public IReadOnlyList<UsagePoint> Combined()
    {
        lock (_gate) return Read(CombinedKey);
    }

    /// <summary>The next combined series after a reading.</summary>
    public static List<UsagePoint> AppendCombined(
        IReadOnlyList<UsagePoint> existing,
        DateTimeOffset now,
        float percent) => Append(existing, now, percent, RetainCombined);

    /// <summary>
    /// The next series after a reading. A reading that adds nothing is skipped
    /// rather than appended: appending it and then thinning would drop the previous
    /// point for being too close to the new one, so a run of quick refreshes would
    /// keep the series at one point forever and the chart would never leave
    /// "Collecting readings…".
    /// </summary>
    public static List<UsagePoint> Append(
        IReadOnlyList<UsagePoint> existing,
        DateTimeOffset now,
        float percent,
        TimeSpan retain)
    {
        var last = existing.Count > 0 ? existing[^1] : (UsagePoint?)null;

        // A flat reading a minute after the last one is noise; a flat reading an
        // hour later is the shape of the drawdown.
        var worthKeeping = last == null
            || Math.Abs(last.Value.Percent - percent) >= 0.5f
            || now - last.Value.At >= SampleGap;
        if (!worthKeeping) return existing.ToList();

        return Compact(existing.Append(new UsagePoint(now, percent)).ToList(), now, retain);
    }

    /// <summary>
    /// Thins the series out with age: every reading for the last week, hourly for
    /// the month before it, six-hourly for the rest. Walking backwards means the
    /// newest reading always survives and the gaps only ever grow towards the past.
    /// </summary>
    public static List<UsagePoint> Compact(
        IReadOnlyList<UsagePoint> points,
        DateTimeOffset now,
        TimeSpan? retain = null)
    {
        var horizon = retain ?? RetainCombined;
        var kept = new List<UsagePoint>(points.Count);
        UsagePoint? newer = null;

        for (var i = points.Count - 1; i >= 0; i--)
        {
            var point = points[i];
            var age = now - point.At;
            if (age > horizon) continue;
            if (newer == null || newer.Value.At - point.At >= GapFor(age))
            {
                kept.Add(point);
                newer = point;
            }
        }

        kept.Reverse();
        return kept.TakeLast(MaxPoints).ToList();
    }

    private static TimeSpan GapFor(TimeSpan age) => age < TimeSpan.FromDays(7)
        ? SampleGap
        : age < TimeSpan.FromDays(30)
            ? TimeSpan.FromHours(1)
            : TimeSpan.FromHours(6);

    private List<UsagePoint> Read(string key) =>
        _series.TryGetValue(key, out var points) ? points : [];

    private static string Key(string accountKey, string label) => $"{accountKey}|{label}";

    // ---- Storage ----------------------------------------------------------

    private sealed record StoredPoint(long T, float P);

    private static Dictionary<string, List<UsagePoint>> Load()
    {
        try
        {
            if (!File.Exists(AppPaths.History)) return new(StringComparer.Ordinal);
            var json = File.ReadAllText(AppPaths.History);
            var raw = JsonSerializer.Deserialize<Dictionary<string, List<StoredPoint>>>(json);
            if (raw == null) return new(StringComparer.Ordinal);

            return raw.ToDictionary(
                pair => pair.Key,
                pair => pair.Value
                    .Select(p => new UsagePoint(DateTimeOffset.FromUnixTimeMilliseconds(p.T), p.P))
                    .ToList(),
                StringComparer.Ordinal);
        }
        catch
        {
            return new(StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        if (!_stored) return;
        try
        {
            var raw = _series.ToDictionary(
                pair => pair.Key,
                pair => pair.Value
                    .Select(p => new StoredPoint(p.At.ToUnixTimeMilliseconds(), p.Percent))
                    .ToList());
            AtomicFile.WriteAllText(AppPaths.History, JsonSerializer.Serialize(raw));
        }
        catch
        {
            // A history that cannot be written is not worth failing a refresh over.
        }
    }
}

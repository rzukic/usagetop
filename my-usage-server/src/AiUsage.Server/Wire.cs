using System.Text.Json.Nodes;
using AiUsage.Data;

namespace AiUsage.Server;

/// <summary>
/// The JSON the apps are sent. It is deliberately the shape the Android app
/// already stores: a snapshot exactly as its own cache encodes one, and every
/// series as `[[millis, percent], ...]` under the key `provider|window`. A client
/// can put a response straight where its own fetch would have put it.
/// </summary>
public static class Wire
{
    public static JsonObject Snapshot(Snapshot snapshot)
    {
        var usages = new JsonArray();
        foreach (var usage in snapshot.Usages)
        {
            var windows = new JsonArray();
            foreach (var window in usage.Windows)
            {
                windows.Add(new JsonObject
                {
                    ["label"] = window.Label,
                    ["remainingPercent"] = window.RemainingPercent is { } percent ? Math.Round(percent, 2) : null,
                    ["resetAt"] = Instant(window.ResetAt),
                    ["detail"] = window.Detail,
                    ["countsTowardCombined"] = window.CountsTowardCombined,
                });
            }

            usages.Add(new JsonObject
            {
                ["id"] = ProviderCatalog.Of(usage.Id).Key,
                // "claude" or "claude-2"; series are filed under this, not under id.
                ["account"] = usage.AccountKey,
                ["accountName"] = usage.AccountName,
                ["state"] = State(usage.State),
                ["message"] = usage.Message,
                ["plan"] = usage.Plan,
                ["fetchedAt"] = Instant(usage.FetchedAt),
                ["stale"] = usage.Stale,
                ["transient"] = usage.Transient,
                ["windows"] = windows,
            });
        }

        var combined = CombinedUsage.Of(snapshot);
        return new JsonObject
        {
            ["fetchedAt"] = Instant(snapshot.FetchedAt),
            ["combinedPercent"] = combined.Percent is { } level ? Math.Round(level, 2) : null,
            ["combinedProviders"] = combined.Providers,
            ["usages"] = usages,
        };
    }

    /// <summary>
    /// The plan history: every account with a plan window, what it costs a month,
    /// and the cycles that have finished. A cycle is a start, an end and how much
    /// of it went; the page does the adding up, so a month can be read either way.
    /// </summary>
    public static JsonObject Plans(IReadOnlyList<PlanAccount> accounts)
    {
        var list = new JsonArray();
        foreach (var account in accounts)
        {
            var periods = new JsonArray();
            foreach (var period in account.Periods)
            {
                periods.Add(new JsonObject
                {
                    ["window"] = period.Window,
                    ["start"] = Instant(period.Start),
                    ["end"] = Instant(period.End),
                    ["used"] = Math.Round(period.Used, 1),
                    ["lowest"] = Math.Round(period.Lowest, 1),
                    ["readings"] = period.Readings,
                    ["complete"] = period.Complete,
                });
            }

            list.Add(new JsonObject
            {
                ["key"] = account.Key,
                ["provider"] = account.Provider,
                ["displayName"] = account.DisplayName,
                ["plan"] = account.Plan,
                ["monthly"] = account.Monthly,
                ["assumed"] = account.Assumed,
                ["periods"] = periods,
            });
        }

        return new JsonObject { ["accounts"] = list };
    }

    public static JsonArray Series(IEnumerable<UsagePoint> points)
    {
        var array = new JsonArray();
        foreach (var point in points)
        {
            array.Add(new JsonArray(point.At.ToUnixTimeMilliseconds(), Math.Round(point.Percent, 2)));
        }
        return array;
    }

    /// <summary>The Android enum's own spelling, so its codec reads the value as is.</summary>
    private static string State(UsageState state) => state switch
    {
        UsageState.Ok => "OK",
        UsageState.NotConfigured => "NOT_CONFIGURED",
        _ => "ERROR",
    };

    private static string? Instant(DateTimeOffset? at) =>
        at?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    /// <summary>
    /// Cuts a series down to roughly <paramref name="max"/> points for a chart.
    /// Each slice of time keeps its lowest and its highest reading rather than an
    /// average: a drawdown is a sawtooth, and averaging turns the vertical edge
    /// of a refill into a ramp and shaves the bottom off every trough.
    /// </summary>
    public static IReadOnlyList<UsagePoint> Downsample(IReadOnlyList<UsagePoint> points, int max)
    {
        if (max < 4 || points.Count <= max) return points;

        var first = points[0].At;
        var span = (points[^1].At - first).TotalMilliseconds;
        if (span <= 0) return [points[^1]];

        var buckets = max / 2;
        var kept = new List<UsagePoint>(max);

        var start = 0;
        for (var bucket = 0; bucket < buckets && start < points.Count; bucket++)
        {
            var edge = first.AddMilliseconds(span * (bucket + 1) / buckets);
            var end = start;
            while (end < points.Count && (points[end].At < edge || bucket == buckets - 1)) end++;
            if (end == start) continue;

            var low = start;
            var high = start;
            for (var i = start + 1; i < end; i++)
            {
                if (points[i].Percent < points[low].Percent) low = i;
                if (points[i].Percent > points[high].Percent) high = i;
            }

            // In the order they happened, so the line still runs left to right.
            kept.Add(points[Math.Min(low, high)]);
            if (low != high) kept.Add(points[Math.Max(low, high)]);

            start = end;
        }

        // The newest reading is the one the chart ends on; it is never dropped.
        if (kept[^1].At != points[^1].At) kept.Add(points[^1]);
        return kept;
    }
}

using AiUsage.Data;

namespace AiUsage.Server;

/// <summary>
/// When each provider is asked next. Every provider keeps its own clock, because
/// their rate limits are their own: one that answers 429 should be left alone
/// without slowing down the four that are fine.
///
/// The rule of thumb is to ask as often as the answer changes. A window nobody is
/// spending is the same number every time, so it is asked at the slow pace; one
/// that moved recently is asked at the fast one; and a provider that is refusing
/// is asked less and less.
/// </summary>
public sealed class Schedule
{
    /// <summary>How long after a window last moved it still counts as in use.</summary>
    public static readonly TimeSpan ActiveFor = TimeSpan.FromMinutes(15);

    /// <summary>The slowest any provider is asked, however badly it is behaving.</summary>
    public static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(15);

    /// <summary>A move smaller than this is rounding, not somebody working.</summary>
    private const float Moved = 0.5f;

    /// <summary>A window is asked this soon after it refills, so the chart shows the edge.</summary>
    private static readonly TimeSpan AfterReset = TimeSpan.FromSeconds(30);

    private sealed class Entry
    {
        public DateTimeOffset Due;
        public DateTimeOffset? MovedAt;
        public int Failures;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeSpan _idle;
    private readonly TimeSpan _active;

    /// <summary>
    /// Every account is due at once on start: the first thing a restarted server
    /// owes its clients is a reading. <paramref name="accounts"/> are their keys;
    /// by default the first account of every provider.
    /// </summary>
    public Schedule(TimeSpan idle, TimeSpan active, DateTimeOffset start, IEnumerable<string>? accounts = null)
    {
        _idle = idle;
        _active = active < idle ? active : idle;

        foreach (var key in accounts ?? ProviderCatalog.All.Select(info => info.Key))
        {
            _entries[key] = new Entry { Due = start };
        }
    }

    /// <summary>The accounts whose turn has come, by key.</summary>
    public IReadOnlyList<string> Due(DateTimeOffset now)
    {
        lock (_gate)
        {
            return _entries.Where(pair => pair.Value.Due <= now).Select(pair => pair.Key).ToList();
        }
    }

    /// <summary>When the loop should next wake.</summary>
    public DateTimeOffset NextDue()
    {
        lock (_gate) return _entries.Count == 0 ? DateTimeOffset.MaxValue : _entries.Values.Min(entry => entry.Due);
    }

    public void Hurry(ProviderId id, DateTimeOffset now) => Hurry(ProviderCatalog.Of(id).Key, now);

    /// <summary>Makes an account due now: its credentials changed, it is new, or someone asked.</summary>
    public void Hurry(string account, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(account, out var entry)) _entries[account] = entry = new Entry();
            entry.Due = now;
            entry.Failures = 0;
        }
    }

    /// <summary>Stops asking about an account that has been removed.</summary>
    public void Forget(string account)
    {
        lock (_gate) _entries.Remove(account);
    }

    /// <summary>
    /// Books a provider's next turn from what it just answered.
    /// <paramref name="before"/> is its reading from the previous round.
    /// </summary>
    public void Report(ProviderUsage? before, ProviderUsage after, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(after.AccountKey, out var entry)) _entries[after.AccountKey] = entry = new Entry();
            entry.Due = now + DelayAfter(entry, before, after, now);
        }
    }

    private TimeSpan DelayAfter(Entry entry, ProviderUsage? before, ProviderUsage after, DateTimeOffset now)
    {
        // A stand-in reading means this fetch failed for a passing reason: a rate
        // limit, a dropped connection. Each one in a row doubles the wait.
        var failedForNow = after.Stale || (after.State == UsageState.Error && after.Transient);
        if (failedForNow)
        {
            entry.Failures = Math.Min(entry.Failures + 1, 8);
            return Capped(_idle * Math.Pow(2, entry.Failures - 1));
        }

        entry.Failures = 0;

        // Nothing to learn by asking quickly: an unconfigured provider answers
        // without touching the network, and a rejected token stays rejected until
        // someone signs in, which hurries the provider itself.
        if (after.State != UsageState.Ok) return after.State == UsageState.Error ? Ceiling : _idle;

        if (HasMoved(before, after)) entry.MovedAt = now;
        var inUse = entry.MovedAt is { } moved && now - moved < ActiveFor;
        var delay = inUse ? _active : _idle;

        // If a window refills before the next turn, take the turn just after the
        // refill instead: the jump back to full is the one reading worth catching
        // on time.
        var nextReset = after.Windows
            .Where(window => window.ResetAt is { } reset && reset > now)
            .Select(window => window.ResetAt!.Value)
            .DefaultIfEmpty(DateTimeOffset.MaxValue)
            .Min();
        var untilReset = nextReset == DateTimeOffset.MaxValue ? TimeSpan.MaxValue : nextReset - now + AfterReset;

        // Never sooner than a minute, whatever a reset time claims: a provider
        // that kept reporting "resets in ten seconds" would otherwise be asked
        // every forty.
        var chosen = untilReset < delay ? untilReset : delay;
        return chosen < Floor ? Floor : chosen;
    }

    private static readonly TimeSpan Floor = TimeSpan.FromMinutes(1);

    private static bool HasMoved(ProviderUsage? before, ProviderUsage after)
    {
        if (before is not { State: UsageState.Ok }) return false;

        foreach (var window in after.Windows)
        {
            if (window.RemainingPercent is not { } now) continue;
            var earlier = before.Windows.FirstOrDefault(w => w.Label == window.Label)?.RemainingPercent;
            if (earlier is { } then && Math.Abs(then - now) >= Moved) return true;
        }
        return false;
    }

    private static TimeSpan Capped(TimeSpan delay) => delay > Ceiling ? Ceiling : delay;
}

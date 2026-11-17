using AiUsage.Data;

namespace AiUsage.Server;

/// <summary>
/// The loop that keeps the readings coming. It is the only thing in the process
/// that talks to a provider, and so the only thing that ever renews a token:
/// with one holder per session there is nobody to race.
/// </summary>
public sealed class Poller(
    Credentials credentials,
    Store store,
    Schedule schedule,
    Notifier notifier,
    ServerOptions options,
    ILogger<Poller> logger) : BackgroundService
{
    /// <summary>The longest the loop sleeps without looking at the schedule again.</summary>
    private static readonly TimeSpan MaxSleep = TimeSpan.FromMinutes(1);

    /// <summary>How close together two forced refreshes may be.</summary>
    private static readonly TimeSpan ForcedGap = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private Snapshot? _current = store.LoadSnapshot();
    private DateTimeOffset _lastForced = DateTimeOffset.MinValue;

    /// <summary>The newest snapshot, or null before the first poll of a new database.</summary>
    public Snapshot? Current => Volatile.Read(ref _current);

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        logger.LogInformation(
            "Polling every {Idle}, or every {Active} while a window is in use",
            options.Poll,
            options.ActivePoll);

        while (!stopping.IsCancellationRequested)
        {
            try
            {
                var due = schedule.Due(DateTimeOffset.UtcNow);
                if (due.Count > 0) await PollAsync(due, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // One bad round must not end the service; the next one may be fine.
                logger.LogError(exception, "A polling round failed");
            }

            var sleep = schedule.NextDue() - DateTimeOffset.UtcNow;
            if (sleep > MaxSleep) sleep = MaxSleep;
            if (sleep < TimeSpan.FromSeconds(1)) sleep = TimeSpan.FromSeconds(1);

            try
            {
                // Sleeps until the next turn, or until something hurries a provider.
                await _wake.WaitAsync(sleep, stopping);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Makes one account due now and wakes the loop: its credentials just changed.</summary>
    public void Hurry(string account)
    {
        schedule.Hurry(account, DateTimeOffset.UtcNow);
        Wake();
    }

    /// <summary>
    /// Drops an account that has been removed, from the schedule and from the
    /// snapshot clients are sent. Its stored history stays, filed under its key.
    /// </summary>
    public async Task ForgetAsync(string account, CancellationToken cancellation)
    {
        schedule.Forget(account);
        await _turn.WaitAsync(cancellation);
        try
        {
            if (Current is { } current)
            {
                var kept = current.Usages.Where(u => u.AccountKey != account).ToList();
                Volatile.Write(ref _current, current with { Usages = kept });
            }
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <summary>
    /// Asks every provider now and returns the result. Calls that arrive too close
    /// together are answered with the reading already in hand: a client tapping
    /// refresh must not be able to turn the server into something that hammers.
    /// </summary>
    public async Task<Snapshot?> RefreshNowAsync(CancellationToken cancellation)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastForced < ForcedGap) return Current;
        _lastForced = now;

        await PollAsync(credentials.Accounts().Select(account => account.Key).ToList(), cancellation);
        return Current;
    }

    private async Task PollAsync(IReadOnlyList<string> keys, CancellationToken cancellation)
    {
        await _turn.WaitAsync(cancellation);
        try
        {
            // A key the schedule still holds for an account that has since been
            // removed would otherwise stay due, and wake the loop every second.
            var accounts = credentials.Accounts().Select(account => account.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in keys.Where(key => !accounts.Contains(key))) schedule.Forget(gone);
            var asked = keys.Where(accounts.Contains).ToList();
            if (asked.Count == 0) return;

            var before = Current;
            var fetched = await new UsageFetcher().FetchAsync(credentials, before, cancellation, asked);

            var after = Merge(before, fetched, accounts);
            store.Record(after, asked);
            Volatile.Write(ref _current, after);

            var earlier = new Dictionary<string, ProviderUsage>(StringComparer.Ordinal);
            foreach (var usage in before?.Usages ?? []) earlier[usage.AccountKey] = usage;
            foreach (var usage in fetched.Usages)
            {
                schedule.Report(earlier.GetValueOrDefault(usage.AccountKey), usage, after.FetchedAt);
                logger.LogDebug("{Account}: {State} {Message}", usage.AccountKey, usage.State, usage.Message);
            }

            foreach (var alert in Alerts.Between(before, after, options.AlertBelow, after.FetchedAt))
            {
                await notifier.SendAsync(alert, cancellation);
            }
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <summary>
    /// A round only asks the accounts whose turn it is, so its answers are laid
    /// over the previous snapshot. Accounts come out in catalogue order, each
    /// provider's first account before the ones added to it, whichever were asked.
    /// One no longer in <paramref name="accounts"/> has been removed and is left out.
    /// </summary>
    internal static Snapshot Merge(Snapshot? before, Snapshot fetched, IReadOnlySet<string>? accounts = null)
    {
        var merged = new Dictionary<string, ProviderUsage>(StringComparer.Ordinal);
        foreach (var usage in before?.Usages ?? []) merged[usage.AccountKey] = usage;
        foreach (var usage in fetched.Usages) merged[usage.AccountKey] = usage;

        var ordered = merged.Values
            .Where(usage => accounts == null || accounts.Contains(usage.AccountKey))
            .OrderBy(usage => (int)usage.Id)
            .ThenBy(usage => usage.Slot.Length == 0 ? 0 : 1)
            .ThenBy(usage => int.TryParse(usage.Slot, out var number) ? number : int.MaxValue)
            .ThenBy(usage => usage.Slot, StringComparer.Ordinal)
            .ToList();

        return new Snapshot(ordered, fetched.FetchedAt);
    }

    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already woken and not yet looked; one wake is enough.
        }
    }
}

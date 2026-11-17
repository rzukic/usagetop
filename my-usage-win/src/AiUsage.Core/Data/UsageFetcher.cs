using AiUsage.Auth;
using AiUsage.Providers;

namespace AiUsage.Data;

/// <summary>
/// One round of asking every provider where it stands. The Windows app and the
/// server both go through here, so a rule about what counts as a reading -- when
/// an old one stands in, when a token is renewed -- is written once.
/// </summary>
public sealed class UsageFetcher
{
    public static readonly IReadOnlyList<IUsageProvider> DefaultProviders =
    [
        new ClaudeProvider(),
        new ChatGptProvider(),
        new MistralProvider(),
        new KimiProvider(),
        new MuseProvider(),
    ];

    private readonly IReadOnlyList<IUsageProvider> _providers;
    private readonly Func<ProviderId, Credentials, CancellationToken, Task<bool>> _renewRejected;

    public UsageFetcher(
        IReadOnlyList<IUsageProvider>? providers = null,
        Func<ProviderId, Credentials, CancellationToken, Task<bool>>? renewRejected = null)
    {
        _providers = providers ?? DefaultProviders;
        _renewRejected = renewRejected ?? TokenRefresher.RenewRejectedAsync;
    }

    /// <summary>
    /// Fetches every account of every provider in parallel. <paramref name="previous"/>
    /// is the last snapshot, whose readings stand in for any account that fails for
    /// a reason that says nothing about its quota. <paramref name="only"/> limits
    /// the round to those account keys.
    /// </summary>
    public async Task<Snapshot> FetchAsync(
        Credentials credentials,
        Snapshot? previous,
        CancellationToken cancellation = default,
        IReadOnlyCollection<string>? only = null)
    {
        var earlier = new Dictionary<string, ProviderUsage>(StringComparer.Ordinal);
        foreach (var usage in previous?.Usages ?? []) earlier[usage.AccountKey] = usage;
        var now = DateTimeOffset.UtcNow;

        var rounds = credentials.Accounts()
            .Where(account => only == null || only.Contains(account.Key))
            .Select(account => (account, provider: _providers.FirstOrDefault(p => p.Id == account.Provider)))
            .Where(pair => pair.provider != null)
            .Select(pair => FetchAccountAsync(pair.provider!, pair.account, credentials.For(pair.account.Slot), cancellation));

        var fetched = await Task.WhenAll(rounds);

        var usages = fetched
            .Select(usage => KeepLastGood(usage, earlier.GetValueOrDefault(usage.AccountKey)))
            // Meta reports nothing at all while the five-hour window is idle or
            // spent, so a reading kept from before its reset has to be rolled
            // forward by hand.
            .Select(usage => MuseProvider.RollOver(usage, now))
            .ToList();

        return new Snapshot(usages, now);
    }

    /// <summary>One account: its own tokens renewed if due, then asked, then labelled as its own.</summary>
    private async Task<ProviderUsage> FetchAccountAsync(
        IUsageProvider provider,
        Account account,
        Credentials credentials,
        CancellationToken cancellation)
    {
        try
        {
            await TokenRefresher.EnsureFreshAsync(credentials, provider.Id, cancellation);
        }
        catch
        {
            // A renewal that fails leaves the old token; the fetch says what that means.
        }

        var usage = await FetchOneAsync(provider, credentials, cancellation);
        return usage with { Slot = account.Slot, AccountName = account.Name };
    }

    private async Task<ProviderUsage> FetchOneAsync(
        IUsageProvider provider,
        Credentials credentials,
        CancellationToken cancellation)
    {
        var usage = await GuardedAsync(provider, credentials, cancellation);
        if (!usage.Rejected) return usage;

        // A refused token is renewed and tried once more before the card is
        // allowed to say "Token rejected": most refusals are only an expiry
        // nobody had on file.
        bool renewed;
        try
        {
            renewed = await _renewRejected(provider.Id, credentials, cancellation);
        }
        catch
        {
            renewed = false;
        }

        if (renewed) return await GuardedAsync(provider, credentials, cancellation);

        // A token lent by a CLI is that CLI's to renew, so say where to go.
        return credentials.Borrowed(provider.Id)
            ? usage with { Message = "CLI token expired — sign in" }
            : usage;
    }

    private static async Task<ProviderUsage> GuardedAsync(
        IUsageProvider provider,
        Credentials credentials,
        CancellationToken cancellation)
    {
        try
        {
            return await provider.FetchAsync(credentials, cancellation);
        }
        catch (Exception exception)
        {
            return ProviderHelpers.NetworkError(provider.Id, exception);
        }
    }

    /// <summary>
    /// A rate limit or a dropped connection says nothing about the quota itself, so
    /// the last reading that got through stands in for it instead of blanking the
    /// card. A rejected token or a moved endpoint is a real problem and is shown.
    ///
    /// The reason travels with the numbers: a reading that has been standing in for
    /// hours is a different thing from one that missed a single fetch, and the screen
    /// has to be able to say which.
    ///
    /// Only the numbers are old. The account's name is today's: an account renamed
    /// while its provider was not answering must not go on showing the old name.
    /// </summary>
    internal static ProviderUsage KeepLastGood(ProviderUsage usage, ProviderUsage? previous)
    {
        if (usage.State != UsageState.Error || !usage.Transient) return usage;

        var last = previous is { State: UsageState.Ok, Windows.Count: > 0 } ? previous : null;
        return last == null
            ? usage
            : last with { Stale = true, Message = usage.Message, AccountName = usage.AccountName };
    }
}

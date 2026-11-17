using System.Collections.Concurrent;
using AiUsage.Data;

namespace AiUsage.Auth;

/// <summary>
/// Keeps OAuth access tokens usable. Access tokens live for hours, so without this
/// the app would go dark shortly after signing in.
/// </summary>
public static class TokenRefresher
{
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long to wait before renewing a rejected token a second time. Without a
    /// floor, a provider that keeps answering 401 for some other reason would have
    /// a refresh token spent on it at every poll.
    /// </summary>
    private static readonly TimeSpan RejectedCooldown = TimeSpan.FromMinutes(30);

    /// <summary>Keyed by account, so one account's refusal does not hold back another's renewal.</summary>
    private static readonly ConcurrentDictionary<string, DateTimeOffset> LastForced = new();

    /// <summary>Renews every token that is about to expire, before anything is fetched.</summary>
    public static async Task EnsureFreshAsync(Credentials credentials, CancellationToken cancellation = default)
    {
        foreach (var config in OAuthConfig.All)
        {
            await EnsureFreshAsync(credentials, config.ProviderId, cancellation);
        }
    }

    /// <summary>Renews one provider's token in the account <paramref name="credentials"/> speaks for, if it is about to expire.</summary>
    public static async Task EnsureFreshAsync(
        Credentials credentials,
        ProviderId id,
        CancellationToken cancellation = default)
    {
        if (OAuthConfig.ForProvider(id) is { } config)
        {
            if (credentials.RefreshToken(id) == null) return;

            var expiresAt = credentials.ExpiresAt(id);
            var stale = credentials.Token(id) == null
                || (expiresAt > 0L
                    && expiresAt - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < Skew.TotalMilliseconds);

            if (stale) await OAuthFlow.RefreshAsync(config, credentials, cancellation);
        }
    }

    /// <summary>
    /// Renews a token the provider has just refused. The expiry on file is not
    /// always known -- a token copied in by hand carries none -- and a provider can
    /// revoke one early, so "not expired yet" does not mean "still accepted".
    /// Returns true when there is a new token worth trying.
    /// </summary>
    public static async Task<bool> RenewRejectedAsync(
        ProviderId id,
        Credentials credentials,
        CancellationToken cancellation = default)
    {
        if (OAuthConfig.ForProvider(id) is not { } config) return false;
        if (credentials.RefreshToken(id) == null) return false;

        var now = DateTimeOffset.UtcNow;
        var key = Account.KeyOf(id, credentials.Slot);
        if (LastForced.TryGetValue(key, out var last) && now - last < RejectedCooldown) return false;
        LastForced[key] = now;

        return await OAuthFlow.RefreshAsync(config, credentials, cancellation);
    }

    /// <summary>Lets a test, or a fresh sign-in, start with no cooldown pending.</summary>
    public static void ForgetRejections() => LastForced.Clear();
}

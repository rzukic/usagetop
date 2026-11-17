using System.Collections.Concurrent;
using AiUsage.Auth;
using AiUsage.Data;

namespace AiUsage.Server;

/// <summary>What the user has to do next to finish a sign-in.</summary>
/// <param name="Kind">
/// "code": approve in the browser, paste the code the page shows.
/// "redirect": approve, then paste the address of the page the browser lands on.
/// "device": type <paramref name="UserCode"/> into the page; nothing to paste.
/// </param>
public sealed record SignInStep(string Kind, string Url, string? UserCode, string Instructions);

/// <summary>
/// Sign-in for a machine with no browser of its own. The desktop app catches the
/// provider's redirect on a loopback socket; a server in a cupboard cannot, so
/// here the user approves in whatever browser they have and carries the result
/// back by hand.
///
/// Each of these is a session of the server's own. That is the point: a refresh
/// token is single-use, so the server must never be handed one that an app or a
/// CLI is still holding.
/// </summary>
public sealed class SignInService(Credentials credentials, Poller poller, ILogger<SignInService> logger)
{
    /// <summary>
    /// Anthropic's own page for clients that cannot receive a redirect: it shows
    /// the authorization code for the user to copy.
    /// </summary>
    private const string ClaudeCodePage = "https://console.anthropic.com/oauth/code/callback";

    private static readonly TimeSpan PendingFor = TimeSpan.FromMinutes(15);

    private sealed record Pending(
        Credentials Account,
        OAuthConfig Config,
        string Verifier,
        string State,
        string RedirectUri,
        DateTimeOffset StartedAt);

    /// <summary>Both keyed by account: two accounts of one provider can be mid-sign-in at once.</summary>
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _status = new(StringComparer.Ordinal);

    /// <summary>"pending", "done", or "failed: reason" for the last sign-in of an account.</summary>
    public string? Status(Account account) => _status.GetValueOrDefault(account.Key);

    public async Task<SignInStep> StartAsync(Account account, CancellationToken cancellation)
    {
        var view = credentials.For(account.Slot);
        switch (account.Provider)
        {
            case ProviderId.Claude:
            {
                var pending = Begin(account, view, OAuthConfig.Claude, ClaudeCodePage);
                return new SignInStep(
                    "code",
                    AuthorizeUrl(pending),
                    null,
                    "Open the link, approve, and paste the code the page shows.");
            }

            case ProviderId.ChatGpt:
            {
                // OpenAI only accepts this exact redirect. Nothing is listening on
                // it, which is fine: the code is in the address either way.
                var config = OAuthConfig.ChatGpt;
                var pending = Begin(account, view, config, $"http://localhost:{config.FixedPort}{config.CallbackPath}");
                return new SignInStep(
                    "redirect",
                    AuthorizeUrl(pending),
                    null,
                    "Open the link and approve. The browser then lands on a localhost page that "
                    + "does not load. Paste that page's full address.");
            }

            case ProviderId.Muse:
            {
                var started = await DeviceLogin.StartAsync(cancellation);
                _status[account.Key] = "pending";
                _ = FinishDeviceAsync(account, view, started);
                return new SignInStep(
                    "device",
                    started.VerificationUrl,
                    started.UserCode,
                    "Open the link and enter the code. This finishes by itself once you approve.");
            }

            default:
                throw new InvalidOperationException(
                    $"{ProviderCatalog.Of(account.Provider).DisplayName} has no sign-in; it takes an API key.");
        }
    }

    /// <summary>Finishes a "code" or "redirect" sign-in with what the user pasted.</summary>
    public async Task CompleteAsync(Account account, string pasted, CancellationToken cancellation)
    {
        if (!_pending.TryRemove(account.Key, out var pending) || DateTimeOffset.UtcNow - pending.StartedAt > PendingFor)
        {
            throw new InvalidOperationException("No sign-in is waiting for this provider. Start one first.");
        }

        var (code, state) = Parse(pasted);
        if (string.IsNullOrEmpty(code)) throw new InvalidOperationException("That did not contain a code.");

        // The state proves the code belongs to the sign-in this server started,
        // and not to a link someone else got the user to open.
        if (state != null && state != pending.State)
        {
            throw new InvalidOperationException("That code belongs to a different sign-in. Start again.");
        }

        try
        {
            await OAuthFlow.CompleteAsync(
                pending.Config,
                pending.Account,
                code,
                pending.Verifier,
                pending.RedirectUri,
                pending.State,
                cancellation);
        }
        catch (Exception exception)
        {
            _status[account.Key] = $"failed: {exception.Message}";
            throw;
        }

        Finished(account);
    }

    /// <summary>
    /// What the user pasted, in any of the shapes it arrives in: Anthropic's
    /// `code#state`, a whole redirect address with both in its query, or a bare
    /// code.
    /// </summary>
    internal static (string Code, string? State) Parse(string pasted)
    {
        var text = pasted.Trim();

        var query = text.IndexOf('?');
        if (query >= 0 && text.Contains("code=", StringComparison.Ordinal))
        {
            string? code = null;
            string? state = null;
            foreach (var pair in text[(query + 1)..].Split('&', '#'))
            {
                var equals = pair.IndexOf('=');
                if (equals < 0) continue;
                var value = Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
                switch (pair[..equals])
                {
                    case "code": code = value; break;
                    case "state": state = value; break;
                }
            }
            return (code ?? "", state);
        }

        var hash = text.IndexOf('#');
        return hash >= 0 ? (text[..hash], text[(hash + 1)..]) : (text, null);
    }

    private Pending Begin(Account account, Credentials view, OAuthConfig config, string redirectUri)
    {
        var pending = new Pending(view, config, Pkce.Verifier(), Pkce.State(), redirectUri, DateTimeOffset.UtcNow);
        _pending[account.Key] = pending;
        _status[account.Key] = "pending";
        return pending;
    }

    private static string AuthorizeUrl(Pending pending) => OAuthFlow.AuthorizeUrl(
        pending.Config,
        pending.RedirectUri,
        Pkce.Challenge(pending.Verifier),
        pending.State);

    private async Task FinishDeviceAsync(Account account, Credentials view, DeviceLogin.Started started)
    {
        try
        {
            var token = await DeviceLogin.AwaitTokenAsync(started);
            view.SetManualToken(ProviderId.Muse, token);
            Finished(account);
        }
        catch (Exception exception)
        {
            _status[account.Key] = $"failed: {exception.Message}";
            logger.LogWarning("{Account} sign-in did not finish: {Reason}", account.DisplayName, exception.Message);
        }
    }

    private void Finished(Account account)
    {
        _status[account.Key] = "done";
        TokenRefresher.ForgetRejections();
        logger.LogInformation("{Account} signed in", account.DisplayName);
        poller.Hurry(account.Key);
    }

    /// <summary>Forgets anything half-done for an account that has been removed.</summary>
    public void Forget(Account account)
    {
        _pending.TryRemove(account.Key, out _);
        _status.TryRemove(account.Key, out _);
    }
}

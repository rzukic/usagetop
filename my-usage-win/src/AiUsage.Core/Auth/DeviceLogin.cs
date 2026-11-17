using System.Text.Json;
using AiUsage.Net;

namespace AiUsage.Auth;

/// <summary>
/// Device-authorization sign-in, the flow Muse Code's own CLI uses: the app asks
/// Meta for a short code, the browser approves it, and the app polls until a token
/// appears.
///
/// Nothing is caught on a socket here, so this is separate from <see cref="OAuthFlow"/>
/// -- there is no redirect URI and no PKCE. The token that comes back is the same
/// long-lived one the CLI stores, so there is nothing to refresh.
/// </summary>
public static class DeviceLogin
{
    private const string Auth = "https://auth.meta.com";

    /// <summary>The Muse Code CLI's own public client.</summary>
    private const string ClientId = "1031625952748946";

    private const string GrantType = "urn:ietf:params:oauth:grant-type:device_code";

    private static readonly Dictionary<string, string> Headers = new()
    {
        ["Accept"] = "application/json",
    };

    public sealed record Started(
        string DeviceCode,
        string UserCode,
        string VerificationUrl,
        long IntervalSeconds,
        long ExpiresInSeconds);

    /// <summary>Asks Meta for a code to show the user.</summary>
    public static async Task<Started> StartAsync(CancellationToken cancellation = default)
    {
        var response = await Http.PostFormAsync(
            $"{Auth}/oidc/device/authorization/",
            Headers,
            Form(("client_id", ClientId)),
            cancellation);

        if (response.Code is < 200 or > 299)
        {
            throw new InvalidOperationException($"Sign-in could not be started (HTTP {response.Code})");
        }

        using var document = Parse(response.Body);
        var root = document.RootElement;

        var deviceCode = Text(root, "device_code");
        var userCode = Text(root, "user_code");
        var url = Text(root, "verification_uri_complete") ?? Text(root, "verification_uri");

        if (deviceCode == null || userCode == null || url == null)
        {
            throw new InvalidOperationException("Sign-in response was incomplete");
        }

        return new Started(
            deviceCode,
            userCode,
            url,
            Math.Max(1L, Integer(root, "interval", 5L)),
            Math.Clamp(Integer(root, "expires_in", 900L), 60L, 1800L));
    }

    /// <summary>Polls until the browser approves the code, then returns the access token.</summary>
    public static async Task<string> AwaitTokenAsync(
        Started started,
        CancellationToken cancellation = default)
    {
        var interval = started.IntervalSeconds;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(started.ExpiresInSeconds);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), cancellation);

            var response = await Http.PostFormAsync(
                $"{Auth}/oidc/device/token/",
                Headers,
                Form(
                    ("grant_type", GrantType),
                    ("device_code", started.DeviceCode),
                    ("client_id", ClientId)),
                cancellation);

            if (response.Code is >= 200 and <= 299)
            {
                using var document = Parse(response.Body);
                var token = Text(document.RootElement, "access_token");
                return token ?? throw new InvalidOperationException("Sign-in returned no token");
            }

            switch (ErrorOf(response.Body))
            {
                case "authorization_pending":
                    break;
                case "slow_down":
                    interval += 5;
                    break;
                case "access_denied":
                    throw new InvalidOperationException("Sign-in was denied");
                case "expired_token":
                    throw new InvalidOperationException("The code expired");
                default:
                    throw new InvalidOperationException($"Sign-in failed (HTTP {response.Code})");
            }
        }

        throw new TimeoutException("Sign-in timed out");
    }

    /// <summary>
    /// Meta answers with an HTML page rather than JSON when it dislikes the request,
    /// so a parse failure is reported as what it is instead of a raw JsonException.
    /// </summary>
    private static JsonDocument Parse(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Meta returned a web page instead of a sign-in response");
        }
    }

    private static string? ErrorOf(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return Text(document.RootElement, "error");
        }
        catch
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() is { Length: > 0 } text ? text : null
            : null;

    private static long Integer(JsonElement element, string name, long fallback) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed) ? parsed : fallback;

    private static string Form(params (string Name, string Value)[] pairs) =>
        string.Join("&", pairs.Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value)}"));
}

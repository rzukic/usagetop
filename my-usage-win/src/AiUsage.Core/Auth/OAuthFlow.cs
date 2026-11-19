using System.Text;
using System.Text.Json;
using AiUsage.Data;
using AiUsage.Net;

namespace AiUsage.Auth;

/// <summary>
/// Authorization-code + PKCE sign-in against a provider's own OAuth server.
/// </summary>
public static class OAuthFlow
{
    private static readonly Dictionary<string, string> JsonHeaders = new()
    {
        ["Accept"] = "application/json",
    };

    /// <summary>Builds the URL the browser is sent to.</summary>
    public static string AuthorizeUrl(
        OAuthConfig config,
        string redirectUri,
        string challenge,
        string state)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("client_id", config.ClientId),
            new("response_type", "code"),
            new("redirect_uri", redirectUri),
            new("scope", config.Scopes),
            new("state", state),
            new("code_challenge", challenge),
            new("code_challenge_method", "S256"),
        };
        if (config.ExtraAuthParams != null) parameters.AddRange(config.ExtraAuthParams);

        var query = string.Join("&", parameters.Select(p => $"{p.Key}={Encode(p.Value)}"));
        return $"{config.AuthorizeUrl}?{query}";
    }

    /// <summary>
    /// Runs the whole flow: listen, open the browser, wait for the redirect, exchange
    /// the code. <paramref name="openBrowser"/> is passed in so the caller decides how
    /// the URL is opened.
    /// </summary>
    public static async Task SignInAsync(
        OAuthConfig config,
        Credentials credentials,
        Action<string> openBrowser,
        CancellationToken cancellation = default)
    {
        using var server = new LoopbackServer(config.CallbackPath, config.FixedPort);

        var verifier = Pkce.Verifier();
        var state = Pkce.State();
        var url = AuthorizeUrl(config, server.RedirectUri, Pkce.Challenge(verifier), state);

        openBrowser(url);

        var result = await server.WaitAsync(TimeSpan.FromMinutes(5), cancellation);

        if (result.Error != null) throw new InvalidOperationException($"Sign-in failed: {result.Error}");
        if (result.Code == null) throw new InvalidOperationException("Sign-in returned no code");
        if (result.State != state) throw new InvalidOperationException("Sign-in state did not match");

        await CompleteAsync(config, credentials, result.Code, verifier, server.RedirectUri, state, cancellation);
    }

    /// <summary>Exchanges the authorization code for tokens and stores them.</summary>
    public static async Task<string> CompleteAsync(
        OAuthConfig config,
        Credentials credentials,
        string code,
        string verifier,
        string redirectUri,
        string state,
        CancellationToken cancellation = default)
    {
        HttpResponse response;
        if (config.JsonTokenRequest)
        {
            var body = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = config.ClientId,
                ["code_verifier"] = verifier,
                ["state"] = state,
            });
            response = await Http.PostJsonAsync(config.TokenUrl, JsonHeaders, body, cancellation);
        }
        else
        {
            var body = Form(
                ("grant_type", "authorization_code"),
                ("code", code),
                ("redirect_uri", redirectUri),
                ("client_id", config.ClientId),
                ("code_verifier", verifier))
                + Secret(config);
            response = await Http.PostFormAsync(config.TokenUrl, JsonHeaders, body, cancellation);
        }

        if (response.Code != 200)
        {
            var detail = response.Body.Length > 300 ? response.Body[..300] : response.Body;
            throw new InvalidOperationException($"Token exchange failed (HTTP {response.Code}): {detail}");
        }

        return Store(config, credentials, response.Body);
    }

    /// <summary>Exchanges a stored refresh token for a new access token.</summary>
    public static async Task<bool> RefreshAsync(
        OAuthConfig config,
        Credentials credentials,
        CancellationToken cancellation = default)
    {
        var refreshToken = credentials.RefreshToken(config.ProviderId);
        if (refreshToken == null) return false;

        try
        {
            HttpResponse response;
            if (config.JsonTokenRequest)
            {
                var body = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                    ["client_id"] = config.ClientId,
                });
                response = await Http.PostJsonAsync(config.TokenUrl, JsonHeaders, body, cancellation);
            }
            else
            {
                var body = Form(
                    ("grant_type", "refresh_token"),
                    ("refresh_token", refreshToken),
                    ("client_id", config.ClientId),
                    ("scope", config.Scopes))
                    + Secret(config);
                response = await Http.PostFormAsync(config.TokenUrl, JsonHeaders, body, cancellation);
            }

            if (response.Code != 200) return false;
            Store(config, credentials, response.Body);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Store(OAuthConfig config, Credentials credentials, string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var access = Text(root, "access_token")
            ?? throw new InvalidOperationException("No access token in response");

        var expiresIn = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt64(out var seconds)
            ? seconds
            : 0L;

        var accountId = Text(root, "id_token") is { } idToken ? AccountIdFrom(idToken) : null;

        // One write for the whole session. A refresh answers with a new refresh
        // token that replaces a spent one, so the pair must never be half-stored.
        // A response without one keeps the token already on file.
        credentials.SetSession(
            config.ProviderId,
            access,
            Text(root, "refresh_token") ?? credentials.RefreshToken(config.ProviderId),
            expiresIn > 0L ? DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeMilliseconds() : 0L,
            accountId,
            borrowed: false);

        return access;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() is { Length: > 0 } text ? text : null
            : null;

    /// <summary>Pulls chatgpt_account_id out of the id_token claims, wherever it is nested.</summary>
    private static string? AccountIdFrom(string idToken)
    {
        var parts = idToken.Split('.');
        if (parts.Length < 2) return null;

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload += new string('=', (4 - payload.Length % 4) % 4);

        try
        {
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return FindString(document.RootElement, "chatgpt_account_id");
        }
        catch
        {
            return null;
        }
    }

    private static string? FindString(JsonElement element, string key)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name == key && property.Value.ValueKind == JsonValueKind.String)
                    {
                        return property.Value.GetString();
                    }
                    if (FindString(property.Value, key) is { } nested) return nested;
                }
                return null;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (FindString(item, key) is { } nested) return nested;
                }
                return null;
            default:
                return null;
        }
    }

    private static string Secret(OAuthConfig config) =>
        config.ClientSecret is { } secret ? $"&client_secret={Encode(secret)}" : "";

    private static string Form(params (string Name, string Value)[] pairs) =>
        string.Join("&", pairs.Select(p => $"{p.Name}={Encode(p.Value)}"));

    private static string Encode(string value) => Uri.EscapeDataString(value);
}

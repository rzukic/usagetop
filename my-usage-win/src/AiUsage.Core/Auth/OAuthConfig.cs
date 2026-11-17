using AiUsage.Data;

namespace AiUsage.Auth;

/// <summary>
/// OAuth endpoints for the providers that actually have a browser sign-in.
///
/// Both are public clients (no secret) using authorization code + PKCE, and both
/// accept a loopback redirect, so the app can run its own callback listener.
/// Mistral and Moonshot have no OAuth server at all -- they only issue static API
/// keys, so they are not listed here. Muse has one, but it signs in with a device
/// code rather than a redirect, so it lives in <see cref="DeviceLogin"/>.
/// </summary>
public sealed record OAuthConfig(
    ProviderId ProviderId,
    string AuthorizeUrl,
    string TokenUrl,
    string ClientId,
    string Scopes,
    string CallbackPath,
    /// <summary>Null binds an ephemeral port; OpenAI requires the fixed 1455.</summary>
    int? FixedPort,
    IReadOnlyDictionary<string, string>? ExtraAuthParams = null,
    /// <summary>Claude's token endpoint takes JSON, OpenAI's takes form encoding.</summary>
    bool JsonTokenRequest = false)
{
    public static readonly OAuthConfig Claude = new(
        ProviderId.Claude,
        "https://claude.ai/oauth/authorize",
        "https://console.anthropic.com/v1/oauth/token",
        "9d1c250a-e61b-44d9-88ed-5944d1962f5e",
        // The usage and profile endpoints only ask for this one. Claude Code's own
        // list also carries user:inference and org:create_api_key, which would let
        // a token meant for reading a percentage run models and mint API keys.
        "user:profile",
        "/callback",
        null,
        new Dictionary<string, string> { ["code"] = "true" },
        JsonTokenRequest: true);

    public static readonly OAuthConfig ChatGpt = new(
        ProviderId.ChatGpt,
        "https://auth.openai.com/oauth/authorize",
        "https://auth.openai.com/oauth/token",
        "app_EMoamEEZ73f0CkXaXp7hrann",
        "openid profile email offline_access",
        "/auth/callback",
        1455,
        new Dictionary<string, string>
        {
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
        });

    public static readonly IReadOnlyList<OAuthConfig> All = [Claude, ChatGpt];

    public static OAuthConfig? ForProvider(ProviderId id) => All.FirstOrDefault(c => c.ProviderId == id);
}

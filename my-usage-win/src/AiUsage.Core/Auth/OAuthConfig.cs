using AiUsage.Data;

namespace AiUsage.Auth;

/// <summary>
/// OAuth endpoints for the providers that actually have a browser sign-in.
///
/// All use authorization code + PKCE and accept a loopback redirect, so the app
/// can run its own callback listener. Claude's and OpenAI's are public clients;
/// Google's installed-app client has a secret that is not one: it ships inside
/// every copy of Antigravity, as Google's own documentation for installed apps
/// expects.
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
    bool JsonTokenRequest = false,
    /// <summary>Sent with every token request when the client has one.</summary>
    string? ClientSecret = null)
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

    /// <summary>
    /// Antigravity's own Google client. cloud-platform is the narrowest scope the
    /// quota endpoint accepts; a token with it can also call Google Cloud APIs as
    /// the account, which is why the server keeps its tokens sealed.
    /// </summary>
    public static readonly OAuthConfig Antigravity = new(
        ProviderId.Antigravity,
        "https://accounts.google.com/o/oauth2/v2/auth",
        "https://oauth2.googleapis.com/token",
        "1071006060591-tmhssin2h21lcre235vtolojh4g403ep.apps.googleusercontent.com",
        "https://www.googleapis.com/auth/cloud-platform",
        "/oauth-callback",
        null,
        new Dictionary<string, string>
        {
            // A refresh token, and a new one on every sign-in rather than none
            // when the account has approved this client before.
            ["access_type"] = "offline",
            ["prompt"] = "consent",
        },
        ClientSecret: "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"); // gitleaks:allow (public: it ships in every Antigravity install)

    public static readonly IReadOnlyList<OAuthConfig> All = [Claude, ChatGpt, Antigravity];

    public static OAuthConfig? ForProvider(ProviderId id) => All.FirstOrDefault(c => c.ProviderId == id);
}

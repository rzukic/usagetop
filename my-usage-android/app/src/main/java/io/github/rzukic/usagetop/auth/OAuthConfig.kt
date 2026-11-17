package io.github.rzukic.usagetop.auth

import io.github.rzukic.usagetop.data.ProviderId

/**
 * OAuth endpoints for the providers that actually have a browser sign-in.
 *
 * Both are public clients (no secret) using authorization code + PKCE, and both
 * accept a loopback redirect, so the app can run its own callback listener.
 * Mistral and Moonshot have no OAuth server at all -- they only issue static
 * API keys, so they are not listed here. Muse has one, but it signs in with a
 * device code rather than a redirect, so it lives in [DeviceLogin].
 */
enum class OAuthConfig(
    val providerId: ProviderId,
    val authorizeUrl: String,
    val tokenUrl: String,
    val clientId: String,
    val scopes: String,
    val callbackPath: String,
    /** null binds an ephemeral port; OpenAI requires the fixed 1455. */
    val fixedPort: Int?,
    val extraAuthParams: Map<String, String> = emptyMap(),
    /** Claude's token endpoint takes JSON, OpenAI's takes form encoding. */
    val jsonTokenRequest: Boolean = false,
) {
    CLAUDE(
        providerId = ProviderId.CLAUDE,
        authorizeUrl = "https://claude.ai/oauth/authorize",
        tokenUrl = "https://console.anthropic.com/v1/oauth/token",
        clientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e",
        // The usage and profile endpoints only ask for this one. Claude Code's own
        // list also carries user:inference and org:create_api_key, which would let
        // a token meant for reading a percentage run models and mint API keys.
        scopes = "user:profile",
        callbackPath = "/callback",
        fixedPort = null,
        extraAuthParams = mapOf("code" to "true"),
        jsonTokenRequest = true,
    ),

    CHATGPT(
        providerId = ProviderId.CHATGPT,
        authorizeUrl = "https://auth.openai.com/oauth/authorize",
        tokenUrl = "https://auth.openai.com/oauth/token",
        clientId = "app_EMoamEEZ73f0CkXaXp7hrann",
        scopes = "openid profile email offline_access",
        callbackPath = "/auth/callback",
        fixedPort = 1455,
        extraAuthParams = mapOf(
            "id_token_add_organizations" to "true",
            "codex_cli_simplified_flow" to "true",
        ),
    );

    companion object {
        fun forProvider(id: ProviderId): OAuthConfig? = values().firstOrNull { it.providerId == id }
    }
}

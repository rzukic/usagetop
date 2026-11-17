package io.github.rzukic.usagetop.auth

import android.util.Base64
import io.github.rzukic.usagetop.data.Credentials
import io.github.rzukic.usagetop.net.Http
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.net.URLEncoder
import java.time.Instant

/**
 * Authorization-code + PKCE sign-in against a provider's own OAuth server.
 *
 * The redirect is caught on the loopback interface by [OAuthCallbackService],
 * so the browser hands the code straight back -- no copy-pasting codes or
 * tokens.
 */
object OAuthFlow {

    /** Builds the URL the browser is sent to. */
    internal fun authorizeUrl(
        config: OAuthConfig,
        redirectUri: String,
        challenge: String,
        state: String,
    ): String {
        val params = linkedMapOf(
            "client_id" to config.clientId,
            "response_type" to "code",
            "redirect_uri" to redirectUri,
            "scope" to config.scopes,
            "state" to state,
            "code_challenge" to challenge,
            "code_challenge_method" to "S256",
        )
        params.putAll(config.extraAuthParams)
        return config.authorizeUrl + "?" + params.entries.joinToString("&") { (name, value) ->
            "$name=${encode(value)}"
        }
    }

    /** Exchanges the authorization code for tokens and stores them. */
    suspend fun complete(
        config: OAuthConfig,
        credentials: Credentials,
        code: String,
        verifier: String,
        redirectUri: String,
        state: String,
    ): String = withContext(Dispatchers.IO) {
        val response = if (config.jsonTokenRequest) {
            val body = JSONObject()
                .put("grant_type", "authorization_code")
                .put("code", code)
                .put("redirect_uri", redirectUri)
                .put("client_id", config.clientId)
                .put("code_verifier", verifier)
                .put("state", state)
                .toString()
            Http.postJson(config.tokenUrl, JSON_HEADERS, body)
        } else {
            val body = form(
                "grant_type" to "authorization_code",
                "code" to code,
                "redirect_uri" to redirectUri,
                "client_id" to config.clientId,
                "code_verifier" to verifier,
            )
            Http.postForm(config.tokenUrl, JSON_HEADERS, body)
        }

        if (response.code != 200) {
            throw IllegalStateException(
                "Token exchange failed (HTTP ${response.code}): ${response.body.take(300)}"
            )
        }
        store(config, credentials, JSONObject(response.body))
    }

    /** Exchanges a stored refresh token for a new access token. */
    suspend fun refresh(config: OAuthConfig, credentials: Credentials): Boolean =
        withContext(Dispatchers.IO) {
            val refreshToken = credentials.refreshToken(config.providerId) ?: return@withContext false

            val response = if (config.jsonTokenRequest) {
                val body = JSONObject()
                    .put("grant_type", "refresh_token")
                    .put("refresh_token", refreshToken)
                    .put("client_id", config.clientId)
                    .toString()
                Http.postJson(config.tokenUrl, JSON_HEADERS, body)
            } else {
                val body = form(
                    "grant_type" to "refresh_token",
                    "refresh_token" to refreshToken,
                    "client_id" to config.clientId,
                    "scope" to config.scopes,
                )
                Http.postForm(config.tokenUrl, JSON_HEADERS, body)
            }

            if (response.code != 200) return@withContext false
            runCatching { store(config, credentials, JSONObject(response.body)) }.isSuccess
        }

    private fun store(config: OAuthConfig, credentials: Credentials, json: JSONObject): String {
        val access = json.optString("access_token").takeIf { it.isNotEmpty() }
            ?: throw IllegalStateException("No access token in response")

        val expiresIn = json.optLong("expires_in", 0L)

        // One write for the whole session. A refresh answers with a new refresh
        // token that replaces a spent one, so the pair must never be half-stored.
        // A response without one keeps the token already on file.
        credentials.setSession(
            id = config.providerId,
            access = access,
            refresh = json.optString("refresh_token").takeIf { it.isNotEmpty() }
                ?: credentials.refreshToken(config.providerId),
            expiresAt = if (expiresIn > 0L) Instant.now().plusSeconds(expiresIn).toEpochMilli() else 0L,
            accountId = json.optString("id_token").takeIf { it.isNotEmpty() }?.let(::accountIdFrom),
        )

        return access
    }

    /** Pulls chatgpt_account_id out of the id_token claims, wherever it is nested. */
    private fun accountIdFrom(idToken: String): String? {
        val payload = idToken.split('.').getOrNull(1) ?: return null
        val padded = payload + "=".repeat((4 - payload.length % 4) % 4)
        return try {
            val json = String(Base64.decode(padded, Base64.URL_SAFE or Base64.NO_WRAP))
            findString(JSONObject(json), "chatgpt_account_id")
        } catch (t: Throwable) {
            null
        }
    }

    private fun findString(json: JSONObject, key: String): String? {
        json.keys().forEach { name ->
            when (val value = json.opt(name)) {
                is JSONObject -> findString(value, key)?.let { return it }
                is String -> if (name == key) return value
            }
        }
        return null
    }

    private fun form(vararg pairs: Pair<String, String>): String =
        pairs.joinToString("&") { (name, value) -> "$name=${encode(value)}" }

    private fun encode(value: String): String = URLEncoder.encode(value, "UTF-8")

    private val JSON_HEADERS = mapOf("Accept" to "application/json")
}

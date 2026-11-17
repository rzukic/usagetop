package io.github.rzukic.usagetop.auth

import io.github.rzukic.usagetop.net.Http
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.net.URLEncoder

/**
 * Device-authorization sign-in, the flow Muse Code's own CLI uses: the app asks
 * Meta for a short code, the browser approves it, and the app polls until a
 * token appears.
 *
 * Nothing is caught on a socket here, so this is separate from [OAuthFlow] --
 * there is no redirect URI and no PKCE. The token that comes back is the same
 * long-lived one the CLI stores, so there is nothing to refresh.
 */
object DeviceLogin {

    data class Started(
        val deviceCode: String,
        val userCode: String,
        val verificationUrl: String,
        val intervalSeconds: Long,
        val expiresInSeconds: Long,
    )

    /** Asks Meta for a code to show the user. */
    suspend fun start(): Started = withContext(Dispatchers.IO) {
        val response = Http.postForm(
            "$AUTH/oidc/device/authorization/",
            HEADERS,
            form("client_id" to CLIENT_ID),
        )

        if (response.code !in 200..299) {
            throw IllegalStateException("Sign-in could not be started (HTTP ${response.code})")
        }

        val json = JSONObject(response.body)
        val deviceCode = json.optString("device_code")
        val userCode = json.optString("user_code")
        val url = json.optString("verification_uri_complete")
            .ifBlank { json.optString("verification_uri") }

        if (deviceCode.isBlank() || userCode.isBlank() || url.isBlank()) {
            throw IllegalStateException("Sign-in response was incomplete")
        }

        Started(
            deviceCode = deviceCode,
            userCode = userCode,
            verificationUrl = url,
            intervalSeconds = json.optLong("interval", 5L).coerceAtLeast(1L),
            expiresInSeconds = json.optLong("expires_in", 900L).coerceIn(60L, 1800L),
        )
    }

    /** Polls until the browser approves the code, then returns the access token. */
    suspend fun awaitToken(started: Started): String = withContext(Dispatchers.IO) {
        var interval = started.intervalSeconds
        val deadline = System.currentTimeMillis() + started.expiresInSeconds * 1000

        while (System.currentTimeMillis() < deadline) {
            delay(interval * 1000)

            val response = Http.postForm(
                "$AUTH/oidc/device/token/",
                HEADERS,
                form(
                    "grant_type" to GRANT_TYPE,
                    "device_code" to started.deviceCode,
                    "client_id" to CLIENT_ID,
                ),
            )

            if (response.code in 200..299) {
                val token = JSONObject(response.body).optString("access_token")
                if (token.isNotBlank()) return@withContext token
                throw IllegalStateException("Sign-in returned no token")
            }

            when (errorOf(response.body)) {
                "authorization_pending" -> Unit
                "slow_down" -> interval += 5
                "access_denied" -> throw IllegalStateException("Sign-in was denied")
                "expired_token" -> throw IllegalStateException("The code expired")
                else -> throw IllegalStateException("Sign-in failed (HTTP ${response.code})")
            }
        }

        throw IllegalStateException("Sign-in timed out")
    }

    private fun errorOf(body: String): String =
        runCatching { JSONObject(body).optString("error") }.getOrDefault("")

    private fun form(vararg pairs: Pair<String, String>): String =
        pairs.joinToString("&") { (name, value) ->
            "$name=${URLEncoder.encode(value, "UTF-8")}"
        }

    private val HEADERS = mapOf("Accept" to "application/json")

    private const val AUTH = "https://auth.meta.com"

    /** The Muse Code CLI's own public client. */
    private const val CLIENT_ID = "1031625952748946"

    private const val GRANT_TYPE = "urn:ietf:params:oauth:grant-type:device_code"
}

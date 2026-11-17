package io.github.rzukic.usagetop.providers

import io.github.rzukic.usagetop.data.Credentials
import io.github.rzukic.usagetop.data.ProviderId
import io.github.rzukic.usagetop.data.ProviderUsage
import io.github.rzukic.usagetop.data.UsageState
import io.github.rzukic.usagetop.data.UsageWindow
import io.github.rzukic.usagetop.net.Http
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.time.Instant

/**
 * ChatGPT Plus/Pro rate-limit windows, read from the same backend endpoint the
 * Codex CLI uses. Authenticated with the ChatGPT session access token.
 */
class ChatGptProvider : UsageProvider {

    override val id = ProviderId.CHATGPT

    override suspend fun fetch(credentials: Credentials): ProviderUsage = withContext(Dispatchers.IO) {
        val token = credentials.token(id)
            ?: return@withContext ProviderUsage(id, UsageState.NOT_CONFIGURED, message = "No token")

        val headers = buildMap {
            put("Authorization", "Bearer $token")
            put("Accept", "application/json")
            put("User-Agent", USER_AGENT)
            credentials.accountId(id)?.let { put("chatgpt-account-id", it) }
        }

        val response = try {
            Http.get(ENDPOINT, headers)
        } catch (t: Throwable) {
            return@withContext networkError(id, t)
        }

        if (response.code != 200) return@withContext httpError(id, response.code)

        val root = try {
            JSONObject(response.body)
        } catch (t: Throwable) {
            return@withContext ProviderUsage(id, UsageState.ERROR, message = "Unexpected response")
        }

        val rateLimit = root.optJSONObject("rate_limit") ?: root
        val windows = buildList {
            rateLimit.optJSONObject("primary_window")?.let { add(window(it)) }
            rateLimit.optJSONObject("secondary_window")?.let { add(window(it)) }
        }

        if (windows.isEmpty()) {
            return@withContext ProviderUsage(id, UsageState.ERROR, message = "No limits in response")
        }
        ProviderUsage(
            id,
            UsageState.OK,
            windows = windows,
            plan = prettyPlan(root.optString("plan_type")),
        )
    }

    /** The usage payload names the tier directly: `plan_type` is `plus`, `pro`, ... */
    private fun prettyPlan(raw: String): String? = when (raw.lowercase()) {
        "free" -> "Free"
        "plus" -> "Plus"
        "pro" -> "Pro"
        "team" -> "Team"
        "business" -> "Business"
        "enterprise" -> "Enterprise"
        else -> raw.takeIf { it.isNotBlank() }?.replaceFirstChar { it.uppercase() }
    }

    private fun window(json: JSONObject): UsageWindow {
        val used = json.optDouble("used_percent", 0.0).toFloat().coerceIn(0f, 100f)

        val resetAfter = json.optLong("reset_after_seconds", -1L)
        val resetAt = if (resetAfter >= 0L) Instant.now().plusSeconds(resetAfter) else null

        return UsageWindow(labelFor(json.optLong("limit_window_seconds", 0L)), 100f - used, resetAt)
    }

    private fun labelFor(seconds: Long): String = when {
        seconds <= 0L -> "limit"
        seconds < 3_600L -> "${seconds / 60}m"
        seconds < 86_400L -> "${seconds / 3_600L}h"
        else -> "${seconds / 86_400L}d"
    }

    private companion object {
        const val ENDPOINT = "https://chatgpt.com/backend-api/wham/usage"
        const val USER_AGENT = "Mozilla/5.0 (Linux; Android 14) ai-usage-widget/1.0"
    }
}

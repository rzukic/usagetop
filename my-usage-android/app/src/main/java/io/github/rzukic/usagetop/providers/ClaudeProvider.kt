package io.github.rzukic.usagetop.providers

import io.github.rzukic.usagetop.data.Credentials
import io.github.rzukic.usagetop.data.ProviderId
import io.github.rzukic.usagetop.data.ProviderUsage
import io.github.rzukic.usagetop.data.UsageState
import io.github.rzukic.usagetop.data.UsageWindow
import io.github.rzukic.usagetop.net.Http
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject

/**
 * Claude Pro/Max rolling limits. Same endpoint Claude Code's `/usage` command uses,
 * authenticated with the OAuth access token rather than an API key.
 *
 * Three windows come back: the five-hour session, the weekly total, and -- since
 * Fable 5.2 -- the share of that weekly total the Fable models may use.
 */
class ClaudeProvider : UsageProvider {

    override val id = ProviderId.CLAUDE

    override suspend fun fetch(credentials: Credentials): ProviderUsage = withContext(Dispatchers.IO) {
        val token = credentials.token(id)
            ?: return@withContext ProviderUsage(id, UsageState.NOT_CONFIGURED, message = "No token")

        val headers = mapOf(
            "Authorization" to "Bearer $token",
            "anthropic-beta" to "oauth-2025-04-20",
            "Accept" to "application/json",
        )

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

        val windows = buildList {
            root.optJSONObject("five_hour")?.let { add(window("5h", it)) }
            root.optJSONObject("seven_day")?.let { add(window("7d", it)) }
            addAll(scoped(root.optJSONArray("limits")))
        }

        if (windows.isEmpty()) {
            return@withContext ProviderUsage(id, UsageState.ERROR, message = "No usage in response")
        }
        ProviderUsage(id, UsageState.OK, windows = windows, plan = plan(headers))
    }

    /**
     * The usage endpoint carries no tier, but the OAuth profile does:
     * `organization.organization_type` is `claude_pro`, `claude_max`, and so on.
     */
    private fun plan(headers: Map<String, String>): String? {
        val response = runCatching { Http.get(PROFILE, headers) }.getOrNull() ?: return null
        if (response.code != 200) return null

        val type = runCatching {
            JSONObject(response.body).optJSONObject("organization")?.optString("organization_type")
        }.getOrNull().orEmpty()

        return when (type) {
            "claude_pro" -> "Pro"
            "claude_max" -> "Max"
            "claude_team" -> "Team"
            "claude_enterprise" -> "Enterprise"
            "claude_free" -> "Free"
            else -> null
        }
    }

    private fun window(label: String, json: JSONObject): UsageWindow {
        val used = when {
            json.has("utilization") -> json.optDouble("utilization", 0.0)
            json.has("used_percent") -> json.optDouble("used_percent", 0.0)
            else -> 0.0
        }.toFloat().coerceIn(0f, 100f)

        return UsageWindow(label, 100f - used, parseInstant(json.optString("resets_at")))
    }

    /**
     * Per-model weekly limits live in `limits[]` rather than in a flat key: a
     * `weekly_scoped` entry names the model it applies to and how much of the
     * weekly allowance that model may use. It is a slice of `seven_day`, not a
     * second allowance, so it is kept out of the combined figure.
     */
    internal fun scoped(limits: JSONArray?): List<UsageWindow> {
        if (limits == null) return emptyList()

        return (0 until limits.length()).mapNotNull { index ->
            val entry = limits.optJSONObject(index) ?: return@mapNotNull null
            if (entry.optString("kind") != "weekly_scoped") return@mapNotNull null

            val model = entry.optJSONObject("scope")
                ?.optJSONObject("model")
                ?.optString("display_name")
                ?.takeIf { it.isNotBlank() }
                ?: return@mapNotNull null

            val used = entry.optDouble("percent", Double.NaN)
            if (used.isNaN()) return@mapNotNull null

            UsageWindow(
                label = "$model 7d",
                remainingPercent = (100.0 - used).coerceIn(0.0, 100.0).toFloat(),
                resetAt = parseInstant(entry.optString("resets_at")),
                countsTowardCombined = false,
            )
        }
    }

    private companion object {
        const val ENDPOINT = "https://api.anthropic.com/api/oauth/usage"
        const val PROFILE = "https://api.anthropic.com/api/oauth/profile"
    }
}

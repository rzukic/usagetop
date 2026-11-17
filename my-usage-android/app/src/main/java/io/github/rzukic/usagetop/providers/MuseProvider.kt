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
 * Muse Code is Meta's coding agent, sold as a subscription with a rolling
 * 5-hour window and a weekly total. There is no documented usage API, but the
 * route the CLI itself calls answers to the CLI's own OAuth access token:
 *
 *   POST /muse-code/key   {"onboard": false}
 *     -> is_subs_active, subs_tier_name,
 *        subs_usage: { window: { used_percent, resets_at },
 *                      weekly: { used_percent, resets_at } }
 *
 * The same response carries the standing inference key. Only the quota fields
 * are read, so the key is never stored.
 *
 * Meta omits `subs_usage` while the 5-hour window is idle, even when the weekly
 * allowance has been spent. That is reported as a transient failure so the last
 * reading stands in, rather than as a full allowance. Once that reading's reset
 * has passed the window is known to be full again, which [rollOver] applies.
 */
class MuseProvider : UsageProvider {

    override val id = ProviderId.MUSE

    override suspend fun fetch(credentials: Credentials): ProviderUsage = withContext(Dispatchers.IO) {
        val token = credentials.token(id)
            ?: return@withContext ProviderUsage(id, UsageState.NOT_CONFIGURED, message = "No token")

        val response = try {
            Http.postJson("$BASE/muse-code/key", headers(token), BODY)
        } catch (t: Throwable) {
            return@withContext networkError(id, t)
        }

        if (response.code != 200) return@withContext httpError(id, response.code)

        val root = runCatching { JSONObject(response.body) }.getOrNull()
            ?: return@withContext ProviderUsage(id, UsageState.ERROR, message = "Unexpected response")

        if (!root.optBoolean("is_subs_active", false)) {
            return@withContext ProviderUsage(id, UsageState.ERROR, message = "No subscription")
        }

        val usage = root.optJSONObject("subs_usage")
        val windows = listOfNotNull(
            usage?.optJSONObject("window")?.let { window("5h", it) },
            usage?.optJSONObject("weekly")?.let { window("Weekly", it) },
        )

        if (windows.isEmpty()) {
            return@withContext ProviderUsage(
                id,
                UsageState.ERROR,
                message = "No usage reported",
                transient = true,
            )
        }

        ProviderUsage(id, UsageState.OK, windows = windows, plan = planName(root))
    }

    private fun window(label: String, json: JSONObject): UsageWindow? {
        val used = json.optDouble("used_percent", Double.NaN)
        if (used.isNaN()) return null

        return UsageWindow(
            label = label,
            remainingPercent = (100.0 - used).coerceIn(0.0, 100.0).toFloat(),
            resetAt = resetAt(json.opt("resets_at")),
        )
    }

    /** `resets_at` arrives as Unix seconds; a string form is tolerated. */
    private fun resetAt(value: Any?): Instant? = when (value) {
        is Number -> Instant.ofEpochSecond(value.toLong())
        is String -> value.toLongOrNull()?.let(Instant::ofEpochSecond) ?: parseInstant(value)
        else -> null
    }

    /** "Muse Code Power Usage" reads better as "Power". */
    private fun planName(root: JSONObject): String? {
        val raw = root.optString("subs_tier_name").takeIf { it.isNotBlank() } ?: return null
        return raw.removePrefix("Muse Code ").removeSuffix(" Usage").ifBlank { raw }
    }

    private fun headers(token: String) = mapOf(
        "Authorization" to "Bearer $token",
        "x-api-version" to "1.0.0",
        "Accept" to "application/json",
    )

    companion object {
        /**
         * Meta stops reporting `subs_usage` once the five-hour window is spent and
         * only starts again when it refills, so a reading kept from before that
         * reset would sit at zero for hours after the window has reloaded. A window
         * whose reset has passed is therefore rebuilt at 100%: the reset time is
         * the one thing the last reading did know. The next reset is not known
         * until Meta reports again, so the window is left without one.
         */
        fun rollOver(usage: ProviderUsage, now: Instant): ProviderUsage {
            if (usage.id != ProviderId.MUSE || !usage.stale) return usage

            val expired = usage.windows.any { window ->
                window.resetAt?.let { !it.isAfter(now) } == true
            }
            if (!expired) return usage

            return usage.copy(
                windows = usage.windows.map { window ->
                    val reset = window.resetAt
                    if (reset != null && !reset.isAfter(now)) {
                        window.copy(remainingPercent = 100f, resetAt = null)
                    } else {
                        window
                    }
                },
            )
        }

        private const val BASE = "https://api.meta.ai"

        /** `onboard: false` asks for the quota without minting a new key. */
        private const val BODY = """{"onboard":false}"""
    }
}

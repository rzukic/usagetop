package io.github.rzukic.usagetop.providers

import android.util.Log
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
import java.util.Locale

/**
 * Kimi sells two unrelated things:
 *
 *  - Kimi Code, a subscription with a rolling 5-hour window and a monthly total,
 *    reported by `GET https://api.kimi.ai/coding/v1/usages`.
 *  - The Open Platform, prepaid credit in CNY, reported by
 *    `GET /v1/users/me/balance` on the Moonshot hosts.
 *
 * The subscription is tried first. The balance is only a fallback, for keys that
 * are not Kimi Code keys -- otherwise a subscriber sees a meaningless ¥0.00.
 */
class KimiProvider : UsageProvider {

    override val id = ProviderId.KIMI

    override suspend fun fetch(credentials: Credentials): ProviderUsage = withContext(Dispatchers.IO) {
        val token = credentials.token(id)
            ?: return@withContext ProviderUsage(id, UsageState.NOT_CONFIGURED, message = "No key")

        planUsage(token) ?: balanceUsage(token, credentials.kimiChina())
    }

    /** Rolling 5-hour and monthly windows of a Kimi Code subscription. */
    private fun planUsage(token: String): ProviderUsage? {
        // Kimi Code runs two deployments with separate keys: api.kimi.ai for
        // overseas accounts and api.kimi.com for China. Try both.
        for (host in PLAN_HOSTS) {
            val usage = planUsageOn(host, token)
            if (usage != null) return usage
        }
        return null
    }

    private fun planUsageOn(host: String, token: String): ProviderUsage? {
        // The Kimi Code API is both OpenAI- and Anthropic-compatible, and the two
        // protocols authenticate differently. Try both before giving up.
        for (headers in listOf(bearerHeaders(token), apiKeyHeaders(token))) {
            val response = try {
                Http.get("$host/coding/v1/usages", headers)
            } catch (t: Throwable) {
                Log.i(TAG, "$host unreachable: ${t.message}")
                return null
            }

            if (response.code == 401 || response.code == 403) {
                Log.i(TAG, "$host usages HTTP ${response.code} via ${headers.keys.first()}")
                continue
            }
            if (response.code != 200) {
                Log.i(TAG, "$host usages HTTP ${response.code}: ${response.body.take(200)}")
                return null
            }

            Log.i(TAG, "$host usages body: ${response.body.take(1500)}")
            return parsePlan(response.body)?.copy(plan = planName(host, headers))
        }
        return null
    }

    /**
     * The response carries two generations of the same data:
     *
     *  - `usages`: ratio pools, one per window (`limit_5h`, `limit_month_total`,
     *    `limit_month_code`), each `{ used_ratio, reset_time }`.
     *  - `limits`: legacy request counts, each `{ window, detail: { limit, used,
     *    resetTime } }`.
     *
     * The ratio pools are preferred; the counts only fill in when they are absent.
     */
    private fun parsePlan(body: String): ProviderUsage? {
        val root = runCatching { JSONObject(body) }.getOrNull() ?: return null

        val ratioWindows = buildList {
            root.optJSONObject("usages")?.let { usages ->
                ratioWindow("5h", usages.optJSONObject("limit_5h"))?.let { add(it) }
                ratioWindow("Monthly", usages.optJSONObject("limit_month_total"))?.let { add(it) }
            }
        }
        if (ratioWindows.isNotEmpty()) return ProviderUsage(id, UsageState.OK, windows = ratioWindows)

        val countWindows = buildList {
            root.optJSONArray("limits")?.let { limits ->
                for (index in 0 until limits.length()) {
                    val entry = limits.optJSONObject(index) ?: continue
                    val detail = entry.optJSONObject("detail") ?: entry
                    countWindow(labelFor(entry, index, limits.length()), detail)?.let { add(it) }
                }
            }
        }
        if (countWindows.isEmpty()) return null
        return ProviderUsage(id, UsageState.OK, windows = countWindows)
    }

    /** The usages payload has no tier; `/coding/v1/me` reports `user_level_name`. */
    private fun planName(host: String, headers: Map<String, String>): String? {
        val response = runCatching { Http.get("$host/coding/v1/me", headers) }.getOrNull() ?: return null
        if (response.code != 200) return null

        return runCatching {
            JSONObject(response.body).optString("user_level_name").takeIf { it.isNotBlank() }
        }.getOrNull()
    }

    private fun ratioWindow(label: String, json: JSONObject?): UsageWindow? {
        if (json == null) return null
        val usedRatio = json.optDouble("used_ratio", Double.NaN)
        if (usedRatio.isNaN()) return null

        return UsageWindow(
            label = label,
            remainingPercent = ((1.0 - usedRatio) * 100.0).coerceIn(0.0, 100.0).toFloat(),
            resetAt = parseInstant(json.optString("reset_time")),
        )
    }

    private fun countWindow(label: String, json: JSONObject): UsageWindow? {
        val limit = json.optDouble("limit", Double.NaN)
        if (limit.isNaN() || limit <= 0.0) return null

        val used = json.optDouble("used", 0.0).coerceIn(0.0, limit)
        return UsageWindow(
            label = label,
            remainingPercent = (((limit - used) / limit) * 100.0).coerceIn(0.0, 100.0).toFloat(),
            resetAt = resetAt(json.opt("resetTime")),
            detail = String.format(Locale.US, "%.0f of %.0f left", limit - used, limit),
        )
    }

    /** Legacy entries describe their window as `{ duration, timeUnit }`. */
    private fun labelFor(entry: JSONObject, index: Int, total: Int): String {
        val window = entry.optJSONObject("window")
        val duration = window?.optLong("duration", 0L) ?: 0L
        val unit = window?.optString("timeUnit").orEmpty()

        val minutes = when {
            unit.contains("MINUTE") -> duration
            unit.contains("HOUR") -> duration * 60
            unit.contains("DAY") -> duration * 24 * 60
            else -> 0L
        }

        return when {
            minutes <= 0L -> if (total > 1) "window ${index + 1}" else "limit"
            minutes < 60L -> "${minutes}m"
            minutes < 1440L -> "${minutes / 60}h"
            else -> "${minutes / 1440}d"
        }
    }

    /** `resetTime` arrives as epoch millis, epoch seconds or an ISO timestamp. */
    private fun resetAt(value: Any?): Instant? = when (value) {
        is Number -> {
            val raw = value.toLong()
            Instant.ofEpochMilli(if (raw < 100_000_000_000L) raw * 1000 else raw)
        }
        is String -> parseInstant(value)
        else -> null
    }

    /** Prepaid Open Platform credit, for keys that are not Kimi Code keys. */
    private fun balanceUsage(token: String, china: Boolean): ProviderUsage {
        val base = if (china) CN_BASE else GLOBAL_BASE

        val response = try {
            Http.get("$base/v1/users/me/balance", bearerHeaders(token))
        } catch (t: Throwable) {
            return networkError(id, t)
        }

        if (response.code != 200) return httpError(id, response.code)

        val root = runCatching { JSONObject(response.body) }.getOrNull()
            ?: return ProviderUsage(id, UsageState.ERROR, message = "Unexpected response")
        val data = root.optJSONObject("data") ?: root
        val available = data.optDouble("available_balance", Double.NaN)
        if (available.isNaN()) {
            return ProviderUsage(id, UsageState.ERROR, message = "No balance in response")
        }

        // A Kimi Code subscriber who pasted an Open Platform key lands here: the
        // plan endpoint rejects the key and the prepaid credit is empty. Point at
        // the right console instead of showing a meaningless ¥0.00.
        if (available <= 0.0) {
            return ProviderUsage(id, UsageState.ERROR, message = "Kimi Code key needed")
        }

        return ProviderUsage(
            id = id,
            state = UsageState.OK,
            windows = listOf(
                UsageWindow(
                    label = "balance",
                    detail = String.format(Locale.US, "¥%.2f available", available),
                ),
            ),
        )
    }

    private fun bearerHeaders(token: String) = mapOf(
        "Authorization" to "Bearer $token",
        "Accept" to "application/json",
    )

    private fun apiKeyHeaders(token: String) = mapOf(
        "x-api-key" to token,
        "Accept" to "application/json",
    )

    private companion object {
        const val TAG = "AiUsageKimi"

        /** Overseas first, then China; a key is only valid on one of them. */
        val PLAN_HOSTS = listOf("https://api.kimi.ai", "https://api.kimi.com")

        const val GLOBAL_BASE = "https://api.moonshot.ai"
        const val CN_BASE = "https://api.moonshot.cn"
    }
}

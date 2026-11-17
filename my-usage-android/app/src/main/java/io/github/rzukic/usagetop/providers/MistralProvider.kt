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
import java.util.Locale

/**
 * Mistral has no documented usage API, but its own billing routes -- the ones
 * the mistral-vibe CLI and OpenUsage read -- accept a normal API key:
 *
 *   GET /v1/billing/budget        api_budget + vibe_budget: percent, cap, reset
 *   GET /v1/billing/subscription  plan name
 *
 * Undocumented but first-party, so no browser session or console scraping is
 * involved. Both return 401 without a key, which is how we know they exist.
 */
class MistralProvider : UsageProvider {

    override val id = ProviderId.MISTRAL

    override suspend fun fetch(credentials: Credentials): ProviderUsage = withContext(Dispatchers.IO) {
        val token = credentials.token(id)
            ?: return@withContext ProviderUsage(id, UsageState.NOT_CONFIGURED, message = "No key")

        val headers = mapOf(
            "Authorization" to "Bearer $token",
            "Accept" to "application/json",
        )

        val response = try {
            Http.get("$BASE/v1/billing/budget", headers)
        } catch (t: Throwable) {
            return@withContext networkError(id, t)
        }

        if (response.code != 200) {
            return@withContext manualFallback(credentials, httpError(id, response.code))
        }

        val root = try {
            JSONObject(response.body)
        } catch (t: Throwable) {
            return@withContext ProviderUsage(id, UsageState.ERROR, message = "Unexpected response")
        }

        val windows = budgets(root)

        if (windows.isEmpty()) {
            return@withContext manualFallback(
                credentials,
                ProviderUsage(id, UsageState.ERROR, message = "No budget in response"),
            )
        }

        ProviderUsage(id, UsageState.OK, windows = windows, plan = planName(headers))
    }

    /**
     * The two budgets the billing route reports. The API credit is a separate
     * pool that coding workflows cannot spend, so it stays on the card but out of
     * the combined figure; the Vibe allowance is what the subscription gives you.
     */
    internal fun budgets(root: JSONObject): List<UsageWindow> = buildList {
        root.optJSONObject("api_budget")?.let {
            add(window("API", it, countsTowardCombined = false))
        }
        root.optJSONObject("vibe_budget")?.let { add(window("Vibe", it)) }
    }

    private fun window(
        label: String,
        json: JSONObject,
        countsTowardCombined: Boolean = true,
    ): UsageWindow {
        val usedPercent = json.optDouble("usage_percentage", 0.0).toFloat().coerceIn(0f, 100f)
        val cap = json.optDouble("initial_budget", 0.0)
        val currency = json.optString("currency").takeIf { it.isNotBlank() && it != "null" }
        val left = cap * (100.0 - usedPercent) / 100.0

        val detail = if (cap > 0.0) {
            val symbol = symbol(currency)
            String.format(Locale.US, "%s%.2f of %s%.2f left", symbol, left, symbol, cap)
        } else {
            null
        }

        return UsageWindow(
            label = label,
            remainingPercent = 100f - usedPercent,
            resetAt = parseInstant(json.optString("reset_at")),
            detail = detail,
            countsTowardCombined = countsTowardCombined,
        )
    }

    private fun planName(headers: Map<String, String>): String? {
        val response = runCatching { Http.get("$BASE/v1/billing/subscription", headers) }.getOrNull()
            ?: return null
        if (response.code != 200) return null

        return runCatching {
            JSONObject(response.body).optString("plan_name").takeIf { it.isNotBlank() }
        }.getOrNull()?.let(::prettyPlan)
    }

    private fun prettyPlan(raw: String): String = when (raw.uppercase()) {
        "EDU" -> "Education"
        "FREE" -> "Free"
        "INDIVIDUAL" -> "Pro"
        "TEAM" -> "Team"
        else -> raw.lowercase().replaceFirstChar { it.uppercase() }
    }

    /** Manual budget/spend, kept for keys that lack billing scope. */
    private fun manualFallback(credentials: Credentials, failure: ProviderUsage): ProviderUsage {
        val budget = credentials.mistralBudget() ?: return failure
        val spent = credentials.mistralSpent() ?: 0.0
        val used = ((spent / budget) * 100.0).toFloat().coerceIn(0f, 100f)

        return ProviderUsage(
            id = id,
            state = UsageState.OK,
            windows = listOf(
                UsageWindow(
                    label = "month",
                    remainingPercent = 100f - used,
                    detail = String.format(Locale.US, "€%.2f of €%.2f left", budget - spent, budget),
                ),
            ),
            message = failure.message,
        )
    }

    private fun symbol(currency: String?): String = when (currency?.uppercase()) {
        "EUR" -> "€"
        "USD" -> "$"
        "GBP" -> "£"
        else -> ""
    }

    private companion object {
        const val BASE = "https://api.mistral.ai"
    }
}

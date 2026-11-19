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
 * A Google AI plan's Antigravity quota (Pro, Ultra), read from the endpoint the
 * Antigravity CLI's own `/quota` uses. Authenticated with a Google OAuth access
 * token from Antigravity's client.
 *
 * The models are split into groups, each with a weekly limit and most with a
 * five-hour one: "Gemini Models", "Claude and GPT models", and Gemini Argon,
 * which on Pro has a weekly limit of its own. The Gemini group is the plan's
 * main allowance and the only one in the combined figure.
 *
 * The twin of the Windows app's and the server's AntigravityProvider.cs.
 */
class AntigravityProvider : UsageProvider {

    override val id = ProviderId.ANTIGRAVITY

    override suspend fun fetch(credentials: Credentials): ProviderUsage = withContext(Dispatchers.IO) {
        val token = credentials.token(id)
            ?: return@withContext ProviderUsage(id, UsageState.NOT_CONFIGURED, message = "Not signed in")

        val headers = mapOf(
            "Authorization" to "Bearer $token",
            "Accept" to "application/json",
            "User-Agent" to USER_AGENT,
        )

        var plan: String? = null
        var project = CONSUMER_PROJECT
        val response = try {
            // The plan's name and, for a managed workspace, its project. A failure
            // here other than a refused token still leaves the quota worth asking.
            val load = Http.postJson("$BASE:loadCodeAssist", headers, """{"metadata":{"ideType":"ANTIGRAVITY"}}""")
            if (load.code == 401) return@withContext httpError(id, load.code)
            if (load.code == 200) {
                runCatching { account(JSONObject(load.body)) }.getOrNull()?.let { (name, managed) ->
                    plan = name
                    if (managed != null) project = managed
                }
            }
            Http.postJson("$BASE:retrieveUserQuotaSummary", headers, JSONObject().put("project", project).toString())
        } catch (t: Throwable) {
            return@withContext networkError(id, t)
        }

        // What a Google account without a plan, or one Antigravity has not been
        // opened with yet, is told.
        if (response.code == 403) {
            return@withContext ProviderUsage(id, UsageState.ERROR, message = "No Antigravity plan", plan = plan)
        }
        if (response.code != 200) return@withContext httpError(id, response.code)

        val root = try {
            JSONObject(response.body)
        } catch (t: Throwable) {
            return@withContext ProviderUsage(id, UsageState.ERROR, message = "Unexpected response")
        }

        val windows = windows(root)
        if (windows.isEmpty()) {
            return@withContext ProviderUsage(id, UsageState.ERROR, message = "No limits in response", plan = plan)
        }
        ProviderUsage(id, UsageState.OK, windows = windows, plan = plan)
    }

    companion object {
        private const val BASE = "https://daily-cloudcode-pa.googleapis.com/v1internal"

        /** The project a consumer plan's quota is filed under. */
        private const val CONSUMER_PROJECT = "aicode-consumers"

        /** The quota endpoint only answers clients that name themselves as Antigravity. */
        private const val USER_AGENT = "antigravity/cli/1.5.12 (aidev_client; auth_method=consumer)"

        /**
         * The plan's name ("Google AI Pro"; a paid tier over the free one), and the
         * project to ask for a managed workspace, null otherwise.
         */
        internal fun account(root: JSONObject): Pair<String?, String?> {
            val plan = (root.optJSONObject("paidTier")?.text("name") ?: root.optJSONObject("currentTier")?.text("name"))
                ?.trim()?.takeIf { it.isNotEmpty() }
            val project = if (root.optBoolean("gcpManaged")) {
                root.text("cloudaicompanionProject") ?: root.optJSONObject("cloudaicompanionProject")?.text("id")
            } else {
                null
            }
            return plan to project
        }

        /**
         * Every group's buckets as windows. The Gemini group's are plain "5h" and
         * "7d"; another group's carry its name, "Claude & GPT 7d". A bucket that is
         * switched off, or that says nothing about what is left, is skipped.
         */
        internal fun windows(root: JSONObject): List<UsageWindow> {
            val groups = root.optJSONArray("groups").objects()
            // An older answer had no groups, only buckets.
            if (groups.isEmpty()) {
                return buildList { add(this, root.optJSONArray("buckets"), prefix = "", main = true) }
            }

            val main = groups.indexOfFirst { short(it.text("displayName")) == "Gemini" }.takeIf { it >= 0 } ?: 0
            return buildList {
                groups.forEachIndexed { index, group ->
                    val isMain = index == main
                    add(this, group.optJSONArray("buckets"), if (isMain) "" else short(group.text("displayName")), isMain)
                }
            }
        }

        private fun add(into: MutableList<UsageWindow>, buckets: JSONArray?, prefix: String, main: Boolean) {
            for (bucket in buckets.objects()) {
                if (bucket.optBoolean("disabled")) continue
                if (!bucket.has("remainingFraction")) continue
                val fraction = bucket.optDouble("remainingFraction", Double.NaN)
                if (fraction.isNaN()) continue

                val span = span(bucket.text("window")) ?: bucket.text("displayName") ?: "limit"
                into.add(
                    UsageWindow(
                        label = if (prefix.isEmpty()) span else "$prefix $span",
                        remainingPercent = (fraction * 100.0).toFloat().coerceIn(0f, 100f),
                        resetAt = parseInstant(bucket.text("resetTime")),
                        countsTowardCombined = main,
                    )
                )
            }
        }

        /** "weekly" is "7d" like every other provider's week; "5h" stays. */
        private fun span(window: String?): String? = when (val text = window?.trim()?.lowercase()) {
            null, "" -> null
            "weekly", "week" -> "7d"
            "daily", "day" -> "1d"
            "monthly", "month" -> "Monthly"
            else -> text.takeIf { it[0].isDigit() }
        }

        /** "Gemini Models" is "Gemini", "Claude and GPT models" is "Claude & GPT". */
        private fun short(name: String?): String {
            var text = name.orEmpty().trim()
            if (text.endsWith(" models", ignoreCase = true)) text = text.dropLast(" models".length).trimEnd()
            return text.replace(" and ", " & ")
        }

        private fun JSONObject.text(name: String): String? =
            if (has(name) && !isNull(name)) (opt(name) as? String)?.takeIf { it.isNotEmpty() } else null

        private fun JSONArray?.objects(): List<JSONObject> =
            if (this == null) emptyList() else (0 until length()).mapNotNull { optJSONObject(it) }
    }
}

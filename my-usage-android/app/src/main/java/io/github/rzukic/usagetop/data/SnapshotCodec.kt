package io.github.rzukic.usagetop.data

import io.github.rzukic.usagetop.providers.parseInstant
import org.json.JSONArray
import org.json.JSONObject
import java.time.Instant

/** JSON round-trip for the cached snapshot. Contains no secrets. */
object SnapshotCodec {

    fun encode(snapshot: Snapshot): String {
        val root = JSONObject()
        root.put("fetchedAt", snapshot.fetchedAt.toString())

        val usages = JSONArray()
        snapshot.usages.forEach { usage ->
            val json = JSONObject()
            json.put("id", usage.id.key)
            json.put("account", usage.accountKey)
            json.put("accountName", usage.accountName ?: JSONObject.NULL)
            json.put("state", usage.state.name)
            json.put("message", usage.message ?: JSONObject.NULL)
            json.put("plan", usage.plan ?: JSONObject.NULL)
            json.put("fetchedAt", usage.fetchedAt.toString())
            json.put("stale", usage.stale)

            val windows = JSONArray()
            usage.windows.forEach { window ->
                val w = JSONObject()
                w.put("label", window.label)
                w.put("remainingPercent", window.remainingPercent?.toDouble() ?: JSONObject.NULL)
                w.put("resetAt", window.resetAt?.toString() ?: JSONObject.NULL)
                w.put("detail", window.detail ?: JSONObject.NULL)
                w.put("countsTowardCombined", window.countsTowardCombined)
                windows.put(w)
            }
            json.put("windows", windows)
            usages.put(json)
        }
        root.put("usages", usages)
        return root.toString()
    }

    fun decode(raw: String?): Snapshot? {
        if (raw.isNullOrBlank()) return null
        return try {
            val root = JSONObject(raw)
            val array = root.optJSONArray("usages") ?: return null

            val usages = (0 until array.length()).mapNotNull { index ->
                val json = array.optJSONObject(index) ?: return@mapNotNull null
                val id = ProviderId.fromKey(json.optString("id")) ?: return@mapNotNull null
                val state = runCatching { UsageState.valueOf(json.optString("state")) }
                    .getOrDefault(UsageState.ERROR)

                val windowArray = json.optJSONArray("windows")
                val windows = if (windowArray == null) {
                    emptyList()
                } else {
                    (0 until windowArray.length()).mapNotNull windowLoop@{ w ->
                        val window = windowArray.optJSONObject(w) ?: return@windowLoop null
                        UsageWindow(
                            label = window.optString("label"),
                            remainingPercent = remainingPercent(window),
                            resetAt = parseInstant(window.optString("resetAt")),
                            detail = if (window.isNull("detail")) null else window.optString("detail"),
                            countsTowardCombined = window.optBoolean("countsTowardCombined", true),
                        )
                    }
                }

                ProviderUsage(
                    id = id,
                    state = state,
                    windows = windows,
                    message = if (json.isNull("message")) null else json.optString("message"),
                    plan = if (json.isNull("plan")) null else json.optString("plan"),
                    fetchedAt = parseInstant(json.optString("fetchedAt")) ?: Instant.now(),
                    stale = json.optBoolean("stale", false),
                    // Absent before accounts existed, which reads as the first account.
                    slot = Accounts.parse(json.optString("account"))
                        ?.takeIf { it.first == id }?.second.orEmpty(),
                    accountName = if (json.isNull("accountName")) null else json.optString("accountName"),
                )
            }

            Snapshot(usages, parseInstant(root.optString("fetchedAt")) ?: Instant.now())
        } catch (t: Throwable) {
            null
        }
    }

    /** Caches written before the switch to "remaining" stored the inverse. */
    private fun remainingPercent(window: JSONObject): Float? = when {
        !window.isNull("remainingPercent") -> window.optDouble("remainingPercent").toFloat()
        !window.isNull("usedPercent") -> 100f - window.optDouble("usedPercent").toFloat()
        else -> null
    }
}

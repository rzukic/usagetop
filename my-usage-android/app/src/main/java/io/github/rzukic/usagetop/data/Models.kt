package io.github.rzukic.usagetop.data

import java.time.Instant

enum class ProviderId(val key: String, val displayName: String, val monogram: String) {
    CLAUDE("claude", "Claude", "C"),
    CHATGPT("chatgpt", "ChatGPT", "G"),
    MISTRAL("mistral", "Mistral", "M"),
    KIMI("kimi", "Kimi", "K"),
    MUSE("muse", "Muse", "U"),
    ANTIGRAVITY("antigravity", "Antigravity", "A");

    companion object {
        fun fromKey(key: String): ProviderId? = values().firstOrNull { it.key == key }
    }
}

enum class UsageState { OK, NOT_CONFIGURED, ERROR }

/**
 * One quota window for a provider. [remainingPercent] is how much of the window
 * is still available, 0-100; null for providers that only expose a balance.
 */
data class UsageWindow(
    val label: String,
    val remainingPercent: Float? = null,
    val resetAt: Instant? = null,
    val detail: String? = null,
    /**
     * False when the window belongs on the provider's own card but not in the
     * combined figure. Claude's Fable window is a slice of its 7-day one, so
     * counting it would weigh the same usage twice; Mistral's API credit cannot
     * be spent on coding workflows, so it is not part of what the subscription
     * gives you.
     */
    val countsTowardCombined: Boolean = true,
)

data class ProviderUsage(
    val id: ProviderId,
    val state: UsageState,
    val windows: List<UsageWindow> = emptyList(),
    val message: String? = null,
    val fetchedAt: Instant = Instant.now(),
    /** The subscription tier, e.g. "Pro" or "Education". */
    val plan: String? = null,
    /** The failure says nothing about the quota, so an earlier reading may stand in. */
    val transient: Boolean = false,
    /** [windows] come from an earlier fetch because this one failed. */
    val stale: Boolean = false,
    /**
     * The provider answered 401: the access token is no longer accepted. Kept
     * apart from the message so the fetch can renew the token and try once more
     * instead of reading its own wording back.
     */
    val rejected: Boolean = false,
    /**
     * Which of the provider's accounts this is: empty for the first, "2" and on
     * for ones added on a server. This app only ever fetches the first itself.
     */
    val slot: String = "",
    /** What the account was called on the server, e.g. "Work". */
    val accountName: String? = null,
) {
    /** "claude", or "claude-2": what history and the server file it under. */
    val accountKey: String get() = Accounts.keyOf(id, slot)

    /** "Claude", or "Claude · Work". */
    val displayName: String
        get() = accountName?.trim()?.takeIf { it.isNotEmpty() }?.let { "${id.displayName} · $it" }
            ?: if (slot.isEmpty()) id.displayName else "${id.displayName} #$slot"
}

/** The account keys the server uses, the same rule as the C# side's Account. */
object Accounts {
    fun keyOf(id: ProviderId, slot: String): String = if (slot.isEmpty()) id.key else "${id.key}-$slot"

    /** "claude" or "claude-2" back into its provider and slot; null for anything else. */
    fun parse(key: String?): Pair<ProviderId, String>? {
        if (key.isNullOrBlank()) return null
        val dash = key.indexOf('-')
        val provider = ProviderId.fromKey((if (dash < 0) key else key.substring(0, dash)).lowercase())
            ?: return null
        val slot = if (dash < 0) "" else key.substring(dash + 1)
        if (dash >= 0 && !isSlot(slot)) return null
        return provider to slot
    }

    private fun isSlot(slot: String) =
        slot.length in 1..16 && slot.all { it in '0'..'9' || it in 'a'..'z' }
}

data class Snapshot(
    val usages: List<ProviderUsage>,
    val fetchedAt: Instant,
)

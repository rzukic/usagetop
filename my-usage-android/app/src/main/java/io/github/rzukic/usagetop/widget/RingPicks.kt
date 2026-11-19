package io.github.rzukic.usagetop.widget

import android.content.Context
import io.github.rzukic.usagetop.data.HiddenLimits
import io.github.rzukic.usagetop.data.ProviderUsage
import io.github.rzukic.usagetop.data.Snapshot
import io.github.rzukic.usagetop.data.UsageState
import io.github.rzukic.usagetop.data.UsageWindow

/**
 * Which windows a rings widget shows: any window of any account, each at most
 * once, up to four, in the order they were picked. Kept per placed widget.
 */
object RingPicks {

    const val MAX = 4

    /** A window by its account and label, the way history files it: "claude-2|7d". */
    data class Pick(val account: String, val label: String) {
        val key: String get() = "$account|$label"

        companion object {
            fun parse(key: String): Pick? {
                val bar = key.lastIndexOf('|')
                if (bar <= 0 || bar == key.length - 1) return null
                return Pick(key.substring(0, bar), key.substring(bar + 1))
            }

            fun of(usage: ProviderUsage, window: UsageWindow) = Pick(usage.accountKey, window.label)
        }
    }

    /** A pick resolved against the reading; [window] null when it is gone or unread. */
    data class Ring(val pick: Pick, val usage: ProviderUsage?, val window: UsageWindow?)

    fun read(context: Context, widgetId: Int): List<Pick>? =
        prefs(context).getString(key(widgetId), null)
            ?.split('\n')
            ?.mapNotNull { Pick.parse(it) }

    fun write(context: Context, widgetId: Int, picks: List<Pick>) {
        prefs(context).edit()
            .putString(key(widgetId), picks.distinct().take(MAX).joinToString("\n") { it.key })
            .apply()
    }

    fun forget(context: Context, widgetId: Int) {
        prefs(context).edit().remove(key(widgetId)).apply()
    }

    /**
     * Until something is picked: the windows closest to empty, as many as fit,
     * leaving out [hidden] ones. Picks someone made stay as made, hidden or not.
     */
    fun defaults(snapshot: Snapshot?, hidden: Set<String> = emptySet()): List<Pick> = choices(snapshot)
        .filter { (usage, window) -> window.remainingPercent != null && !HiddenLimits.isHidden(hidden, usage, window) }
        .sortedBy { (_, window) -> window.remainingPercent }
        .take(MAX)
        .map { (usage, window) -> Pick.of(usage, window) }

    /** Every window there is to pick, account by account. */
    fun choices(snapshot: Snapshot?): List<Pair<ProviderUsage, UsageWindow>> =
        snapshot?.usages.orEmpty()
            .filter { it.state == UsageState.OK }
            .flatMap { usage -> usage.windows.map { usage to it } }

    /**
     * The picks against the reading. A pick whose window has gone keeps its place,
     * as an empty ring, rather than shuffling the others along.
     */
    fun resolve(picks: List<Pick>, snapshot: Snapshot?): List<Ring> = picks.map { pick ->
        val usage = snapshot?.usages?.firstOrNull { it.accountKey == pick.account }
        val window = usage?.takeIf { it.state == UsageState.OK }?.windows?.firstOrNull { it.label == pick.label }
        Ring(pick, usage, window)
    }

    private fun key(widgetId: Int) = "picks_$widgetId"
    private fun prefs(context: Context) = context.getSharedPreferences("widget_rings", Context.MODE_PRIVATE)
}

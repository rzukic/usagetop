package io.github.rzukic.usagetop.widget

import io.github.rzukic.usagetop.data.CombinedUsage
import io.github.rzukic.usagetop.data.HiddenLimits
import io.github.rzukic.usagetop.data.ProviderUsage
import io.github.rzukic.usagetop.data.Projection
import io.github.rzukic.usagetop.data.Snapshot
import io.github.rzukic.usagetop.data.UsageState
import io.github.rzukic.usagetop.data.UsageWindow
import java.time.Duration
import java.time.Instant

/**
 * What the widget shows, apart from how it is drawn: one line per account, the
 * window its ring stands for, the other windows worth a mention, and the pace.
 * The same reading as the Mac panel (`Forecast.swift`), so the two agree.
 */
object WidgetModel {

    /** A limit is blue until it warns, then amber, then red. */
    const val WARNING = 40f
    const val CRITICAL = 15f

    /** Older than this, a reading says how old it is. */
    val STALE_AFTER: Duration = Duration.ofMinutes(45)

    enum class Pace { EMPTY, AHEAD, ON, UNDER }

    /** A window other than the ring's, shown small beside it: "5h 38%". */
    data class Mention(val window: UsageWindow, val percent: Float, val ahead: Boolean)

    /**
     * One account. [window] is the one its ring and figure stand for: the lowest
     * it has, counted in the combined figure or not, because that is the one that
     * stops you first. [ahead] is about that window only.
     */
    data class Line(
        val usage: ProviderUsage,
        val window: UsageWindow,
        val percent: Float,
        val ahead: Boolean,
        val mentions: List<Mention>,
    )

    /** An account that needs a sign-in rather than a ring. */
    data class Out(val usage: ProviderUsage)

    data class Accounts(val lines: List<Line>, val out: List<Out>)

    /** What would be left now had the window been spent evenly since it refilled. */
    fun evenPercent(window: UsageWindow, now: Instant): Float? {
        val length = Projection.period(window.label) ?: return null
        val reset = window.resetAt ?: return null
        val left = Duration.between(now, reset)
        if (left.isNegative || left.isZero || left > length) return null
        return (left.toMillis().toDouble() / length.toMillis() * 100).toFloat()
    }

    fun pace(window: UsageWindow, now: Instant): Pace? {
        val level = window.remainingPercent ?: return null
        if (level < 0.5f) return Pace.EMPTY
        val even = evenPercent(window, now) ?: return null
        val gap = level - even
        return when {
            gap < -5f -> Pace.AHEAD
            gap > 5f -> Pace.UNDER
            else -> Pace.ON
        }
    }

    /**
     * Every account worth a line, the one closest to running out first. Accounts
     * with nothing to read and no sign-in problem are left out entirely; ones that
     * need a sign-in go last, as a single quiet line. [hidden] limits are not
     * shown: an account whose limits are all hidden has no line.
     */
    fun accounts(snapshot: Snapshot?, now: Instant = Instant.now(), hidden: Set<String> = emptySet()): Accounts {
        val usages = snapshot?.usages.orEmpty()
        val lines = usages
            .filter { it.state == UsageState.OK }
            .mapNotNull { line(it, now, hidden) }
            .sortedBy { it.percent }
        val out = usages.filter { it.state == UsageState.ERROR }.map { Out(it) }
        return Accounts(lines, out)
    }

    /** The account's line from its visible limits; null when none can be shown. */
    fun line(usage: ProviderUsage, now: Instant, hidden: Set<String> = emptySet()): Line? {
        val rated = HiddenLimits.visible(usage, hidden).filter { it.remainingPercent != null }
        val lowest = rated.minByOrNull { it.remainingPercent!! } ?: return null
        val percent = lowest.remainingPercent!!

        val mentions = rated
            .filter { it !== lowest }
            .mapNotNull { window ->
                val level = window.remainingPercent!!
                val ahead = pace(window, now) == Pace.AHEAD
                // A window outside the combined figure (Claude's Fable slice) only
                // earns a place once it needs attention.
                val worth = window.countsTowardCombined || level <= WARNING || ahead
                if (worth) Mention(window, level, ahead) else null
            }

        return Line(usage, lowest, percent, pace(lowest, now) == Pace.AHEAD, mentions)
    }

    /** The next reset that lifts the combined figure, and whose it is. */
    data class Lift(val at: Instant, val usage: ProviderUsage)

    fun nextLift(snapshot: Snapshot?, now: Instant = Instant.now()): Lift? {
        val usages = snapshot?.usages.orEmpty().filter { it.state == UsageState.OK }
        return usages
            .flatMap { usage -> CombinedUsage.longWindows(usage).map { usage to it } }
            .mapNotNull { (usage, window) ->
                val at = window.resetAt ?: return@mapNotNull null
                if (at.isAfter(now)) Lift(at, usage) else null
            }
            .minByOrNull { it.at }
    }

    /** How old the reading is, once that is worth saying; null while it is fresh. */
    fun staleAge(snapshot: Snapshot?, now: Instant = Instant.now()): Duration? {
        val at = snapshot?.fetchedAt ?: return null
        val age = Duration.between(at, now)
        return if (age >= STALE_AFTER) age else null
    }

    /**
     * "7d" → "Weekly", "Fable 7d" → "Fable weekly", "1d" → "Daily"; anything else
     * ("5h", "Monthly", "Vibe") as it is. The Mac's `WindowName.display`.
     */
    fun name(window: UsageWindow): String {
        val trimmed = window.label.trim()
        val match = DAYS_LABEL.matchEntire(trimmed) ?: return trimmed
        val word = DAY_WORDS[match.groupValues[2].toIntOrNull()] ?: return trimmed
        val prefix = match.groupValues[1]
        return if (prefix.isEmpty()) word else "$prefix ${word.lowercase()}"
    }

    private val DAYS_LABEL = Regex("""^(?:(.*\S)\s+)?(\d+)\s*[dD]$""")
    private val DAY_WORDS = mapOf(1 to "Daily", 7 to "Weekly")

    /** The shortest name, for under a small ring: "5h", "Week", "Month". */
    fun shortName(window: UsageWindow): String {
        val period = Projection.period(window.label)
        return when {
            window.label.trim().lowercase().startsWith("fable") -> "Fable"
            period == null -> window.label
            period < Duration.ofDays(1) -> window.label
            period <= Duration.ofDays(7) -> "Week"
            else -> "Month"
        }
    }
}

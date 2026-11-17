package io.github.rzukic.usagetop.data

import java.time.Duration

/**
 * Every subscription's long windows rolled into one figure.
 *
 * The APIs only ever expose percentages, never token counts, so "total tokens
 * available" can only be the average of what is left. Each subscription counts
 * once, however many windows it reports, which is what the caption promises. The
 * short windows are deliberately left out: a 5-hour limit resets several times a
 * day and would drag the line around without saying anything about the month.
 */
object CombinedUsage {

    /** The combined figure plus what went into it. */
    data class Combined(val percent: Float?, val providers: Int, val windows: Int)

    /**
     * The windows that count as a provider's long limit: the tier that resets
     * furthest away. Claude and ChatGPT contribute their 7-day window, Kimi its
     * monthly one, Mistral its Vibe allowance, Muse its weekly one.
     *
     * Windows that do not count toward the combined figure are left out: Claude's
     * Fable window is a slice of its 7-day one, and Mistral's API credit cannot be
     * spent on coding workflows.
     */
    fun longWindows(usage: ProviderUsage): List<UsageWindow> {
        // A five-hour window resets several times a day, so it says nothing about
        // the month. It has to go before the tier is picked: a short window that
        // happens to reset within a day of the long one would otherwise be read as
        // the same tier and averaged in.
        val long = usage.windows.filter {
            it.remainingPercent != null && it.countsTowardCombined && !it.isShortWindow()
        }

        // A provider that only exposes a short window still counts -- dropping it
        // would silently remove a subscription from the average.
        val rated = long.ifEmpty {
            usage.windows.filter { it.remainingPercent != null && it.countsTowardCombined }
        }
        if (rated.isEmpty()) return emptyList()

        // Without any reset time there is no way to tell the tiers apart, so
        // every window stands in rather than none.
        val furthest = rated.mapNotNull { it.resetAt }.maxOrNull() ?: return rated

        return rated.filter { window ->
            window.resetAt == null || Duration.between(window.resetAt, furthest) <= SAME_TIER
        }
    }

    /** "5h" is a short window; "7d", "Weekly" and "Monthly" are not. */
    private fun UsageWindow.isShortWindow(): Boolean {
        val match = DURATION_LABEL.matchEntire(label.trim()) ?: return false
        val amount = match.groupValues[1].toDoubleOrNull() ?: return false
        val hours = when (match.groupValues[2].lowercase()) {
            "m" -> amount / 60.0
            "h" -> amount
            "d" -> amount * 24.0
            else -> return false
        }
        return hours < 24.0
    }

    /** A window that goes into the average, with the provider it belongs to. */
    data class Contribution(val provider: ProviderId, val window: UsageWindow, val account: String = provider.key)

    /**
     * Every window that goes into the average, flattened. The chart needs them
     * individually to know how far each reset lifts the combined level, and whose
     * mark to put above it.
     */
    fun contributions(snapshot: Snapshot): List<Contribution> = snapshot.usages
        .filter { it.state == UsageState.OK }
        .flatMap { usage -> longWindows(usage).map { Contribution(usage.id, it, usage.accountKey) } }

    /**
     * One provider's long windows as a single level. The row on the All usage
     * screen and the combined figure both go through here, so the two cannot
     * drift apart the way they did when the row averaged on its own.
     */
    fun level(windows: List<UsageWindow>): Float? {
        val rated = windows.mapNotNull { it.remainingPercent }
        return if (rated.isEmpty()) null else rated.average().toFloat()
    }

    /**
     * The average of each subscription's own level. A subscription counts once
     * however many budgets it reports, which is what the caption promises: a
     * second budget is not a second subscription.
     */
    fun of(snapshot: Snapshot): Combined {
        val contributing = snapshot.usages
            .filter { it.state == UsageState.OK }
            .map { longWindows(it) }
            .filter { it.isNotEmpty() }

        val levels = contributing.mapNotNull { level(it) }

        return Combined(
            percent = if (levels.isEmpty()) null else levels.average().toFloat(),
            providers = levels.size,
            windows = contributing.sumOf { windows -> windows.count { it.remainingPercent != null } },
        )
    }

    /** Windows resetting within a day of each other belong to the same tier. */
    private val SAME_TIER: Duration = Duration.ofHours(24)

    /** A window label that is a plain duration, e.g. "5h" or "7d". */
    private val DURATION_LABEL = Regex("""(\d+)\s*([mhd])""", RegexOption.IGNORE_CASE)
}

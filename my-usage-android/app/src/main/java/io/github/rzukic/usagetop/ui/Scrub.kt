package io.github.rzukic.usagetop.ui

import io.github.rzukic.usagetop.data.UsagePoint
import java.time.Instant

/**
 * What the chart reads out under the finger. A level holds until the next
 * reading, so the level at a moment is the last reading at or before it, not an
 * interpolation between two: the line is a step, and the number should be the
 * one the step is at.
 */
object Scrub {

    /** A level, and whether it came from the forecast rather than a reading. */
    data class Level(val point: UsagePoint, val projected: Boolean)

    /**
     * The level at [at]. Past the last reading it is the forecast, if there is
     * one; with no forecast it stays on the last reading, which is what the line
     * on screen does too.
     */
    fun levelAt(
        series: List<UsagePoint>,
        projection: List<UsagePoint>,
        at: Instant,
    ): Level? {
        val last = series.lastOrNull() ?: return null

        if (at.isAfter(last.at)) {
            projection.lastOrNull { !it.at.isAfter(at) }?.let { return Level(it, true) }
        }

        val point = series.lastOrNull { !it.at.isAfter(at) } ?: series.first()
        return Level(point, false)
    }
}

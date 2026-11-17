package io.github.rzukic.usagetop.data

import java.time.Duration
import java.time.Instant

/**
 * The straight line through a drawdown once its resets are taken out.
 *
 * A window that resets every week saws up and down, and a line fitted to that
 * sawtooth says nothing useful. Adding each refill back in turns the series into
 * cumulative consumption, which only ever rises, so a least-squares line through
 * it is the actual burn rate. Subtracting the refills again puts the line back in
 * percent space, where it steps up wherever the allowance was topped up.
 */
object Trendline {

    /** A window resetting: the level jumped back up by [amount] points. */
    data class Refill(val at: Instant, val amount: Float)

    /** The fitted line in percent space, plus where the refills happened. */
    data class Fit(
        val vertices: List<UsagePoint>,
        val refills: List<Refill>,
        /** Percent per hour the line falls at between refills; negative. */
        val rate: Double,
    )

    /** A jump this large is a reset, not a reading drifting. */
    private const val REFILL = 1.5f

    private const val MILLIS_PER_HOUR = 3_600_000.0

    fun fit(points: List<UsagePoint>): Fit? {
        if (points.size < 2) return null
        val sorted = points.sortedBy { it.at }
        val origin = sorted.first().at

        val refills = ArrayList<Refill>()
        sorted.zipWithNext { previous, next ->
            val jump = next.percent - previous.percent
            if (jump >= REFILL) refills.add(Refill(next.at, jump))
        }

        /** How much had been topped up by [at], optionally counting a refill there. */
        fun refilledAt(at: Instant, inclusive: Boolean): Double = refills.sumOf { refill ->
            val counts = refill.at.isBefore(at) || (inclusive && refill.at == at)
            if (counts) refill.amount.toDouble() else 0.0
        }

        val hours = sorted.map { Duration.between(origin, it.at).toMillis() / MILLIS_PER_HOUR }
        val consumed = sorted.map { refilledAt(it.at, true) + (100.0 - it.percent) }

        val meanX = hours.average()
        val meanY = consumed.average()
        var covariance = 0.0
        var variance = 0.0
        for (index in hours.indices) {
            covariance += (hours[index] - meanX) * (consumed[index] - meanY)
            variance += (hours[index] - meanX) * (hours[index] - meanX)
        }
        if (variance <= 0.0) return null

        val slope = covariance / variance
        val intercept = meanY - slope * meanX

        fun consumptionAt(at: Instant): Double =
            intercept + slope * (Duration.between(origin, at).toMillis() / MILLIS_PER_HOUR)

        // Both sides of every refill, so the line steps up where the level did.
        val events = (sorted.map { it.at } + refills.map { it.at }).distinct().sorted()
        val vertices = ArrayList<UsagePoint>(events.size + refills.size)
        for (at in events) {
            val before = 100.0 - (consumptionAt(at) - refilledAt(at, false))
            val after = 100.0 - (consumptionAt(at) - refilledAt(at, true))
            if (before != after) vertices.add(UsagePoint(at, before.toFloat().coerceIn(0f, 100f)))
            vertices.add(UsagePoint(at, after.toFloat().coerceIn(0f, 100f)))
        }

        return Fit(vertices, refills, -slope)
    }
}

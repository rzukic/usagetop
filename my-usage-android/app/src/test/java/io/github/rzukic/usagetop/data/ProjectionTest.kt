package io.github.rzukic.usagetop.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Duration
import java.time.Instant
import kotlin.math.abs

class ProjectionTest {

    private val t0: Instant = Instant.ofEpochMilli(1_700_000_000_000L)

    private fun at(hours: Double): Instant = t0.plusMillis((hours * 3_600_000).toLong())

    private fun at(hours: Long): Instant = at(hours.toDouble())

    private fun track(
        percent: Double,
        recent: Double?,
        usual: Double? = recent,
        resetAt: Instant? = null,
        period: Duration? = null,
        ease: Double = 3.0,
    ) = Projection.Track(
        recent = recent,
        usual = usual,
        percent = percent,
        at = t0,
        resetAt = resetAt,
        period = period,
        easeHours = ease,
    )

    private fun point(hours: Double, percent: Float) = UsagePoint(at(hours), percent)

    /** The sample closest to [target], since the line is drawn from fixed steps. */
    private fun percentAt(points: List<UsagePoint>, target: Instant): Float =
        points.minByOrNull { abs(Duration.between(it.at, target).toMillis()) }!!.percent

    @Test
    fun `each window falls at its own rate, not at the average of them`() {
        // One window is untouched, the other is being spent at 10 points an hour.
        // A single fitted rate would drop the pair by 10 an hour; the truth is 5.
        val points = Projection.combined(
            tracks = listOf(track(100.0, 0.0), track(50.0, 10.0)),
            from = t0,
            to = at(4),
        )

        assertEquals(75f, percentAt(points, t0), 0.5f)
        assertEquals(70f, percentAt(points, at(1)), 0.5f)
        assertEquals(65f, percentAt(points, at(2)), 0.5f)
    }

    @Test
    fun `a window that runs out stops pulling the average down`() {
        val points = Projection.combined(
            tracks = listOf(track(80.0, 0.0), track(20.0, 10.0)),
            from = t0,
            to = at(4),
        )

        assertEquals(50f, percentAt(points, t0), 0.5f)
        assertEquals(45f, percentAt(points, at(1)), 0.5f)
        assertEquals(40f, percentAt(points, at(3)), 0.5f)
        assertEquals(40f, percentAt(points, at(4)), 0.5f)
    }

    @Test
    fun `a reset puts the window back to full in one step`() {
        val points = Projection.path(track(50.0, 10.0, resetAt = at(2)), t0, at(4))

        assertEquals(40f, percentAt(points, at(1)), 0.5f)
        assertEquals(30f, percentAt(points, at(2).minusMillis(1)), 0.5f)
        assertEquals(100f, percentAt(points, at(2)), 0.5f)
        assertEquals(90f, percentAt(points, at(3)), 0.5f)
    }

    @Test
    fun `every later cycle refills too when the period is known`() {
        val track = track(50.0, 10.0, resetAt = at(2), period = Duration.ofHours(5))

        assertEquals(100.0, Projection.levelAt(track, at(7)), 0.001)
        assertEquals(80.0, Projection.levelAt(track, at(9)), 0.001)
    }

    @Test
    fun `the recent rate gives way to the usual one`() {
        // Ten points an hour lately, one point an hour usually.
        val track = track(100.0, recent = 10.0, usual = 1.0, ease = 3.0)

        assertEquals(10.0, Projection.rateAt(track, 0.0), 0.001)
        assertEquals(1.0, Projection.rateAt(track, 30.0), 0.01)
        // 1 * 3 + 9 * 3 * (1 - e^-1): well short of the 30 a straight line would drain.
        assertEquals(100.0 - 20.067, Projection.levelAt(track, at(3)), 0.01)
    }

    @Test
    fun `an idle spell does not stop the usual burn`() {
        val track = track(60.0, recent = 0.0, usual = 2.0)

        assertTrue(track.draining)
        // 2 * 24 drained, less the 2 * 3 the quiet start held back.
        assertEquals(18.0, Projection.levelAt(track, at(24)), 0.1)
        assertNotNull(Projection.emptyAt(track, at(100)))
    }

    @Test
    fun `either rate stands in for the other when missing`() {
        assertEquals(90.0, Projection.levelAt(track(100.0, recent = null, usual = 2.0), at(5)), 0.001)
        assertEquals(90.0, Projection.levelAt(track(100.0, recent = 2.0, usual = null), at(5)), 0.001)

        val unknown = track(70.0, recent = null, usual = null)
        assertFalse(unknown.measured)
        assertEquals(70.0, Projection.levelAt(unknown, at(50)), 0.001)
    }

    @Test
    fun `empty is when it runs dry unless the reset comes first`() {
        val empty = Projection.emptyAt(track(20.0, 10.0), at(100))
        assertNotNull(empty)
        assertEquals(2.0, Duration.between(t0, empty).toMillis() / 3_600_000.0, 0.01)

        assertNull(Projection.emptyAt(track(20.0, 10.0, resetAt = at(1)), at(100)))
        assertNull(Projection.emptyAt(track(20.0, 0.0), at(100)))
    }

    @Test
    fun `a burn rate leaves the refills out`() {
        // 100 -> 90, a refill to 100, then -> 95: fifteen points spent in three hours.
        val points = listOf(point(0.0, 100f), point(1.0, 90f), point(2.0, 100f), point(3.0, 95f))

        assertEquals(5.0, Projection.rateOver(points, Duration.ofDays(7), at(3))!!, 0.001)
        assertEquals(5.0, Projection.rateOver(points, Duration.ofHours(1), at(3))!!, 0.001)
        // The level holds from the last reading until now, so the hours since count too.
        assertEquals(2.5, Projection.rateOver(points, Duration.ofHours(2), at(4))!!, 0.001)
    }

    @Test
    fun `a burn rate needs half an hour of readings`() {
        val points = listOf(point(0.0, 100f), point(0.2, 90f))

        assertNull(Projection.rateOver(points, Duration.ofHours(3), at(0.25)))
        assertNull(Projection.rateOver(emptyList(), Duration.ofHours(3), at(1)))
    }

    @Test
    fun `the line starts where the recorded series ended`() {
        val points = Projection.combined(
            tracks = listOf(track(100.0, 0.0), track(50.0, 10.0)),
            from = t0,
            to = at(4),
            anchor = 40f,
        )

        assertEquals(40f, points.first().percent, 0.01f)
    }
}

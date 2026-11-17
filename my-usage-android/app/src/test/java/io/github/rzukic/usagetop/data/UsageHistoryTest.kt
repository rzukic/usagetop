package io.github.rzukic.usagetop.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Duration
import java.time.Instant

class UsageHistoryTest {

    private val t0: Instant = Instant.ofEpochMilli(1_700_000_000_000L)

    private fun at(minutes: Long): Instant = t0.plusSeconds(minutes * 60)

    @Test
    fun `quick flat refreshes do not keep the combined series at one point`() {
        var series = UsageHistory.appendCombined(emptyList(), at(0), 50f)
        assertEquals(1, series.size)

        // Refreshes two and four minutes later add nothing and must not
        // replace the first point either.
        series = UsageHistory.appendCombined(series, at(2), 50f)
        series = UsageHistory.appendCombined(series, at(4), 50f)
        assertEquals(listOf(at(0)), series.map { it.at })

        series = UsageHistory.appendCombined(series, at(6), 50f)
        assertEquals(listOf(at(0), at(6)), series.map { it.at })
    }

    @Test
    fun `a moved combined value replaces the reading a minute earlier`() {
        var series = UsageHistory.appendCombined(emptyList(), at(0), 50f)
        series = UsageHistory.appendCombined(series, at(1), 49f)
        assertEquals(49f, series.last().percent)
        assertEquals(at(1), series.last().at)
    }

    @Test
    fun `a reading that adds nothing hands the same list back`() {
        val series = UsageHistory.append(emptyList(), at(0), 50f, UsageHistory.RETAIN)

        // The caller uses identity to decide whether there is anything to save.
        assertSame(series, UsageHistory.append(series, at(2), 50f, UsageHistory.RETAIN))
    }

    @Test
    fun `a window keeps five weeks and thins out past the first`() {
        // Six weeks of quarter-hourly readings, the cadence the worker runs at.
        val now = t0.plus(Duration.ofDays(42))
        val points = (0..42 * 96).map { UsagePoint(at(15L * it), 50f) }

        val kept = UsageHistory.compact(points, now, UsageHistory.RETAIN)

        assertTrue(Duration.between(kept.first().at, now) <= UsageHistory.RETAIN)
        assertEquals(now, kept.last().at)

        // The newest week is untouched; the weeks before it are hourly.
        val recent = kept.filter { Duration.between(it.at, now) < Duration.ofDays(7) }
        val older = kept.filter { Duration.between(it.at, now) >= Duration.ofDays(8) }
        assertEquals(7 * 96, recent.size)
        assertTrue(older.zipWithNext().all { (a, b) -> Duration.between(a.at, b.at) >= Duration.ofHours(1) })
        assertTrue(kept.size < UsageHistory.MAX_POINTS)
    }

    @Test
    fun `a window's chart looks back as far as its cycle needs`() {
        fun span(label: String) = UsageHistory.chartSpan(UsageWindow(label, 50f))

        assertEquals(Duration.ofDays(7), span("5h"))
        assertEquals(Duration.ofDays(7), span("7d"))
        assertEquals(Duration.ofDays(7), span("Fable 7d"))
        assertEquals(Duration.ofDays(35), span("Monthly"))
        assertEquals(Duration.ofDays(35), span("Vibe"))
    }

    @Test
    fun `a label gives up its period only when it ends in a duration`() {
        assertEquals(Duration.ofHours(5), Projection.period("5h"))
        assertEquals(Duration.ofDays(7), Projection.period("Weekly"))
        assertEquals(Duration.ofDays(7), Projection.period("Fable 7d"))
        assertEquals(Duration.ofDays(30), Projection.period("Monthly"))

        // A version number is not a duration just because it ends in a digit
        // and a letter.
        assertEquals(null, Projection.period("GPT-5h"))

        // Mistral's budgets name no duration, but run on its monthly billing cycle.
        assertEquals(Duration.ofDays(30), Projection.period("Vibe"))
        assertEquals(Duration.ofDays(30), Projection.period("API"))
    }
}

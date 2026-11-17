package io.github.rzukic.usagetop.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Instant

class TrendlineTest {

    private val t0: Instant = Instant.ofEpochMilli(1_700_000_000_000L)

    private fun at(minutes: Long): Instant = t0.plusSeconds(minutes * 60)

    @Test
    fun `a straight drawdown needs no refills and the line follows it`() {
        val series = listOf(
            UsagePoint(at(0), 90f),
            UsagePoint(at(60), 80f),
            UsagePoint(at(120), 70f),
        )

        val fit = Trendline.fit(series)!!

        assertTrue(fit.refills.isEmpty())
        assertEquals(90f, fit.vertices.first().percent, 0.5f)
        assertEquals(70f, fit.vertices.last().percent, 0.5f)
    }

    @Test
    fun `a jump up is read as a refill and the trend steps with it`() {
        val series = listOf(
            UsagePoint(at(0), 80f),
            UsagePoint(at(60), 60f),
            UsagePoint(at(120), 40f),
            UsagePoint(at(180), 20f),
            UsagePoint(at(240), 100f),
            UsagePoint(at(300), 80f),
        )

        val fit = Trendline.fit(series)!!

        assertEquals(1, fit.refills.size)
        assertEquals(at(240), fit.refills.single().at)
        assertEquals(80f, fit.refills.single().amount, 0.01f)

        // Two vertices at the refill: the line drops to the pre-refill level and
        // jumps back up, so the trend stays parallel to the sawtooth.
        val atRefill = fit.vertices.filter { it.at == at(240) }
        assertEquals(2, atRefill.size)
        assertTrue(atRefill[1].percent > atRefill[0].percent)
    }

    @Test
    fun `the trend ignores the refill when measuring the burn rate`() {
        // 20 points an hour, reset once. Fitted naively the sawtooth would read as
        // almost flat; with the refill added back the slope is the real burn.
        val series = listOf(
            UsagePoint(at(0), 80f),
            UsagePoint(at(60), 60f),
            UsagePoint(at(120), 40f),
            UsagePoint(at(180), 20f),
            UsagePoint(at(240), 100f),
            UsagePoint(at(300), 80f),
            UsagePoint(at(360), 60f),
        )

        val fit = Trendline.fit(series)!!

        // Consumption rises across the whole series rather than falling back at
        // the reset, so the first vertex sits above the last.
        assertTrue(fit.vertices.first().percent > fit.vertices.last().percent)
    }

    @Test
    fun `the fitted rate is the burn per hour, and it falls`() {
        // 20 points an hour, no refills: the projection needs that number.
        val series = listOf(
            UsagePoint(at(0), 90f),
            UsagePoint(at(60), 70f),
            UsagePoint(at(120), 50f),
        )

        val fit = Trendline.fit(series)!!

        assertEquals(-20.0, fit.rate, 0.01)
    }

    @Test
    fun `one reading is not enough to fit a line`() {
        assertNull(Trendline.fit(listOf(UsagePoint(at(0), 50f))))
        assertNull(Trendline.fit(emptyList()))
    }
}

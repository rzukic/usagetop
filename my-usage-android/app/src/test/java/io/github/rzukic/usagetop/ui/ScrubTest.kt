package io.github.rzukic.usagetop.ui

import io.github.rzukic.usagetop.data.UsagePoint
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Instant

class ScrubTest {

    private val start = Instant.parse("2026-11-16T10:00:00Z")

    private fun at(minutes: Long) = start.plusSeconds(minutes * 60)

    private fun point(minutes: Long, percent: Float) = UsagePoint(at(minutes), percent)

    /** A level holds until the next reading, so the step is what is read out. */
    private val series = listOf(
        point(0, 100f),
        point(10, 80f),
        point(20, 60f),
    )

    @Test
    fun `a moment between readings reads the one before it`() {
        val level = Scrub.levelAt(series, emptyList(), at(15))!!

        assertEquals(80f, level.point.percent, 0.01f)
        assertFalse(level.projected)
    }

    @Test
    fun `a moment exactly on a reading reads that reading`() {
        assertEquals(60f, Scrub.levelAt(series, emptyList(), at(20))!!.point.percent, 0.01f)
    }

    @Test
    fun `before the first reading it reads the first`() {
        assertEquals(100f, Scrub.levelAt(series, emptyList(), at(-30))!!.point.percent, 0.01f)
    }

    @Test
    fun `past the last reading with no forecast it stays on the last`() {
        val level = Scrub.levelAt(series, emptyList(), at(90))!!

        assertEquals(60f, level.point.percent, 0.01f)
        assertFalse(level.projected)
    }

    @Test
    fun `past the last reading the forecast is what is read`() {
        val projection = listOf(point(20, 60f), point(40, 30f), point(60, 0f))

        val level = Scrub.levelAt(series, projection, at(50))!!

        assertEquals(30f, level.point.percent, 0.01f)
        assertTrue(level.projected)
    }

    @Test
    fun `a forecast that starts later than the finger leaves the last reading`() {
        val projection = listOf(point(40, 30f), point(60, 0f))

        val level = Scrub.levelAt(series, projection, at(30))!!

        assertEquals(60f, level.point.percent, 0.01f)
        assertFalse(level.projected)
    }

    @Test
    fun `nothing to read from is null rather than a guess`() {
        assertNull(Scrub.levelAt(emptyList(), emptyList(), at(0)))
    }
}

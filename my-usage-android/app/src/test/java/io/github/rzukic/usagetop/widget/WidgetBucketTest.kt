package io.github.rzukic.usagetop.widget

import io.github.rzukic.usagetop.data.UsageWindow
import org.junit.Assert.assertEquals
import org.junit.Test
import java.time.Instant

class WidgetBucketTest {

    private fun inHours(hours: Double): Instant =
        Instant.now().plusSeconds((hours * 3600).toLong())

    @Test
    fun `a healthy window in its final quarter is a forfeit`() {
        // 94% of a weekly budget with five hours left to spend it.
        assertEquals(
            "Use it or lose it",
            WidgetRenderer.bucketOf(UsageWindow("Fable 7d", 94f, inHours(5.4))),
        )
    }

    @Test
    fun `a healthy window with time to spare stays healthy`() {
        // 99% of a five-hour budget with three hours left: the window is not over yet.
        assertEquals("Healthy", WidgetRenderer.bucketOf(UsageWindow("5h", 99f, inHours(3.2))))
    }

    @Test
    fun `a five-hour window only nudges in its last seventy-five minutes`() {
        assertEquals("Healthy", WidgetRenderer.bucketOf(UsageWindow("5h", 90f, inHours(1.5))))
        assertEquals("Use it or lose it", WidgetRenderer.bucketOf(UsageWindow("5h", 90f, inHours(1.0))))
    }

    @Test
    fun `a window with nothing left is used up, not a forfeit`() {
        assertEquals("Used up", WidgetRenderer.bucketOf(UsageWindow("5h", 0f, inHours(1.0))))
    }

    @Test
    fun `a window already in the warning band is running out`() {
        assertEquals("Running out", WidgetRenderer.bucketOf(UsageWindow("7d", 19f, inHours(1.0))))
    }

    @Test
    fun `a window with no level is unavailable`() {
        assertEquals("Unavailable", WidgetRenderer.bucketOf(UsageWindow("Balance", null, null)))
    }

    @Test
    fun `a window with no reset time cannot be a forfeit`() {
        assertEquals("Healthy", WidgetRenderer.bucketOf(UsageWindow("7d", 94f, null)))
    }
}

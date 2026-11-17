package io.github.rzukic.usagetop.providers

import io.github.rzukic.usagetop.data.ProviderId
import io.github.rzukic.usagetop.data.ProviderUsage
import io.github.rzukic.usagetop.data.UsageState
import io.github.rzukic.usagetop.data.UsageWindow
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import java.time.Instant

class MuseProviderTest {

    private val now: Instant = Instant.ofEpochMilli(1_700_000_000_000L)

    /** What the repository keeps when Meta answers without `subs_usage`. */
    private fun stale(vararg windows: UsageWindow) = ProviderUsage(
        id = ProviderId.MUSE,
        state = UsageState.OK,
        windows = windows.toList(),
        message = "No usage reported",
        stale = true,
    )

    @Test
    fun `a window whose reset has passed is full again`() {
        val rolled = MuseProvider.rollOver(
            stale(
                UsageWindow("5h", 0f, now.minusSeconds(60)),
                UsageWindow("Weekly", 66f, now.plusSeconds(86_400)),
            ),
            now,
        )

        assertEquals(100f, rolled.windows[0].remainingPercent)
        assertNull(rolled.windows[0].resetAt)
        assertEquals(66f, rolled.windows[1].remainingPercent)
        assertEquals(now.plusSeconds(86_400), rolled.windows[1].resetAt)
    }

    @Test
    fun `a window that has not reset keeps its reading`() {
        val reading = stale(UsageWindow("5h", 0f, now.plusSeconds(600)))

        assertEquals(reading, MuseProvider.rollOver(reading, now))
    }

    @Test
    fun `a reading from this fetch is left alone`() {
        val fresh = stale(UsageWindow("5h", 0f, now.minusSeconds(60))).copy(stale = false)

        assertEquals(fresh, MuseProvider.rollOver(fresh, now))
    }

    @Test
    fun `another provider's stale reading is left alone`() {
        val other = stale(UsageWindow("5h", 0f, now.minusSeconds(60)))
            .copy(id = ProviderId.CLAUDE)

        assertEquals(other, MuseProvider.rollOver(other, now))
    }
}

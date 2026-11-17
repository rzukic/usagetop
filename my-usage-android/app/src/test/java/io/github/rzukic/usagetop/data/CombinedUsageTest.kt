package io.github.rzukic.usagetop.data

import org.junit.Assert.assertEquals
import org.junit.Test
import java.time.Instant

class CombinedUsageTest {

    private val t0: Instant = Instant.ofEpochMilli(1_700_000_000_000L)

    private fun at(hours: Long): Instant = t0.plusSeconds(hours * 3600)

    private fun usage(
        vararg windows: UsageWindow,
        id: ProviderId = ProviderId.MUSE,
    ) = ProviderUsage(
        id = id,
        state = UsageState.OK,
        windows = windows.toList(),
    )

    @Test
    fun `a five-hour window is not counted even when it resets near the weekly one`() {
        // Muse: the 5h window is empty and resets 22h before the weekly one, which
        // used to put it inside the same-tier tolerance and halve the average.
        val windows = CombinedUsage.longWindows(
            usage(
                UsageWindow("5h", 0f, at(1)),
                UsageWindow("Weekly", 66f, at(23)),
            ),
        )

        assertEquals(listOf("Weekly"), windows.map { it.label })
    }

    @Test
    fun `two windows resetting at the same instant are both counted`() {
        // Same billing date, so the same tier: neither is dropped for resetting
        // sooner than the other.
        val windows = CombinedUsage.longWindows(
            usage(
                UsageWindow("Monthly", 100f, at(500)),
                UsageWindow("Top-up", 84f, at(500)),
            ),
        )

        assertEquals(listOf("Monthly", "Top-up"), windows.map { it.label })
    }

    @Test
    fun `mistral's api credit is left out of the combined figure`() {
        // It is a separate pool that coding workflows cannot spend, so the row and
        // the combined figure both read the Vibe allowance alone.
        val mistral = usage(
            UsageWindow("API", 100f, at(500), countsTowardCombined = false),
            UsageWindow("Vibe", 82f, at(500)),
            id = ProviderId.MISTRAL,
        )

        val windows = CombinedUsage.longWindows(mistral)

        assertEquals(listOf("Vibe"), windows.map { it.label })
        assertEquals(82f, CombinedUsage.level(windows))
    }

    @Test
    fun `a model-scoped slice of the weekly limit is not counted twice`() {
        // Claude: Fable may use part of the weekly allowance, so its window is a
        // slice of the 7d one. Averaging both would weigh the same usage twice.
        val windows = CombinedUsage.longWindows(
            usage(
                UsageWindow("7d", 19f, at(72)),
                UsageWindow("Fable 7d", 100f, at(72), countsTowardCombined = false),
            ),
        )

        assertEquals(listOf("7d"), windows.map { it.label })
    }

    @Test
    fun `a provider that only exposes a short window still counts`() {
        val windows = CombinedUsage.longWindows(usage(UsageWindow("5h", 40f, at(2))))

        assertEquals(listOf("5h"), windows.map { it.label })
    }

    @Test
    fun `a subscription counts once however many windows it reports`() {
        val snapshot = Snapshot(
            usages = listOf(
                usage(UsageWindow("7d", 10f, at(72)), id = ProviderId.CLAUDE),
                usage(UsageWindow("7d", 30f, at(72)), id = ProviderId.CHATGPT),
            ),
            fetchedAt = t0,
        )

        assertEquals(20f, CombinedUsage.of(snapshot).percent)
        assertEquals(2, CombinedUsage.of(snapshot).providers)
    }
}

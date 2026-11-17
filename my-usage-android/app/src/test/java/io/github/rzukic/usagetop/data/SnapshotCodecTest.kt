package io.github.rzukic.usagetop.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Instant

class SnapshotCodecTest {

    @Test
    fun `a sub-limit survives the cache`() {
        val reset = Instant.parse("2026-11-11T19:59:59Z")
        val snapshot = Snapshot(
            usages = listOf(
                ProviderUsage(
                    id = ProviderId.CLAUDE,
                    state = UsageState.OK,
                    windows = listOf(
                        UsageWindow("7d", 19f, reset),
                        UsageWindow("Fable 7d", 100f, reset, countsTowardCombined = false),
                    ),
                ),
            ),
            fetchedAt = Instant.parse("2026-11-08T02:04:49Z"),
        )

        val decoded = SnapshotCodec.decode(SnapshotCodec.encode(snapshot))

        assertEquals(snapshot.usages.single().windows, decoded?.usages?.single()?.windows)
    }

    @Test
    fun `a cache written before the flag existed decodes as counting`() {
        val raw = """
            {"fetchedAt":"2026-11-08T02:04:49Z","usages":[{"id":"claude","state":"OK",
             "windows":[{"label":"7d","remainingPercent":19.0}]}]}
        """.trimIndent()

        val window = SnapshotCodec.decode(raw)?.usages?.single()?.windows?.single()

        assertTrue(window!!.countsTowardCombined)
    }
}

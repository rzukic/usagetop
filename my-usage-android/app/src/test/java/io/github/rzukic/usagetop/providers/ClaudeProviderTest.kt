package io.github.rzukic.usagetop.providers

import org.json.JSONArray
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Instant

class ClaudeProviderTest {

    private fun scoped(json: String) = ClaudeProvider().scoped(JSONArray(json))

    @Test
    fun `the fable weekly limit becomes a window of its own`() {
        val windows = scoped(
            """
            [
              {"kind":"session","group":"session","percent":9,"severity":"normal","is_active":false},
              {"kind":"weekly_all","group":"weekly","percent":68,"severity":"normal","is_active":false},
              {"kind":"weekly_scoped","group":"weekly","percent":100,"severity":"critical",
               "is_active":true,"resets_at":"2026-11-16T09:00:00Z",
               "scope":{"model":{"id":null,"display_name":"Fable"}}}
            ]
            """.trimIndent(),
        )

        val fable = windows.single()
        assertEquals("Fable 7d", fable.label)
        assertEquals(0f, fable.remainingPercent)
        assertEquals(Instant.parse("2026-11-16T09:00:00Z"), fable.resetAt)
        assertFalse(fable.countsTowardCombined)
    }

    @Test
    fun `a scoped limit that names no model is skipped`() {
        val windows = scoped(
            """[{"kind":"weekly_scoped","percent":40,"scope":{"model":{"display_name":""}}}]""",
        )

        assertTrue(windows.isEmpty())
    }

    @Test
    fun `a response without a limits array adds nothing`() {
        assertTrue(ClaudeProvider().scoped(null).isEmpty())
    }
}

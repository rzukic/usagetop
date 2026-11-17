package io.github.rzukic.usagetop.providers

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class MistralProviderTest {

    private fun budgets(json: String) = MistralProvider().budgets(JSONObject(json))

    @Test
    fun `the api credit is kept out of the combined figure`() {
        val windows = budgets(
            """
            {
              "api_budget": {"usage_percentage": 0, "initial_budget": 12.75,
                             "currency": "EUR", "reset_at": "2026-12-01T00:00:00Z"},
              "vibe_budget": {"usage_percentage": 18, "initial_budget": 127.5,
                              "currency": "EUR", "reset_at": "2026-12-01T00:00:00Z"}
            }
            """.trimIndent(),
        )

        val api = windows.single { it.label == "API" }
        val vibe = windows.single { it.label == "Vibe" }

        assertFalse(api.countsTowardCombined)
        assertTrue(vibe.countsTowardCombined)
        assertEquals(82f, vibe.remainingPercent)
        assertEquals("€104.55 of €127.50 left", vibe.detail)
    }

    @Test
    fun `a response with only the vibe budget still reports it`() {
        val windows = budgets(
            """{"vibe_budget":{"usage_percentage":50,"initial_budget":100,"currency":"EUR"}}""",
        )

        assertEquals(listOf("Vibe"), windows.map { it.label })
        assertEquals(50f, windows.single().remainingPercent)
    }
}

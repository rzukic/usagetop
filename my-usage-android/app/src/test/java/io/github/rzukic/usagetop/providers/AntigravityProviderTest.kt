package io.github.rzukic.usagetop.providers

import io.github.rzukic.usagetop.data.CombinedUsage
import io.github.rzukic.usagetop.data.ProviderId
import io.github.rzukic.usagetop.data.ProviderUsage
import io.github.rzukic.usagetop.data.UsageState
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Test
import java.time.Instant

/** The twin of AntigravityProviderTests in the C# suite. */
class AntigravityProviderTest {

    private val summary = """
        {
          "groups": [
            {"displayName": "Gemini Models", "buckets": [
              {"bucketId": "gemini-weekly", "window": "weekly", "resetTime": "2026-11-26T14:33:54Z", "remainingFraction": 0.9415697},
              {"bucketId": "gemini-5h", "window": "5h", "resetTime": "2026-11-19T19:33:54Z", "remainingFraction": 0.79549384}]},
            {"displayName": "Claude and GPT models", "buckets": [
              {"bucketId": "3p-weekly", "window": "weekly", "resetTime": "2026-11-26T15:59:58Z", "remainingFraction": 1},
              {"bucketId": "3p-5h", "window": "5h", "resetTime": "2026-11-19T20:59:58Z", "remainingFraction": 1}]},
            {"displayName": "Gemini 4 Argon", "buckets": [
              {"bucketId": "gemini-argon-wtu-weekly", "displayName": "Gemini 4 Argon Weekly Limit", "window": "weekly",
               "resetTime": "2026-11-26T15:59:58Z", "remainingFraction": 1}]}
          ]
        }
    """

    @Test
    fun everyGroupBecomesWindowsAndOnlyTheGeminiGroupCounts() {
        val windows = AntigravityProvider.windows(JSONObject(summary))

        assertEquals(listOf("7d", "5h", "Claude & GPT 7d", "Claude & GPT 5h", "Gemini 4 Argon 7d"), windows.map { it.label })
        assertEquals(94.15697f, windows[0].remainingPercent!!, 0.001f)
        assertEquals(Instant.parse("2026-11-26T14:33:54Z"), windows[0].resetAt)
        assertEquals(listOf(true, true, false, false, false), windows.map { it.countsTowardCombined })
    }

    @Test
    fun theCombinedFigureTakesTheGeminiWeek() {
        val usage = ProviderUsage(ProviderId.ANTIGRAVITY, UsageState.OK, windows = AntigravityProvider.windows(JSONObject(summary)))
        assertEquals(listOf("7d"), CombinedUsage.longWindows(usage).map { it.label })
    }

    @Test
    fun aSwitchedOffBucketOrOneWithoutAFigureIsSkipped() {
        val windows = AntigravityProvider.windows(JSONObject("""
            {"groups": [{"displayName": "Gemini Models", "buckets": [
              {"window": "weekly", "remainingFraction": 0.5, "disabled": true},
              {"window": "5h"},
              {"window": "weekly", "remainingFraction": 0}]}]}
        """))
        assertEquals(listOf(0f), windows.map { it.remainingPercent })
    }

    @Test
    fun thePlanIsThePaidTierAndAConsumerHasNoProjectOfItsOwn() {
        val consumer = JSONObject("""
            {"currentTier": {"id": "free-tier", "name": "Antigravity"}, "cloudaicompanionProject": "some-project",
             "gcpManaged": false, "paidTier": {"id": "g1-pro-tier", "name": "Google AI Pro"}}
        """)
        assertEquals("Google AI Pro" to null, AntigravityProvider.account(consumer))

        val managed = JSONObject("""{"currentTier": {"name": "Standard"}, "cloudaicompanionProject": "work-project", "gcpManaged": true}""")
        assertEquals("Standard" to "work-project", AntigravityProvider.account(managed))
    }
}

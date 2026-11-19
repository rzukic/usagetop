package io.github.rzukic.usagetop.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Duration
import java.time.Instant

class ServerClientTest {

    /** What `/api/state` sends; the same text the C# side is tested against. */
    private val answer = """
        {
          "serverTime": 1794428250000,
          "snapshot": {
            "fetchedAt": "2026-11-11T20:17:30.000Z",
            "combinedPercent": 46.2,
            "combinedProviders": 2,
            "usages": [
              {
                "id": "mistral", "state": "OK", "plan": "Education", "message": null,
                "fetchedAt": "2026-11-11T20:17:30.000Z", "stale": false, "transient": false,
                "windows": [
                  { "label": "Vibe", "remainingPercent": 77.25, "resetAt": "2026-12-01T00:00:00.000Z",
                    "detail": "left", "countsTowardCombined": true },
                  { "label": "API credit", "remainingPercent": null, "resetAt": null,
                    "detail": "12 left", "countsTowardCombined": false }
                ]
              },
              { "id": "kimi", "state": "ERROR", "message": "Token rejected", "plan": null,
                "fetchedAt": "2026-11-11T20:17:30.000Z", "stale": false, "transient": false, "windows": [] },
              { "id": "a-provider-from-the-future", "state": "OK", "windows": [] }
            ]
          },
          "combined": [[1794428000000, 47.0], [1794428250000, 46.2]],
          "series": { "mistral|Vibe": [[1794428250000, 77.25]] }
        }
    """.trimIndent()

    @Test
    fun `an answer becomes the snapshot and series the screens draw`() {
        val state = ServerState.parse(answer)!!

        assertEquals(Instant.parse("2026-11-11T20:17:30Z"), state.snapshot.fetchedAt)
        assertEquals(Instant.ofEpochMilli(1794428250000), state.serverTime)
        assertEquals(listOf(ProviderId.MISTRAL, ProviderId.KIMI), state.snapshot.usages.map { it.id })

        val mistral = state.snapshot.usages[0]
        assertEquals(UsageState.OK, mistral.state)
        assertEquals("Education", mistral.plan)
        assertEquals(
            UsageWindow("Vibe", 77.25f, Instant.parse("2026-12-01T00:00:00Z"), "left"),
            mistral.windows[0],
        )
        assertNull(mistral.windows[1].remainingPercent)
        assertNull(mistral.windows[1].resetAt)
        assertFalse(mistral.windows[1].countsTowardCombined)

        assertEquals(UsageState.ERROR, state.snapshot.usages[1].state)
        assertEquals("Token rejected", state.snapshot.usages[1].message)

        assertEquals(2, state.combined.size)
        assertEquals(46.2f, state.combined[1].percent)
        assertEquals(77.25f, state.series.getValue("mistral|Vibe").single().percent)
    }

    @Test
    fun `the server's hidden limits come with its state, and an older server sends none`() {
        val withList = answer.trimEnd().removeSuffix("}") + ""","hiddenLimits": ["antigravity|Claude & GPT 7d", "claude|Fable 7d"] }"""
        assertEquals(
            listOf("antigravity|Claude & GPT 7d", "claude|Fable 7d"),
            ServerState.parse(withList)!!.hiddenLimits,
        )
        assertEquals(emptyList<String>(), ServerState.parse(answer.trimEnd().removeSuffix("}") + ""","hiddenLimits": [] }""")!!.hiddenLimits)
        assertNull(ServerState.parse(answer)!!.hiddenLimits)
    }

    @Test
    fun `a server that has not polled yet has no state`() {
        assertNull(ServerState.parse("""{ "serverTime": 1, "snapshot": null, "combined": [], "series": {} }"""))
    }

    @Test
    fun `an address is tidied or turned down`() {
        assertEquals("http://100.64.0.7:8377", ServerClient.normalizeUrl("100.64.0.7:8377"))
        assertEquals("http://nas.local:8377", ServerClient.normalizeUrl(" http://nas.local:8377/ "))
        assertEquals("https://usage.example.org", ServerClient.normalizeUrl("https://usage.example.org"))
        assertNull(ServerClient.normalizeUrl(""))
        assertNull(ServerClient.normalizeUrl("   "))
        assertNull(ServerClient.normalizeUrl("ftp://nas.local"))
        assertNull(ServerClient.normalizeUrl("http://"))
        assertNull(ServerClient.normalizeUrl(null))
    }

    private val t0 = Instant.ofEpochMilli(1_700_000_000_000L)

    private fun every(minutes: Long, from: Long, count: Int, percent: Float) =
        (0 until count).map { UsagePoint(t0.plus(Duration.ofMinutes(from + it * minutes)), percent) }

    @Test
    fun `a first sync keeps what the app drew before the server existed`() {
        // The app has ten hours; the server only started five hours in.
        val ours = every(15, 0, 40, 50f)
        val theirs = every(5, 300, 60, 60f)
        val boundary = t0.plus(Duration.ofMinutes(300))

        val merged = UsageHistory.splice(ours, theirs, theirs.last().at, UsageHistory.RETAIN)

        assertEquals(ours.first(), merged.first())
        assertTrue(merged.filter { it.at.isBefore(boundary) }.all { it.percent == 50f })
        assertTrue(merged.filter { !it.at.isBefore(boundary) }.all { it.percent == 60f })
        assertEquals(theirs.last(), merged.last())
    }

    @Test
    fun `a top-up replaces the overlap instead of doubling it`() {
        val synced = every(5, 0, 24, 60f)

        // Ten minutes of overlap, then two new readings.
        val topUp = every(5, 105, 5, 60f)

        val merged = UsageHistory.splice(synced, topUp, topUp.last().at, UsageHistory.RETAIN)

        assertEquals(merged.size, merged.map { it.at }.distinct().size)
        assertEquals(26, merged.size)
        assertEquals(t0.plus(Duration.ofMinutes(125)), merged.last().at)
    }

    @Test
    fun `an empty answer changes nothing`() {
        val ours = every(15, 0, 4, 50f)

        assertEquals(ours, UsageHistory.splice(ours, emptyList(), ours.last().at, UsageHistory.RETAIN))
    }
}

class AccountsTest {

    @Test
    fun `an account key names a provider and a slot`() {
        assertEquals(ProviderId.CLAUDE to "", Accounts.parse("claude"))
        assertEquals(ProviderId.CLAUDE to "2", Accounts.parse("claude-2"))
        assertEquals("claude-2", Accounts.keyOf(ProviderId.CLAUDE, "2"))
        listOf(null, "", "nope", "nope-2", "claude-", "claude-2-3", "claude-A").forEach {
            assertNull(it, Accounts.parse(it))
        }
    }

    @Test
    fun `a second account from the server keeps its own name and history key`() {
        val state = ServerState.parse(
            """
            { "serverTime": 1, "combined": [], "series": { "claude-2|7d": [[1, 30.0]] },
              "snapshot": { "fetchedAt": "2026-11-12T10:00:00.000Z", "usages": [
                { "id": "claude", "account": "claude", "state": "OK", "windows": [] },
                { "id": "claude", "account": "claude-2", "accountName": "Work", "state": "OK", "windows": [] },
                { "id": "kimi", "account": "kimi-3", "accountName": null, "state": "OK", "windows": [] } ] } }
            """.trimIndent(),
        )!!

        assertEquals(listOf("claude", "claude-2", "kimi-3"), state.snapshot.usages.map { it.accountKey })
        assertEquals(listOf("Claude", "Claude · Work", "Kimi #3"), state.snapshot.usages.map { it.displayName })
        assertTrue(state.series.containsKey("claude-2|7d"))

        // And the app's own cache keeps all of it.
        val cached = SnapshotCodec.decode(SnapshotCodec.encode(state.snapshot))!!
        assertEquals(state.snapshot.usages.map { it.accountKey }, cached.usages.map { it.accountKey })
        assertEquals("Work", cached.usages[1].accountName)
    }

    @Test
    fun `each account counts as its own subscription`() {
        val snapshot = Snapshot(
            listOf(
                ProviderUsage(ProviderId.CLAUDE, UsageState.OK, listOf(UsageWindow("7d", 80f))),
                ProviderUsage(ProviderId.CLAUDE, UsageState.OK, listOf(UsageWindow("7d", 30f)), slot = "2"),
            ),
            Instant.EPOCH,
        )

        val combined = CombinedUsage.of(snapshot)
        assertEquals(2, combined.providers)
        assertEquals(55f, combined.percent!!, 0.01f)
        assertEquals(listOf("claude", "claude-2"), CombinedUsage.contributions(snapshot).map { it.account })
    }

    /** What `/api/credentials` sends: never a token, only whether one is there. */
    private val accounts = """
        [
          { "key": "claude", "provider": "claude", "name": null, "displayName": "Claude",
            "removable": false, "configured": true, "renews": true },
          { "key": "claude-2", "provider": "claude", "name": "Work", "displayName": "Claude (Work)",
            "removable": true, "configured": false, "renews": false },
          { "key": "mistral", "provider": "mistral", "displayName": "Mistral" }
        ]
    """.trimIndent()

    @Test
    fun `accounts say what is stored, not what it is`() {
        val parsed = ServerClient.parseAccounts(accounts)

        assertEquals(listOf("claude", "claude-2", "mistral"), parsed.map { it.key })
        assertEquals("Claude (Work)", parsed[1].displayName)
        assertEquals("Work", parsed[1].name)
        assertTrue(parsed[0].configured)
        assertTrue(parsed[0].renews)
        assertFalse(parsed[0].removable)
        assertTrue(parsed[1].removable)
        assertFalse(parsed[1].configured)
    }

    @Test
    fun `an account the server describes sparsely still reads`() {
        val parsed = ServerClient.parseAccounts(accounts)[2]

        assertEquals("Mistral", parsed.displayName)
        assertNull(parsed.name)
        assertFalse(parsed.configured)
        assertFalse(parsed.removable)
    }

    @Test
    fun `an answer that is not a list of accounts is refused`() {
        val problem = runCatching { ServerClient.parseAccounts("""{"error":"nope"}""") }.exceptionOrNull()

        assertTrue(problem is Exception)
    }

    @Test
    fun `a sign-in step says what to do next`() {
        val step = ServerSignInStep.parse(
            org.json.JSONObject(
                """{"kind":"device","url":"https://example.test/device","userCode":"ABCD-1234",
                    "instructions":"Type the code into the page."}""",
            ),
        )

        assertEquals("device", step.kind)
        assertEquals("ABCD-1234", step.userCode)
        assertEquals("Type the code into the page.", step.instructions)
    }
}

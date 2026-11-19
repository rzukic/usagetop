package io.github.rzukic.usagetop.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class HiddenLimitsTest {

    private val antigravity = ProviderUsage(
        ProviderId.ANTIGRAVITY, UsageState.OK,
        listOf(
            UsageWindow("7d", 64f),
            UsageWindow("5h", 82f),
            UsageWindow("Claude & GPT 7d", 100f),
        ),
    )
    private val work = ProviderUsage(
        ProviderId.CLAUDE, UsageState.OK,
        listOf(UsageWindow("7d", 40f), UsageWindow("Fable 7d", 60f)),
        slot = "2", accountName = "Work",
    )

    @Test
    fun `a limit is keyed the way history files its series`() {
        assertEquals("antigravity|Claude & GPT 7d", HiddenLimits.key(antigravity, antigravity.windows[2]))
        assertEquals("claude-2|Fable 7d", HiddenLimits.key(work, work.windows[1]))
        assertEquals("claude|Fable 7d", HiddenLimits.key("claude", "Fable 7d"))
    }

    @Test
    fun `a key splits back into its account and label`() {
        assertEquals("antigravity" to "Claude & GPT 7d", HiddenLimits.split("antigravity|Claude & GPT 7d"))
        assertEquals("claude-2" to "7d", HiddenLimits.split("claude-2|7d"))
        assertNull(HiddenLimits.split("claude"))
        assertNull(HiddenLimits.split("|7d"))
        assertNull(HiddenLimits.split("claude|"))
    }

    @Test
    fun `hiding takes a limit out of what is shown, and only that account's`() {
        val hidden = setOf("antigravity|Claude & GPT 7d", "claude|7d")
        assertEquals(listOf("7d", "5h"), HiddenLimits.visible(antigravity, hidden).map { it.label })
        // "claude|7d" is the first Claude account's; the Work account's 7d stays.
        assertEquals(listOf("7d", "Fable 7d"), HiddenLimits.visible(work, hidden).map { it.label })
        assertEquals(1, HiddenLimits.hiddenCount(antigravity, hidden))
        assertEquals(0, HiddenLimits.hiddenCount(work, hidden))
        assertTrue(HiddenLimits.isHidden(hidden, antigravity, antigravity.windows[2]))
        assertFalse(HiddenLimits.isHidden(hidden, antigravity, antigravity.windows[0]))
    }

    @Test
    fun `the server's list is in charge only while it keeps one`() {
        val server = setOf("antigravity|Claude & GPT 7d")
        val local = setOf("claude|Fable 7d")
        assertEquals(server, HiddenLimits.inUse(usesServer = true, server = server, local = local))
        // A server too old to keep a list: this device's own stays in charge.
        assertEquals(local, HiddenLimits.inUse(usesServer = true, server = null, local = local))
        // No server: the device's list, whatever a server once said.
        assertEquals(local, HiddenLimits.inUse(usesServer = false, server = server, local = local))
        // The server's list, even empty, is never merged with the device's.
        assertEquals(emptySet<String>(), HiddenLimits.inUse(usesServer = true, server = emptySet(), local = local))
    }

    @Test
    fun `the account's screen lists hidden limits after the shown ones, each group in its own order`() {
        val hidden = setOf("antigravity|7d")
        assertEquals(listOf("5h", "Claude & GPT 7d", "7d"), HiddenLimits.shownFirst(antigravity, hidden).map { it.label })
        assertEquals(antigravity.windows, HiddenLimits.shownFirst(antigravity, emptySet()))
    }

    @Test
    fun `nothing hidden shows everything`() {
        assertEquals(antigravity.windows, HiddenLimits.visible(antigravity, emptySet()))
    }
}

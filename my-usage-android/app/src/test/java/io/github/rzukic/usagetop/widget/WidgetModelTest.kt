package io.github.rzukic.usagetop.widget

import io.github.rzukic.usagetop.data.ProviderId
import io.github.rzukic.usagetop.data.ProviderUsage
import io.github.rzukic.usagetop.data.Snapshot
import io.github.rzukic.usagetop.data.UsageState
import io.github.rzukic.usagetop.data.UsageWindow
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Duration
import java.time.Instant

class WidgetModelTest {

    private val now = Instant.parse("2026-11-19T17:00:00Z")
    private fun inHours(h: Double) = now.plus(Duration.ofMinutes((h * 60).toLong()))

    // The Mac's sample Claude: 5h is being burned fast, Weekly is about on pace.
    private val claude = ProviderUsage(
        ProviderId.CLAUDE, UsageState.OK,
        listOf(
            UsageWindow("5h", 38f, inHours(2.2)),
            UsageWindow("7d", 31f, inHours(52.0)),
            UsageWindow("Fable 7d", 57f, inHours(52.0), countsTowardCombined = false),
        ),
        plan = "Max 20x",
    )
    private val kimi = ProviderUsage(
        ProviderId.KIMI, UsageState.OK,
        listOf(UsageWindow("Weekly", 11f, inHours(30.0))),
    )
    private val chatgpt = ProviderUsage(
        ProviderId.CHATGPT, UsageState.OK,
        listOf(UsageWindow("5h", 91f, inHours(3.6)), UsageWindow("7d", 72f, inHours(106.0))),
    )
    private val signedOut = ProviderUsage(
        ProviderId.CLAUDE, UsageState.ERROR, message = "Signed out", slot = "2", accountName = "Work",
    )
    private val notSetUp = ProviderUsage(ProviderId.MUSE, UsageState.NOT_CONFIGURED)

    private fun snapshot(vararg usages: ProviderUsage, at: Instant = now) = Snapshot(usages.toList(), at)

    @Test
    fun `the ring stands for the lowest window and the flame only for that window`() {
        val line = WidgetModel.line(claude, now)!!
        assertEquals("7d", line.window.label)
        assertEquals(31f, line.percent)
        // Weekly 31% with 2d 4h of 7d to go is about even pace: no flame on it.
        assertFalse(line.ahead)
        // 5h is mentioned, with the flame that belongs to it.
        val fiveHour = line.mentions.single()
        assertEquals("5h", fiveHour.window.label)
        assertTrue(fiveHour.ahead)
    }

    @Test
    fun `a window outside the combined figure is only mentioned once it needs attention`() {
        assertTrue(WidgetModel.line(claude, now)!!.mentions.none { it.window.label == "Fable 7d" })

        val lowFable = claude.copy(windows = claude.windows.map {
            if (it.label == "Fable 7d") it.copy(remainingPercent = 35f) else it
        })
        assertTrue(WidgetModel.line(lowFable, now)!!.mentions.any { it.window.label == "Fable 7d" })
    }

    @Test
    fun `accounts run from closest to empty, sign-in problems last, unset ones not at all`() {
        val accounts = WidgetModel.accounts(snapshot(chatgpt, signedOut, claude, notSetUp, kimi), now)
        assertEquals(listOf(ProviderId.KIMI, ProviderId.CLAUDE, ProviderId.CHATGPT), accounts.lines.map { it.usage.id })
        assertEquals(listOf("Work"), accounts.out.map { it.usage.accountName })
    }

    @Test
    fun `pace compares what is left with an even spread`() {
        // Half of a 5h window left with half the time to go: on pace.
        assertEquals(WidgetModel.Pace.ON, WidgetModel.pace(UsageWindow("5h", 50f, inHours(2.5)), now))
        assertEquals(WidgetModel.Pace.AHEAD, WidgetModel.pace(UsageWindow("5h", 20f, inHours(2.5)), now))
        assertEquals(WidgetModel.Pace.UNDER, WidgetModel.pace(UsageWindow("5h", 90f, inHours(2.5)), now))
        assertEquals(WidgetModel.Pace.EMPTY, WidgetModel.pace(UsageWindow("5h", 0f, inHours(2.5)), now))
        assertNull(WidgetModel.pace(UsageWindow("Vibe", 50f, null), now))
    }

    @Test
    fun `the next lift is the soonest long reset`() {
        val lift = WidgetModel.nextLift(snapshot(claude, kimi, chatgpt), now)!!
        assertEquals(ProviderId.KIMI, lift.usage.id)
        assertEquals(inHours(30.0), lift.at)
    }

    @Test
    fun `a reading only says its age once it is old`() {
        assertNull(WidgetModel.staleAge(snapshot(claude, at = now.minus(Duration.ofMinutes(20))), now))
        assertEquals(Duration.ofHours(2), WidgetModel.staleAge(snapshot(claude, at = now.minus(Duration.ofHours(2))), now))
    }

    @Test
    fun `windows are named the way the Mac names them`() {
        assertEquals("Weekly", WidgetModel.name(UsageWindow("7d")))
        assertEquals("Fable weekly", WidgetModel.name(UsageWindow("Fable 7d")))
        assertEquals("5h", WidgetModel.name(UsageWindow("5h")))
        assertEquals("Vibe", WidgetModel.name(UsageWindow("Vibe")))
        assertEquals("Week", WidgetModel.shortName(UsageWindow("7d")))
        assertEquals("Month", WidgetModel.shortName(UsageWindow("Vibe")))
        assertEquals("5h", WidgetModel.shortName(UsageWindow("5h")))
    }
}

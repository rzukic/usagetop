package io.github.rzukic.usagetop.widget

import org.junit.Assert.assertEquals
import org.junit.Test

class LimitPicksTest {

    private val appHidden = setOf("antigravity|Claude & GPT 7d", "claude|Fable 7d")

    @Test
    fun `without choices of its own a widget leaves out what the app hides`() {
        assertEquals(appHidden, LimitPicks.hiddenFor(appHidden, LimitPicks.Overrides()))
    }

    @Test
    fun `the widget's own choices win over the app's`() {
        val overrides = LimitPicks.Overrides(show = setOf("claude|Fable 7d"), hide = setOf("chatgpt|5h"))
        assertEquals(
            setOf("antigravity|Claude & GPT 7d", "chatgpt|5h"),
            LimitPicks.hiddenFor(appHidden, overrides),
        )
    }

    @Test
    fun `only what differs from the app is kept`() {
        val shown = mapOf(
            "antigravity|Claude & GPT 7d" to false, // hidden in the app, and here: nothing to keep
            "claude|Fable 7d" to true, // hidden in the app, shown here
            "chatgpt|5h" to false, // shown in the app, left out here
            "chatgpt|7d" to true, // shown in both: nothing to keep
        )
        assertEquals(
            LimitPicks.Overrides(show = setOf("claude|Fable 7d"), hide = setOf("chatgpt|5h")),
            LimitPicks.overridesFrom(appHidden, shown),
        )
    }

    @Test
    fun `a limit hidden in the app later is hidden in the widget too, unless the widget asked for it`() {
        val overrides = LimitPicks.Overrides(show = setOf("claude|Fable 7d"))
        val later = appHidden + "kimi|Weekly"
        assertEquals(setOf("antigravity|Claude & GPT 7d", "kimi|Weekly"), LimitPicks.hiddenFor(later, overrides))
    }
}

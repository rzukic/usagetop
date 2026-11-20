package io.github.rzukic.usagetop.widget

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class AccountBadgeTest {

    @Test
    fun `a badge shows up to two letters as written, as in every app`() {
        val cases = mapOf("RZ" to "RZ", "Work" to "W", "Claude Pro" to "CP", "work-laptop" to "wl", "#2" to "#2", "12" to "12", "x" to "x")
        cases.forEach { (label, badge) -> assertEquals(label, badge, AccountBadge.initials(label)) }
    }

    @Test
    fun `a label is the name or the slot, and none for a provider's only account`() {
        assertEquals("Work", AccountBadge.label(" Work ", "2"))
        assertEquals("#2", AccountBadge.label(null, "2"))
        assertNull(AccountBadge.label(null, ""))
        assertEquals("#2", AccountBadge.label("claude-2"))
        assertNull(AccountBadge.label("claude"))
    }
}

package io.github.rzukic.usagetop.widget

import io.github.rzukic.usagetop.data.Accounts
import io.github.rzukic.usagetop.data.ProviderUsage

/**
 * An account's initials in a pill, as the other apps' rails and the Mac's menu
 * bar show them, so two accounts of one provider can be told apart where only
 * its mark is drawn: "RZ", "W" for "Work", "#2" for a second one without a name.
 */
object AccountBadge {

    /**
     * The account's own part of its name, for places that already show the
     * provider's mark; null for a provider's only, unnamed account.
     */
    fun label(name: String?, slot: String): String? =
        name?.trim()?.takeIf { it.isNotEmpty() } ?: if (slot.isNotEmpty()) "#$slot" else null

    fun label(usage: ProviderUsage): String? = label(usage.accountName, usage.slot)

    /** For an account the snapshot does not hold (yet): only its slot is known. */
    fun label(accountKey: String): String? = Accounts.parse(accountKey)?.second?.let { label(null, it) }

    /**
     * Up to two letters, in the case they were written in: the first of each of
     * the first two words, a short name as it is, a number as it is, and of one
     * longer word its first letter. The rule every app follows.
     */
    fun initials(label: String): String {
        val words = label.split(' ', '-', '_', '·', '\t').filter { it.isNotEmpty() }
        if (words.size >= 2) return "${words[0][0]}${words[1][0]}"
        val word = words.firstOrNull() ?: return ""
        if (word.length <= 2) return word
        return if (word[0].isDigit() || word[0] == '#') word.take(3) else word.take(1)
    }
}

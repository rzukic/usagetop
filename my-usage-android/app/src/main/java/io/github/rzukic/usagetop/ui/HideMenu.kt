package io.github.rzukic.usagetop.ui

import android.view.Gravity
import android.view.View
import android.widget.PopupMenu
import androidx.core.view.ViewCompat
import io.github.rzukic.usagetop.R

/**
 * Hiding a limit without a control on every row: a long press opens a menu with
 * the one thing to do, "Hide 7d from the overview" or "Show 7d in the overview".
 * TalkBack gets the same as a custom action, so it needs no long press.
 */
object HideMenu {

    fun attach(view: View, label: String, hidden: Boolean, toggle: () -> Unit) {
        val context = view.context
        val item = context.getString(if (hidden) R.string.limit_show_in_overview else R.string.limit_hide_from_overview, label)
        view.isLongClickable = true
        view.setOnLongClickListener {
            PopupMenu(context, view, Gravity.END).apply {
                menu.add(item)
                setOnMenuItemClickListener { toggle(); true }
                show()
            }
            true
        }
        ViewCompat.addAccessibilityAction(
            view,
            context.getString(if (hidden) R.string.limit_show_action else R.string.limit_hide_action),
        ) { _, _ -> toggle(); true }
    }
}

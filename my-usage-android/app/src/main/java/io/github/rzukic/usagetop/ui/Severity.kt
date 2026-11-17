package io.github.rzukic.usagetop.ui

import android.content.Context
import androidx.core.content.ContextCompat
import io.github.rzukic.usagetop.R

/** One place for the rule that a limit is blue until it warns, then orange, then red. */
object Severity {

    fun color(context: Context, percent: Float): Int = ContextCompat.getColor(
        context,
        when {
            percent <= 15f -> R.color.meter_critical
            percent <= 40f -> R.color.meter_warning
            else -> R.color.meter_normal
        },
    )

    /** Only a warning or a critical figure is coloured; a healthy one stays quiet. */
    fun textColor(context: Context, percent: Float): Int = ContextCompat.getColor(
        context,
        when {
            percent <= 15f -> R.color.meter_critical
            percent <= 40f -> R.color.meter_warning
            else -> R.color.text_primary
        },
    )
}

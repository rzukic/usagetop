package io.github.rzukic.usagetop.widget

import android.app.AlarmManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.os.PowerManager

/**
 * The widget's countdowns are text, so they only move when the widget is redrawn.
 * A refresh every fifteen minutes left "22m" sitting there long after it was wrong,
 * so a one-minute alarm redraws from the cache instead — no network, no provider
 * calls, just the same snapshot with fresh arithmetic.
 */
object WidgetTicker {

    private const val MINUTE = 60_000L

    /** Replaces any pending tick, so repeated renders never stack alarms up. */
    fun schedule(context: Context) {
        val manager = context.getSystemService(AlarmManager::class.java) ?: return
        manager.set(
            AlarmManager.RTC,
            System.currentTimeMillis() + MINUTE,
            pending(context),
        )
    }

    fun cancel(context: Context) {
        context.getSystemService(AlarmManager::class.java)?.cancel(pending(context))
    }

    /** Nobody is reading a countdown on a dark screen, so the tick skips the redraw. */
    fun isAwake(context: Context): Boolean =
        context.getSystemService(PowerManager::class.java)?.isInteractive ?: true

    private fun pending(context: Context): PendingIntent {
        val intent = Intent(context, UsageWidgetProvider::class.java)
            .setAction(UsageWidgetProvider.ACTION_TICK)
        return PendingIntent.getBroadcast(
            context,
            4,
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
    }
}

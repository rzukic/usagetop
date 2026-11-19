package io.github.rzukic.usagetop.widget

import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import io.github.rzukic.usagetop.R
import io.github.rzukic.usagetop.data.UsageRepository
import io.github.rzukic.usagetop.work.RefreshWorker

class UsageWidgetProvider : AppWidgetProvider() {

    override fun onUpdate(
        context: Context,
        appWidgetManager: AppWidgetManager,
        appWidgetIds: IntArray,
    ) {
        render(context)
        RefreshWorker.enqueueNow(context)
    }

    override fun onReceive(context: Context, intent: Intent) {
        super.onReceive(context, intent)
        when (intent.action) {
            ACTION_REFRESH -> RefreshWorker.enqueueNow(context)
            ACTION_TICK -> {
                if (WidgetTicker.isAwake(context)) render(context)
                WidgetTicker.schedule(context)
            }
        }
    }

    /** Resized: before Android 12 the size decides which layout it gets, so draw again. */
    override fun onAppWidgetOptionsChanged(
        context: Context,
        appWidgetManager: AppWidgetManager,
        appWidgetId: Int,
        newOptions: android.os.Bundle,
    ) {
        render(context)
    }

    /** A widget taken off the home screen forgets its own choice of limits. */
    override fun onDeleted(context: Context, appWidgetIds: IntArray) {
        appWidgetIds.forEach { LimitPicks.forget(context, it) }
    }

    override fun onDisabled(context: Context) {
        super.onDisabled(context)
        // The other widgets share the tick; stop it only when none is left.
        val manager = AppWidgetManager.getInstance(context)
        if (manager == null || (RingsWidgetProvider.ids(context, manager).isEmpty() &&
                TotalWidgetProvider.ids(context, manager).isEmpty())
        ) {
            WidgetTicker.cancel(context)
        }
    }

    companion object {
        const val ACTION_REFRESH = "io.github.rzukic.usagetop.ACTION_REFRESH"
        const val ACTION_TICK = "io.github.rzukic.usagetop.ACTION_TICK"

        /** Pushes the cached snapshot into every placed widget, both kinds. */
        fun render(context: Context) {
            val manager = AppWidgetManager.getInstance(context) ?: return
            val ids = manager.getAppWidgetIds(
                ComponentName(context, UsageWidgetProvider::class.java)
            )
            val rings = RingsWidgetProvider.ids(context, manager)
            val totals = TotalWidgetProvider.ids(context, manager)
            if (ids.isEmpty() && rings.isEmpty() && totals.isEmpty()) return

            val snapshot = UsageRepository(context).cached()
            ids.forEach { id -> manager.updateAppWidget(id, WidgetRenderer.build(context, snapshot, manager, id)) }
            if (ids.isNotEmpty()) manager.notifyAppWidgetViewDataChanged(ids, R.id.widget_list)
            RingsWidgetProvider.render(context, manager, snapshot)
            TotalWidgetProvider.render(context, manager, snapshot)
            WidgetTicker.schedule(context)
        }
    }
}

package io.github.rzukic.usagetop.widget

import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.ComponentName
import android.content.Context
import io.github.rzukic.usagetop.data.Snapshot

/**
 * The combined figure alone, at 2x2, as its own entry in the widget list. The
 * usage widget shows the same when shrunk to 2x2, but a launcher lists each
 * widget once, at its starting size, so the small one would never be found.
 */
class TotalWidgetProvider : AppWidgetProvider() {

    override fun onUpdate(context: Context, appWidgetManager: AppWidgetManager, appWidgetIds: IntArray) {
        UsageWidgetProvider.render(context)
    }

    companion object {
        fun ids(context: Context, manager: AppWidgetManager): IntArray =
            manager.getAppWidgetIds(ComponentName(context, TotalWidgetProvider::class.java))

        fun render(context: Context, manager: AppWidgetManager, snapshot: Snapshot?) {
            val ids = ids(context, manager)
            if (ids.isEmpty()) return
            val views = WidgetRenderer.squared { WidgetRenderer.small(context, snapshot) }
            manager.updateAppWidget(ids, views)
        }
    }
}

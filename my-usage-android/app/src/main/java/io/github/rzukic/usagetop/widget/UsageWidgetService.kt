package io.github.rzukic.usagetop.widget

import android.content.Context
import android.content.Intent
import android.widget.RemoteViews
import android.widget.RemoteViewsService
import io.github.rzukic.usagetop.data.UsageRepository

/** Backs the widget's scrolling list. */
class UsageWidgetService : RemoteViewsService() {
    override fun onGetViewFactory(intent: Intent): RemoteViewsFactory =
        UsageViewsFactory(applicationContext)
}

private class UsageViewsFactory(private val context: Context) :
    RemoteViewsService.RemoteViewsFactory {

    private var entries: List<WidgetRenderer.Entry> = emptyList()

    override fun onCreate() = Unit

    override fun onDataSetChanged() {
        entries = WidgetRenderer.entries(UsageRepository(context).cached())
    }

    override fun onDestroy() {
        entries = emptyList()
    }

    override fun getCount(): Int = entries.size

    /** Two layouts: a bucket heading and a window row. */
    override fun getViewTypeCount(): Int = 2

    override fun getItemId(position: Int): Long = position.toLong()

    override fun hasStableIds(): Boolean = false

    override fun getLoadingView(): RemoteViews? = null

    override fun getViewAt(position: Int): RemoteViews = when (val entry = entries.getOrNull(position)) {
        is WidgetRenderer.Entry.Group -> WidgetRenderer.groupViews(context, entry.label)
        is WidgetRenderer.Entry.Row -> WidgetRenderer.rowViews(context, entry.usage, entry.window)
        null -> WidgetRenderer.groupViews(context, "")
    }
}

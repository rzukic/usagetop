package io.github.rzukic.usagetop.widget

import android.content.Context
import android.content.Intent
import android.widget.RemoteViews
import android.widget.RemoteViewsService
import io.github.rzukic.usagetop.data.UsageRepository

/** Backs the 4x4 widget's list of accounts. */
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

    /** Two layouts: an account with its ring, and one that needs a sign-in. */
    override fun getViewTypeCount(): Int = 2

    override fun getItemId(position: Int): Long = position.toLong()

    override fun hasStableIds(): Boolean = false

    override fun getLoadingView(): RemoteViews? = null

    override fun getViewAt(position: Int): RemoteViews = when (val entry = entries.getOrNull(position)) {
        is WidgetRenderer.Entry.Line -> WidgetRenderer.rowViews(context, entry.line, compact = false)
        is WidgetRenderer.Entry.Out -> WidgetRenderer.outViews(context, entry.out)
        // The list shrank between getCount and here; an empty sign-in row is harmless.
        null -> RemoteViews(context.packageName, io.github.rzukic.usagetop.R.layout.widget_account_out)
    }
}

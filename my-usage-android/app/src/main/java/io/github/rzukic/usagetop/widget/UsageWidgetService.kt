package io.github.rzukic.usagetop.widget

import android.appwidget.AppWidgetManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.widget.RemoteViews
import android.widget.RemoteViewsService
import io.github.rzukic.usagetop.data.UsageRepository

/** Backs the 4x4 widget's list of accounts. */
class UsageWidgetService : RemoteViewsService() {
    override fun onGetViewFactory(intent: Intent): RemoteViewsFactory = UsageViewsFactory(
        applicationContext,
        intent.getIntExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, AppWidgetManager.INVALID_APPWIDGET_ID)
            .takeIf { it != AppWidgetManager.INVALID_APPWIDGET_ID },
    )

    companion object {
        /**
         * The list of one placed widget, which may show other limits than the next.
         * Each needs an intent of its own: Android keeps one list per distinct intent.
         */
        fun intent(context: Context, widgetId: Int?): Intent =
            Intent(context, UsageWidgetService::class.java).apply {
                if (widgetId != null) putExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, widgetId)
                data = Uri.parse(toUri(Intent.URI_INTENT_SCHEME))
            }
    }
}

private class UsageViewsFactory(private val context: Context, private val widgetId: Int?) :
    RemoteViewsService.RemoteViewsFactory {

    private var entries: List<WidgetRenderer.Entry> = emptyList()

    override fun onCreate() = Unit

    override fun onDataSetChanged() {
        entries = WidgetRenderer.entries(context, UsageRepository(context).cached(), widgetId)
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

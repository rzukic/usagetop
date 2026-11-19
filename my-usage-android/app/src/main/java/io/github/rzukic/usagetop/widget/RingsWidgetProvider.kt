package io.github.rzukic.usagetop.widget

import android.app.PendingIntent
import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.view.View
import android.widget.RemoteViews
import io.github.rzukic.usagetop.R
import io.github.rzukic.usagetop.data.Accounts
import io.github.rzukic.usagetop.data.Snapshot
import io.github.rzukic.usagetop.data.UsageRepository
import io.github.rzukic.usagetop.ui.ProviderArt
import io.github.rzukic.usagetop.ui.ProviderDetailActivity
import kotlin.math.roundToInt

/**
 * The rings widget: one to four windows you choose, at 2x2. One pick is a
 * single large ring; more share the grid. The picks are the widget's settings
 * (long-press › Settings from Android 12; when placing it before that).
 */
class RingsWidgetProvider : AppWidgetProvider() {

    override fun onUpdate(context: Context, appWidgetManager: AppWidgetManager, appWidgetIds: IntArray) {
        UsageWidgetProvider.render(context)
    }

    override fun onDeleted(context: Context, appWidgetIds: IntArray) {
        appWidgetIds.forEach { RingPicks.forget(context, it) }
    }

    companion object {

        fun ids(context: Context, manager: AppWidgetManager): IntArray =
            manager.getAppWidgetIds(ComponentName(context, RingsWidgetProvider::class.java))

        /** Draws every placed rings widget from [snapshot]. */
        fun render(context: Context, manager: AppWidgetManager, snapshot: Snapshot?) {
            ids(context, manager).forEach { id -> manager.updateAppWidget(id, build(context, snapshot, id)) }
        }

        fun build(context: Context, snapshot: Snapshot?, widgetId: Int): RemoteViews =
            build(context, snapshot, RingPicks.read(context, widgetId) ?: RingPicks.defaults(snapshot))

        fun build(context: Context, snapshot: Snapshot?, picks: List<RingPicks.Pick>): RemoteViews =
            WidgetRenderer.squared { side -> plain(context, snapshot, picks, side) }

        /**
         * One version, its panel filling whatever it is given. Below [COMPACT_BELOW]
         * dp across, four rings and their names only fit smaller.
         */
        fun plain(context: Context, snapshot: Snapshot?, picks: List<RingPicks.Pick>, side: Float? = null): RemoteViews {
            val rings = RingPicks.resolve(picks, snapshot)
            val stale = WidgetModel.staleAge(snapshot) != null
            val compact = side != null && side < COMPACT_BELOW
            return if (rings.size == 1) one(context, rings.single(), stale) else grid(context, rings, stale, compact)
        }

        /** The regular grid needs about 165 dp: two rows of a 50 dp ring and its name. */
        private const val COMPACT_BELOW = 165f

        private fun one(context: Context, ring: RingPicks.Ring, stale: Boolean): RemoteViews {
            val views = RemoteViews(context.packageName, R.layout.widget_rings_one)
            val percent = ring.window?.remainingPercent
            WidgetArt.setRing(context, views, R.id.ring_track, R.id.ring_level, 104f, 7f, percent)
            logo(ring)?.let { views.setImageViewResource(R.id.one_logo, it) }
            views.setTextViewText(R.id.hero_value, percent?.let { "${it.roundToInt()}%" } ?: "—")
            WidgetArt.textColor(context, views, R.id.hero_value, percent?.let { WidgetArt.figureColor(it) } ?: R.color.widget_ink_secondary)

            val window = ring.window
            val name = window?.let { WidgetModel.name(it) } ?: ring.pick.label
            val ahead = window != null && WidgetModel.pace(window, java.time.Instant.now()) == WidgetModel.Pace.AHEAD
            views.setViewVisibility(R.id.one_flame, if (ahead) View.VISIBLE else View.GONE)

            if (stale) {
                // Old data: the age takes the countdown's place.
                views.setTextViewText(R.id.one_window, "$name · ")
                views.setViewVisibility(R.id.one_reset, View.GONE)
                views.setViewVisibility(R.id.stale, View.VISIBLE)
                views.setTextViewText(R.id.stale, staleText(context))
            } else {
                views.setViewVisibility(R.id.stale, View.GONE)
                val reset = window?.resetAt
                views.setTextViewText(R.id.one_window, if (reset == null) name else "$name · ")
                views.setViewVisibility(R.id.one_reset, if (reset == null) View.GONE else View.VISIBLE)
                WidgetRenderer.setReset(views, R.id.one_reset, reset)
            }
            views.setOnClickPendingIntent(R.id.widget_root, detailIntent(context, ring))
            return views
        }

        private val CELLS = listOf(
            Cell(R.id.cell1, R.id.cell1_track, R.id.cell1_level, R.id.cell1_value, R.id.cell1_logo, R.id.cell1_name),
            Cell(R.id.cell2, R.id.cell2_track, R.id.cell2_level, R.id.cell2_value, R.id.cell2_logo, R.id.cell2_name),
            Cell(R.id.cell3, R.id.cell3_track, R.id.cell3_level, R.id.cell3_value, R.id.cell3_logo, R.id.cell3_name),
            Cell(R.id.cell4, R.id.cell4_track, R.id.cell4_level, R.id.cell4_value, R.id.cell4_logo, R.id.cell4_name),
        )

        private data class Cell(val root: Int, val track: Int, val level: Int, val value: Int, val logo: Int, val name: Int)

        private fun grid(context: Context, rings: List<RingPicks.Ring>, stale: Boolean, compact: Boolean): RemoteViews {
            val views = RemoteViews(context.packageName, if (compact) R.layout.widget_rings_compact else R.layout.widget_rings)
            CELLS.forEachIndexed { index, cell ->
                val ring = rings.getOrNull(index)
                // Three rings: the third sits centred under the first two.
                views.setViewVisibility(cell.root, if (ring == null) View.GONE else View.VISIBLE)
                if (ring == null) return@forEachIndexed
                val percent = ring.window?.remainingPercent
                if (compact) {
                    WidgetArt.setRing(context, views, cell.track, cell.level, 38f, 3.6f, percent)
                } else {
                    WidgetArt.setRing(context, views, cell.track, cell.level, 50f, 4.5f, percent)
                }
                views.setTextViewText(cell.value, percent?.let { "${it.roundToInt()}%" } ?: "—")
                WidgetArt.textColor(context, views, cell.value, percent?.let { WidgetArt.figureColor(it) } ?: R.color.widget_ink_secondary)
                logo(ring)?.let { views.setImageViewResource(cell.logo, it) }
                views.setTextViewText(cell.name, ring.window?.let { WidgetModel.shortName(it) } ?: ring.pick.label)
            }
            views.setViewVisibility(R.id.row2, if (rings.size > 2) View.VISIBLE else View.GONE)
            // Old data fades the rings rather than crowding them with a chip.
            views.setFloat(R.id.grid, "setAlpha", if (stale) 0.42f else 1f)
            views.setOnClickPendingIntent(R.id.widget_root, WidgetRenderer.openIntent(context))
            return views
        }

        private fun logo(ring: RingPicks.Ring): Int? {
            val id = ring.usage?.id ?: Accounts.parse(ring.pick.account)?.first ?: return null
            return ProviderArt.logo(id)
        }

        private fun staleText(context: Context): String {
            val age = WidgetModel.staleAge(UsageRepository(context).cached()) ?: return ""
            return context.getString(R.string.widget_old, io.github.rzukic.usagetop.ui.Format.span(age).substringBefore(' '))
        }

        private fun detailIntent(context: Context, ring: RingPicks.Ring): PendingIntent {
            val intent = Intent(context, ProviderDetailActivity::class.java)
                .putExtra(ProviderDetailActivity.EXTRA_PROVIDER, ring.pick.account)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
            return PendingIntent.getActivity(
                context, ring.pick.account.hashCode(), intent,
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
            )
        }
    }
}

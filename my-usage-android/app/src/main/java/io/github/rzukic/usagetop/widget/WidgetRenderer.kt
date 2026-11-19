package io.github.rzukic.usagetop.widget

import android.app.PendingIntent
import android.appwidget.AppWidgetManager
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.SystemClock
import android.util.SizeF
import android.view.View
import android.widget.RemoteViews
import io.github.rzukic.usagetop.R
import io.github.rzukic.usagetop.data.CombinedUsage
import io.github.rzukic.usagetop.data.Projection
import io.github.rzukic.usagetop.data.Snapshot
import io.github.rzukic.usagetop.data.UsageHistory
import io.github.rzukic.usagetop.ui.Format
import io.github.rzukic.usagetop.ui.MainActivity
import io.github.rzukic.usagetop.ui.ProviderArt
import io.github.rzukic.usagetop.ui.ProviderDetailActivity
import java.time.Duration
import java.time.Instant
import kotlin.math.roundToInt

/**
 * The usagetop widget in the Mac panel's look, at three sizes:
 * - 2x2: the combined figure alone, in a ring;
 * - 4x2: the combined figure on one line, then the two accounts closest to empty;
 * - 4x4: the combined figure with its week and the next lift, then every account
 *   in a list that scrolls when there are more than fit.
 */
object WidgetRenderer {

    /** Wide enough for rows, tall enough for the list; between launcher cells. */
    private const val WIDE_DP = 220f
    private const val TALL_DP = 220f

    /** The 4x2 shows this many accounts; it does not scroll. */
    private const val MEDIUM_ROWS = 2

    private val SPARK_SPAN: Duration = Duration.ofDays(7)

    /** A list item: an account with a ring, or one that needs a sign-in. */
    sealed interface Entry {
        data class Line(val line: WidgetModel.Line) : Entry
        data class Out(val out: WidgetModel.Out) : Entry
    }

    fun entries(snapshot: Snapshot?): List<Entry> {
        val accounts = WidgetModel.accounts(snapshot)
        return accounts.lines.map { Entry.Line(it) } + accounts.out.map { Entry.Out(it) }
    }

    /**
     * The views for one placed widget. From Android 12 the launcher gets every
     * size and picks by itself as the widget is resized; before, the size it
     * reports now decides.
     */
    fun build(context: Context, snapshot: Snapshot?, manager: AppWidgetManager, id: Int): RemoteViews {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            return RemoteViews(
                mapOf(
                    SizeF(0f, 0f) to small(context, snapshot),
                    SizeF(WIDE_DP, 0f) to medium(context, snapshot),
                    SizeF(WIDE_DP, TALL_DP) to large(context, snapshot),
                ),
            )
        }
        val options = manager.getAppWidgetOptions(id)
        val width = options.getInt(AppWidgetManager.OPTION_APPWIDGET_MIN_WIDTH)
        val height = options.getInt(AppWidgetManager.OPTION_APPWIDGET_MAX_HEIGHT)
        return when {
            width >= WIDE_DP && height >= TALL_DP -> large(context, snapshot)
            width >= WIDE_DP -> medium(context, snapshot)
            else -> small(context, snapshot)
        }
    }

    fun small(context: Context, snapshot: Snapshot?): RemoteViews {
        val views = RemoteViews(context.packageName, R.layout.widget_small)
        val combined = snapshot?.let { CombinedUsage.of(it) }
        bindHero(context, views, combined?.percent, sizeDp = 112f, strokeDp = 7f)
        bindStale(context, views, snapshot)
        views.setOnClickPendingIntent(R.id.widget_root, openIntent(context))
        return views
    }

    fun medium(context: Context, snapshot: Snapshot?): RemoteViews {
        val views = RemoteViews(context.packageName, R.layout.widget_medium)
        val combined = snapshot?.let { CombinedUsage.of(it) }
        bindHero(context, views, combined?.percent, sizeDp = 30f, strokeDp = 3.2f)
        views.setTextViewText(
            R.id.hero_caption,
            context.getString(R.string.widget_short_caption, combined?.providers ?: 0),
        )
        bindStale(context, views, snapshot)

        views.removeAllViews(R.id.rows)
        WidgetModel.accounts(snapshot).lines.take(MEDIUM_ROWS).forEach { line ->
            views.addView(R.id.rows, rowViews(context, line, compact = true))
        }
        views.setOnClickPendingIntent(R.id.widget_root, openIntent(context))
        return views
    }

    fun large(context: Context, snapshot: Snapshot?): RemoteViews {
        val views = RemoteViews(context.packageName, R.layout.widget_large)
        val now = Instant.now()
        val combined = snapshot?.let { CombinedUsage.of(it) }
        bindHero(context, views, combined?.percent, sizeDp = 50f, strokeDp = 4.5f)
        views.setTextViewText(
            R.id.hero_caption,
            when (val n = combined?.providers ?: 0) {
                1 -> context.getString(R.string.widget_across_one)
                else -> context.getString(R.string.widget_across, n)
            },
        )
        bindStale(context, views, snapshot)

        val spark = snapshot?.let { spark(context, it, now) }
        if (spark == null) {
            views.setViewVisibility(R.id.spark_box, View.GONE)
        } else {
            views.setViewVisibility(R.id.spark_box, View.VISIBLE)
            views.setImageViewBitmap(R.id.spark, spark)
            WidgetArt.tint(context, views, R.id.spark, R.color.widget_line)
        }

        val lift = WidgetModel.nextLift(snapshot, now)
        views.setTextViewText(
            R.id.next_lift,
            lift?.let { context.getString(R.string.widget_next_lift, Format.`when`(it.at), it.usage.displayName) }.orEmpty(),
        )
        views.setViewVisibility(R.id.next_lift, if (lift == null) View.GONE else View.VISIBLE)

        views.setRemoteAdapter(R.id.widget_list, Intent(context, UsageWidgetService::class.java))
        views.setEmptyView(R.id.widget_list, R.id.widget_empty)
        views.setPendingIntentTemplate(R.id.widget_list, rowIntent(context))
        views.setOnClickPendingIntent(R.id.widget_refresh, refreshIntent(context))
        views.setOnClickPendingIntent(R.id.widget_root, openIntent(context))
        return views
    }

    private fun bindHero(context: Context, views: RemoteViews, percent: Float?, sizeDp: Float, strokeDp: Float) {
        WidgetArt.setRing(context, views, R.id.ring_track, R.id.ring_level, sizeDp, strokeDp, percent)
        views.setTextViewText(R.id.hero_value, percent?.let { "${it.roundToInt()}%" } ?: "—")
        WidgetArt.textColor(context, views, R.id.hero_value, percent?.let { WidgetArt.figureColor(it) } ?: R.color.widget_ink)
    }

    private fun bindStale(context: Context, views: RemoteViews, snapshot: Snapshot?) {
        val age = WidgetModel.staleAge(snapshot)
        if (age == null) {
            views.setViewVisibility(R.id.stale, View.GONE)
        } else {
            views.setViewVisibility(R.id.stale, View.VISIBLE)
            views.setTextViewText(R.id.stale, context.getString(R.string.widget_old, Format.span(age).substringBefore(' ')))
        }
    }

    /** The combined week as a line, then where it is heading, as on the All usage screen. */
    private fun spark(context: Context, snapshot: Snapshot, now: Instant): android.graphics.Bitmap? {
        val history = UsageHistory(context)
        val points = history.combined().filter { Duration.between(it.at, now) <= SPARK_SPAN }
        if (points.size < 2) return null
        val tracks = CombinedUsage.contributions(snapshot).mapNotNull { contribution ->
            Projection.track(
                contribution.window,
                history.series(contribution.account, contribution.window.label, Projection.USUAL_BASIS),
                now,
            )
        }
        val forecast = Projection.combined(
            tracks = tracks,
            from = now,
            to = now.plus(SPARK_SPAN.dividedBy(5)),
            anchor = points.last().percent,
        )
        return WidgetArt.spark(context, 96f, 40f, points, forecast, SPARK_SPAN, now)
    }

    /** One account's row; [compact] leaves out the plan and any mention without a flame. */
    fun rowViews(context: Context, line: WidgetModel.Line, compact: Boolean): RemoteViews {
        val views = RemoteViews(context.packageName, R.layout.widget_account)
        val usage = line.usage

        WidgetArt.setRing(context, views, R.id.row_track, R.id.row_level, 34f, 3.2f, line.percent)
        views.setImageViewResource(R.id.row_logo, ProviderArt.logo(usage.id))

        views.setTextViewText(R.id.row_name, usage.displayName)
        val plan = usage.plan?.takeIf { !compact && it.isNotBlank() }
        views.setTextViewText(R.id.row_plan, plan.orEmpty())
        views.setViewVisibility(R.id.row_plan, if (plan == null) View.GONE else View.VISIBLE)

        val reset = line.window.resetAt
        val name = WidgetModel.name(line.window)
        views.setTextViewText(R.id.row_window, if (reset == null) name else "$name · ")
        setReset(views, R.id.row_reset, reset)

        val mentions = line.mentions
            .filter { !compact || it.ahead }
            .sortedWith(compareBy({ !it.ahead }, { it.percent }))
            .take(2)
        bindMention(context, views, mentions.getOrNull(0), R.id.row_m1_label, R.id.row_m1_value, R.id.row_m1_flame)
        bindMention(context, views, mentions.getOrNull(1), R.id.row_m2_label, R.id.row_m2_value, null)

        views.setViewVisibility(R.id.row_flame, if (line.ahead) View.VISIBLE else View.GONE)
        views.setTextViewText(R.id.row_value, "${line.percent.roundToInt()}%")
        WidgetArt.textColor(context, views, R.id.row_value, WidgetArt.figureColor(line.percent))

        // Only a list row can open its own account; in the 4x2 a tap opens the app.
        if (!compact) {
            views.setOnClickFillInIntent(
                R.id.row_root,
                Intent().putExtra(ProviderDetailActivity.EXTRA_PROVIDER, usage.accountKey),
            )
        }
        return views
    }

    private fun bindMention(
        context: Context,
        views: RemoteViews,
        mention: WidgetModel.Mention?,
        labelId: Int,
        valueId: Int,
        flameId: Int?,
    ) {
        val visible = if (mention == null) View.GONE else View.VISIBLE
        views.setViewVisibility(labelId, visible)
        views.setViewVisibility(valueId, visible)
        flameId?.let { views.setViewVisibility(it, if (mention?.ahead == true) View.VISIBLE else View.GONE) }
        if (mention == null) return
        views.setTextViewText(labelId, WidgetModel.name(mention.window))
        views.setTextViewText(valueId, "${mention.percent.roundToInt()}%")
        // A healthy window stays quiet; only a low one takes a colour.
        val colour = WidgetArt.figureColor(mention.percent)
        WidgetArt.textColor(context, views, valueId, if (colour == R.color.widget_ink) R.color.widget_ink_secondary else colour)
    }

    fun outViews(context: Context, out: WidgetModel.Out): RemoteViews {
        val views = RemoteViews(context.packageName, R.layout.widget_account_out)
        views.setImageViewResource(R.id.row_logo, ProviderArt.logo(out.usage.id))
        views.setTextViewText(R.id.row_name, context.getString(R.string.widget_signed_out, out.usage.displayName))
        views.setOnClickFillInIntent(
            R.id.row_root,
            Intent().putExtra(ProviderDetailActivity.EXTRA_PROVIDER, out.usage.accountKey),
        )
        return views
    }

    /**
     * A countdown close enough to watch runs on a Chronometer, which ticks inside
     * the launcher by itself: no alarm, so battery saver and doze cannot stall it.
     * Anything further out is text; it changes once a day.
     */
    internal fun setReset(views: RemoteViews, viewId: Int, resetAt: Instant?) {
        val remaining = resetAt?.let { Duration.between(Instant.now(), it) }
        if (remaining != null && !remaining.isNegative && remaining.toHours() < 24) {
            views.setChronometer(viewId, SystemClock.elapsedRealtime() + remaining.toMillis(), "%s", true)
            views.setChronometerCountDown(viewId, true)
        } else {
            // A recycled row may still be ticking; stop it before writing plain text.
            views.setChronometer(viewId, 0L, "%s", false)
            views.setTextViewText(viewId, Format.reset(resetAt).orEmpty())
        }
    }

    private fun refreshIntent(context: Context): PendingIntent {
        val intent = Intent(context, UsageWidgetProvider::class.java)
            .setAction(UsageWidgetProvider.ACTION_REFRESH)
        return PendingIntent.getBroadcast(
            context, 1, intent, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
    }

    internal fun openIntent(context: Context): PendingIntent {
        val intent = Intent(context, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        return PendingIntent.getActivity(
            context, 2, intent, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
    }

    /** Rows fill in the account key; the template decides where it goes. */
    private fun rowIntent(context: Context): PendingIntent {
        val intent = Intent(context, ProviderDetailActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        return PendingIntent.getActivity(
            context, 3, intent,
            // Mutable: the host merges each row's fill-in intent into this template.
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE,
        )
    }
}

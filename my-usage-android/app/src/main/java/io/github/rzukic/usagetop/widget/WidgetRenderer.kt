package io.github.rzukic.usagetop.widget

import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.drawable.Drawable
import android.os.SystemClock
import android.widget.RemoteViews
import androidx.core.content.ContextCompat
import io.github.rzukic.usagetop.R
import io.github.rzukic.usagetop.data.ProviderUsage
import io.github.rzukic.usagetop.data.Snapshot
import io.github.rzukic.usagetop.data.UsageState
import io.github.rzukic.usagetop.data.UsageWindow
import io.github.rzukic.usagetop.ui.Format
import io.github.rzukic.usagetop.ui.MainActivity
import io.github.rzukic.usagetop.ui.ProviderArt
import io.github.rzukic.usagetop.ui.ProviderDetailActivity
import io.github.rzukic.usagetop.ui.RingBitmap
import io.github.rzukic.usagetop.ui.Severity
import java.time.Duration
import java.time.Instant


import kotlin.math.roundToInt

/**
 * Renders the widget in OpenUsage's Priority style: a level ring per window, rows
 * bucketed by how close they are to running out, and a scrolling list so every
 * window fits whatever height the launcher gives us.
 */
object WidgetRenderer {

    private const val RING_DP = 40

    private const val FORFEIT = "Use it or lose it"

    /**
     * The last quarter of a window, capped at half a day. Past that point the quota
     * you have not spent is about to be taken back, so it is worth a nudge.
     */
    private val FORFEIT_CAP: Duration = Duration.ofHours(12)

    /** A window label that is a plain duration, e.g. "5h" or "7d". */
    private val DURATION_LABEL = Regex("""^(\d+)\s*([mhd])$""", RegexOption.IGNORE_CASE)

    /** Buckets in the order they are shown: dead first, then forfeit, then fine. */
    private val BUCKETS =
        listOf("Running out", FORFEIT, "Used up", "Healthy", "Unavailable")

    /** A list item: either a bucket heading or one window of one provider. */
    sealed interface Entry {
        data class Group(val label: String) : Entry
        data class Row(val usage: ProviderUsage, val window: UsageWindow?) : Entry
    }

    fun build(context: Context, snapshot: Snapshot?): RemoteViews {
        val views = RemoteViews(context.packageName, R.layout.widget_usage)
        views.setTextViewText(R.id.widget_updated, snapshot?.let { Format.ago(it.fetchedAt) }.orEmpty())

        views.setRemoteAdapter(R.id.widget_list, Intent(context, UsageWidgetService::class.java))
        views.setEmptyView(R.id.widget_list, R.id.widget_empty)

        views.setOnClickPendingIntent(R.id.widget_refresh, refreshIntent(context))
        views.setOnClickPendingIntent(R.id.widget_title, openIntent(context))
        views.setPendingIntentTemplate(R.id.widget_list, rowIntent(context))
        return views
    }

    /** Every window of every provider, grouped by how close it is to running out. */
    fun entries(snapshot: Snapshot?): List<Entry> {
        val usages = snapshot?.usages.orEmpty()
        if (usages.isEmpty()) return emptyList()

        val rows = usages.flatMap { usage ->
            // A provider with nothing to report still gets a row, so its state shows.
            usage.windows.ifEmpty { listOf(UsageWindow(label = "")) }
                .map { window -> Entry.Row(usage, window) }
        }

        return BUCKETS.flatMap { label ->
            val group = rows.filter { bucketOf(it.window) == label }
            // The forfeit bucket is about the clock, not the level: soonest first.
            val ordered = if (label == FORFEIT) {
                group.sortedBy { it.window?.resetAt ?: Instant.MAX }
            } else {
                group.sortedBy { it.window?.remainingPercent ?: Float.MAX_VALUE }
            }
            if (ordered.isEmpty()) emptyList() else listOf(Entry.Group(label)) + ordered
        }
    }

    fun groupViews(context: Context, label: String): RemoteViews {
        val views = RemoteViews(context.packageName, R.layout.widget_group)
        views.setTextViewText(R.id.group_label, label.uppercase())
        return views
    }

    fun rowViews(context: Context, usage: ProviderUsage, window: UsageWindow?): RemoteViews {
        val views = RemoteViews(context.packageName, R.layout.widget_row)
        val percent = window?.remainingPercent
        val spent = percent != null && percent <= 0f

        views.setImageViewBitmap(R.id.row_ring, ring(context, usage, percent))

        views.setTextViewText(R.id.row_title, usage.displayName)
        views.setTextColor(
            R.id.row_title,
            color(context, if (spent) R.color.text_tertiary else R.color.text_primary),
        )

        // The plan is deliberately absent here: the widget is about what is left.
        views.setTextViewText(R.id.row_subtitle, window?.label.orEmpty().ifBlank { note(usage) })

        if (percent != null) {
            views.setTextViewText(R.id.row_value, "${percent.roundToInt()}% left")
            views.setTextColor(R.id.row_value, Severity.textColor(context, percent))
            setReset(views, window.resetAt)
        } else {
            views.setTextViewText(R.id.row_value, window?.detail ?: "—")
            views.setTextColor(R.id.row_value, color(context, R.color.text_secondary))
            setReset(views, null)
        }

        views.setOnClickFillInIntent(
            R.id.row_root,
            Intent().putExtra(ProviderDetailActivity.EXTRA_PROVIDER, usage.accountKey),
        )
        return views
    }

    /**
     * A countdown that is close enough to watch runs on a Chronometer, which ticks
     * itself inside the launcher — no alarm, so battery saver and doze cannot stall
     * it. Anything further out is written as text: it changes once a day, so a
     * redraw every fifteen minutes is already more than enough.
     */
    private fun setReset(views: RemoteViews, resetAt: Instant?) {
        val remaining = resetAt?.let { Duration.between(Instant.now(), it) }

        if (remaining != null && !remaining.isNegative && remaining.toHours() < 24) {
            views.setChronometer(
                R.id.row_reset,
                SystemClock.elapsedRealtime() + remaining.toMillis(),
                "%s",
                true,
            )
            views.setChronometerCountDown(R.id.row_reset, true)
        } else {
            // A recycled row may still be ticking; stop it before writing plain text.
            views.setChronometer(R.id.row_reset, 0L, "%s", false)
            views.setTextViewText(R.id.row_reset, Format.reset(resetAt).orEmpty())
        }
    }

    /** Which heading a window belongs under. */
    internal fun bucketOf(window: UsageWindow?): String {
        val percent = window?.remainingPercent ?: return "Unavailable"
        return when {
            percent <= 0f -> "Used up"
            percent <= 40f -> "Running out"
            forfeiting(window) -> FORFEIT
            else -> "Healthy"
        }
    }

    /** Healthy, but the window is nearly over and the quota goes with it. */
    private fun forfeiting(window: UsageWindow): Boolean {
        val reset = window.resetAt ?: return false
        val left = Duration.between(Instant.now(), reset)
        return !left.isNegative && !left.isZero && left <= deadline(window.label)
    }

    private fun deadline(label: String): Duration {
        val length = length(label) ?: return FORFEIT_CAP
        val quarter = length.dividedBy(4)
        return if (quarter < FORFEIT_CAP) quarter else FORFEIT_CAP
    }

    /** "5h", "7d" and "30m" say how long the window is; "Monthly" does not. */
    private fun length(label: String): Duration? {
        val match = DURATION_LABEL.matchEntire(label.trim()) ?: return null
        val amount = match.groupValues[1].toLongOrNull() ?: return null
        return when (match.groupValues[2].lowercase()) {
            "m" -> Duration.ofMinutes(amount)
            "h" -> Duration.ofHours(amount)
            "d" -> Duration.ofDays(amount)
            else -> null
        }
    }

    private fun ring(context: Context, usage: ProviderUsage, percent: Float?): Bitmap {
        val size = dp(context, RING_DP)
        val track = color(context, R.color.meter_track)
        val logo: Drawable? = ContextCompat.getDrawable(context, ProviderArt.logo(usage.id))

        return if (percent == null) {
            RingBitmap.draw(size, null, track, track, logo, color(context, R.color.text_tertiary))
        } else {
            RingBitmap.draw(
                size,
                percent,
                Severity.color(context, percent),
                track,
                logo,
                color(context, R.color.text_primary),
            )
        }
    }

    private fun note(usage: ProviderUsage): String = when (usage.state) {
        UsageState.NOT_CONFIGURED -> usage.message ?: "Not configured"
        UsageState.ERROR -> usage.message ?: "Error"
        UsageState.OK -> usage.message ?: "No data"
    }

    private fun color(context: Context, id: Int) = ContextCompat.getColor(context, id)
    private fun dp(context: Context, value: Int): Int =
        (value * context.resources.displayMetrics.density).roundToInt()

    private fun refreshIntent(context: Context): PendingIntent {
        val intent = Intent(context, UsageWidgetProvider::class.java)
            .setAction(UsageWidgetProvider.ACTION_REFRESH)
        return PendingIntent.getBroadcast(
            context,
            1,
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
    }

    private fun openIntent(context: Context): PendingIntent {
        val intent = Intent(context, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        return PendingIntent.getActivity(
            context,
            2,
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
    }

    /** Rows fill in the provider key; the template decides where it goes. */
    private fun rowIntent(context: Context): PendingIntent {
        val intent = Intent(context, ProviderDetailActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        return PendingIntent.getActivity(
            context,
            3,
            intent,
            // Mutable: the host merges each row's fill-in intent into this template.
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE,
        )
    }
}

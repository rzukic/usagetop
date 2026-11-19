package io.github.rzukic.usagetop.widget

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.DashPathEffect
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RectF
import android.os.Build
import android.util.LruCache
import android.widget.RemoteViews
import io.github.rzukic.usagetop.R
import io.github.rzukic.usagetop.data.UsagePoint
import java.time.Duration
import java.time.Instant
import kotlin.math.roundToInt

/**
 * The widget's pictures: level rings and the combined line.
 *
 * RemoteViews cannot draw an arc, so these are bitmaps, but only shapes: one
 * byte a pixel, coloured by the ImageView from a colour resource. That keeps
 * the light and dark setting with the resources, where the launcher resolves it
 * itself, and keeps an update small: a whole widget, every size at once, has to
 * fit in about a megabyte, and a 2x2 ring drawn in full colour for both themes
 * would take most of that on its own.
 */
object WidgetArt {

    /** Shapes repeat across rows and redraws; the one-minute tick should not redraw them. */
    private val shapes = LruCache<String, Bitmap>(64)

    /** The full circle a ring's level runs around. */
    fun track(context: Context, sizeDp: Float, strokeDp: Float): Bitmap =
        arc(context, sizeDp, strokeDp, 100)

    /** The level itself, from twelve o'clock clockwise. A sliver stays at 0-1%. */
    fun level(context: Context, sizeDp: Float, strokeDp: Float, percent: Float): Bitmap =
        arc(context, sizeDp, strokeDp, percent.roundToInt().coerceIn(1, 100))

    private fun arc(context: Context, sizeDp: Float, strokeDp: Float, percent: Int): Bitmap {
        val px = px(context, sizeDp)
        val stroke = px(context, strokeDp).toFloat()
        val key = "arc/$px/$stroke/$percent"
        shapes.get(key)?.let { return it }

        val bitmap = Bitmap.createBitmap(px, px, Bitmap.Config.ALPHA_8)
        val inset = stroke / 2f
        val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.STROKE
            strokeWidth = stroke
            strokeCap = Paint.Cap.ROUND
        }
        Canvas(bitmap).drawArc(RectF(inset, inset, px - inset, px - inset), -90f, percent * 3.6f, false, paint)
        shapes.put(key, bitmap)
        return bitmap
    }

    /**
     * The combined level over [span] up to now as a line, then the forecast
     * dashed. No fill and no grid: at this size they read as texture.
     */
    fun spark(
        context: Context,
        widthDp: Float,
        heightDp: Float,
        points: List<UsagePoint>,
        forecast: List<UsagePoint>,
        span: Duration,
        now: Instant,
    ): Bitmap? {
        val start = now.minus(span)
        val shown = points.filter { !it.at.isBefore(start) && !it.at.isAfter(now) }
        if (shown.size < 2) return null

        val w = px(context, widthDp)
        val h = px(context, heightDp)
        val stroke = px(context, 2f).toFloat()
        // History takes the left five sixths, the forecast the rest.
        val end = now.plus(span.dividedBy(5))
        val total = Duration.between(start, end).toMillis().toFloat()
        val x = { at: Instant -> Duration.between(start, at).toMillis() / total * (w - stroke) + stroke / 2 }
        val y = { percent: Float -> h - stroke / 2 - percent.coerceIn(0f, 100f) / 100f * (h - stroke) }

        val bitmap = Bitmap.createBitmap(w, h, Bitmap.Config.ALPHA_8)
        val canvas = Canvas(bitmap)
        val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.STROKE
            strokeWidth = stroke
            strokeCap = Paint.Cap.ROUND
            strokeJoin = Paint.Join.ROUND
        }
        canvas.drawPath(path(shown, x, y), paint)

        val ahead = forecast.filter { it.at.isAfter(now) && !it.at.isAfter(end) }
        if (ahead.isNotEmpty()) {
            paint.pathEffect = DashPathEffect(floatArrayOf(stroke * 1.6f, stroke * 1.6f), 0f)
            canvas.drawPath(path(listOf(shown.last()) + ahead, x, y), paint)
        }
        return bitmap
    }

    private fun path(points: List<UsagePoint>, x: (Instant) -> Float, y: (Float) -> Float) = Path().apply {
        points.forEachIndexed { i, p -> if (i == 0) moveTo(x(p.at), y(p.percent)) else lineTo(x(p.at), y(p.percent)) }
    }

    /**
     * A ring into its two stacked ImageViews: the track, then the level in its
     * severity colour. [percent] null leaves the track alone, for an account with
     * nothing to read.
     */
    fun setRing(
        context: Context,
        views: RemoteViews,
        trackId: Int,
        levelId: Int,
        sizeDp: Float,
        strokeDp: Float,
        percent: Float?,
    ) {
        views.setImageViewBitmap(trackId, track(context, sizeDp, strokeDp))
        tint(context, views, trackId, R.color.widget_track)
        if (percent == null) {
            views.setViewVisibility(levelId, android.view.View.INVISIBLE)
        } else {
            views.setViewVisibility(levelId, android.view.View.VISIBLE)
            views.setImageViewBitmap(levelId, level(context, sizeDp, strokeDp, percent))
            tint(context, views, levelId, ringColor(percent))
        }
    }

    fun ringColor(percent: Float): Int = when {
        percent <= WidgetModel.CRITICAL -> R.color.widget_ring_critical
        percent <= WidgetModel.WARNING -> R.color.widget_ring_warning
        else -> R.color.widget_ring_normal
    }

    /** A figure's colour: only a warning or a critical one is coloured. */
    fun figureColor(percent: Float): Int = when {
        percent <= WidgetModel.CRITICAL -> R.color.widget_critical
        percent <= WidgetModel.WARNING -> R.color.widget_warning
        else -> R.color.widget_ink
    }

    /**
     * Colours a shape from a resource. From Android 12 the launcher resolves the
     * resource in its own light or dark setting; before, it is resolved now and the
     * widget is redrawn when the setting changes.
     */
    fun tint(context: Context, views: RemoteViews, viewId: Int, colorRes: Int) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            views.setColor(viewId, "setColorFilter", colorRes)
        } else {
            views.setInt(viewId, "setColorFilter", context.getColor(colorRes))
        }
    }

    /** A text colour from a resource, resolved the same way as [tint]. */
    fun textColor(context: Context, views: RemoteViews, viewId: Int, colorRes: Int) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            views.setColor(viewId, "setTextColor", colorRes)
        } else {
            views.setTextColor(viewId, context.getColor(colorRes))
        }
    }

    fun px(context: Context, dp: Float): Int = (dp * context.resources.displayMetrics.density).roundToInt()
}

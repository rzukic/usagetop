package io.github.rzukic.usagetop.ui

import android.content.Context
import android.graphics.Canvas
import android.graphics.DashPathEffect
import android.graphics.LinearGradient
import android.graphics.Paint
import android.graphics.Path
import android.graphics.Shader
import android.graphics.drawable.Drawable
import android.util.AttributeSet
import android.view.HapticFeedbackConstants
import android.view.MotionEvent
import android.view.View
import android.view.ViewConfiguration
import androidx.core.content.ContextCompat
import io.github.rzukic.usagetop.R
import io.github.rzukic.usagetop.data.Projection
import io.github.rzukic.usagetop.data.ProviderId
import io.github.rzukic.usagetop.data.Trendline
import io.github.rzukic.usagetop.data.UsagePoint
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale
import kotlin.math.abs
import kotlin.math.roundToInt

/**
 * A window's remaining level over time -- the drawdown. Percent runs 0-100 up the
 * vertical axis, time along the horizontal one.
 *
 * With a forecast it is a single window's card, drawn as the server page draws
 * it: the readings up to now, the forecast running on past now to where the
 * window runs dry or refills, and the even spread through the cycle. With a
 * [span] it becomes the combined view: the axis is the picked range with now in
 * the middle, the left half is the history with a dotted line at every refill,
 * and the right half carries every window's forecast forward plus a marker at
 * every reset still to come. With neither, the axis covers exactly the readings
 * it was given.
 */
class DrawdownChart @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
) : View(context, attrs) {

    private var points: List<UsagePoint> = emptyList()
    private var span: Duration? = null
    private var resets: List<Reset> = emptyList()
    private var projection: List<UsagePoint> = emptyList()
    private var forecast: Projection.Track? = null
    private var ahead: Duration = Duration.ofDays(7)
    private var lineColor: Int = ContextCompat.getColor(context, R.color.meter_normal)

    /** Where the finger is while it is on the chart, or null when it is not. */
    private var scrubX: Float? = null
    private var scrubbing = false
    private var downX = 0f
    private var downY = 0f
    private val touchSlop = ViewConfiguration.get(context).scaledTouchSlop

    // What the last draw put on screen, so the crosshair reads off the same axis.
    private var axisFrom: Instant? = null
    private var axisTo: Instant? = null
    private var plotBottom = 0f
    private var drawnSeries: List<UsagePoint> = emptyList()
    private var drawnProjection: List<UsagePoint> = emptyList()
    private var axisShort = false

    private val icons = mutableMapOf<ProviderId, Drawable?>()

    /**
     * A window reset still to come. [providers] is who refills, drawn as a small
     * mark above the line.
     */
    data class Reset(
        val at: Instant,
        val providers: List<ProviderId> = emptyList(),
    )

    private val gridPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = ContextCompat.getColor(context, R.color.hairline)
        strokeWidth = dp(1f)
    }

    private val linePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = dp(2f)
        strokeCap = Paint.Cap.ROUND
        strokeJoin = Paint.Join.ROUND
    }

    private val fillPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.FILL
    }

    private val hintPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = ContextCompat.getColor(context, R.color.text_tertiary)
        textSize = dp(11f)
        textAlign = Paint.Align.CENTER
    }

    private val dotPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.FILL
    }

    private val spanPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = ContextCompat.getColor(context, R.color.text_tertiary)
        textSize = dp(10f)
        textAlign = Paint.Align.RIGHT
    }

    /** The fitted burn rate, dashed so it never reads as another reading. */
    private val trendPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = dp(1.5f)
        strokeCap = Paint.Cap.ROUND
        pathEffect = DashPathEffect(floatArrayOf(dp(5f), dp(4f)), 0f)
    }

    /** Where a window reset and the level jumped back up. */
    private val refillPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = ContextCompat.getColor(context, R.color.text_tertiary)
        strokeWidth = dp(1f)
        pathEffect = DashPathEffect(floatArrayOf(dp(2f), dp(3f)), 0f)
    }

    /**
     * A reset that has not happened yet. Brighter and longer-dashed than a
     * refill, because it is the one thing in the future half that is a fact
     * rather than a projection.
     */
    private val resetPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = ContextCompat.getColor(context, R.color.text_secondary)
        strokeWidth = dp(1f)
        pathEffect = DashPathEffect(floatArrayOf(dp(4f), dp(4f)), 0f)
    }

    private val nowPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = ContextCompat.getColor(context, R.color.text_tertiary)
        strokeWidth = dp(1f)
    }

    private val axisPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = ContextCompat.getColor(context, R.color.text_tertiary)
        textSize = dp(10f)
        textAlign = Paint.Align.CENTER
    }

    /** Where an even spread through the cycle would be. */
    private val evenPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = ContextCompat.getColor(context, R.color.text_tertiary)
        strokeWidth = dp(1f)
        pathEffect = DashPathEffect(floatArrayOf(dp(3f), dp(3f)), 0f)
    }

    /** The ring where the forecast runs dry: hollow, in the window's colour. */
    private val emptyRing = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = dp(2f)
    }

    private val emptyFill = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.FILL
        color = ContextCompat.getColor(context, R.color.panel_raised)
    }

    private val emptyLabel = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = ContextCompat.getColor(context, R.color.text_primary)
        textSize = dp(10f)
    }

    /** The crosshair: a hairline where the finger is. */
    private val scrubLinePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = ContextCompat.getColor(context, R.color.text_secondary)
        strokeWidth = dp(1f)
    }

    /** The level under the finger, in a pill that stays inside the chart. */
    private val scrubPillPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.FILL
        color = ContextCompat.getColor(context, R.color.panel_raised)
    }

    private val scrubLabelPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = ContextCompat.getColor(context, R.color.text_primary)
        textSize = dp(11f)
        textAlign = Paint.Align.CENTER
    }

    private val scrubTimePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = ContextCompat.getColor(context, R.color.text_primary)
        textSize = dp(10f)
        textAlign = Paint.Align.CENTER
    }

    fun setSeries(
        points: List<UsagePoint>,
        color: Int,
        span: Duration? = null,
        resets: List<Reset> = emptyList(),
        projection: List<UsagePoint> = emptyList(),
    ) {
        this.points = points
        this.lineColor = color
        this.span = span
        this.resets = resets
        this.projection = projection
        this.forecast = null
        invalidate()
    }

    /**
     * A single window's card: its readings, and [forecast] carried on past now,
     * never further than [ahead].
     */
    fun setWindow(
        points: List<UsagePoint>,
        color: Int,
        forecast: Projection.Track?,
        ahead: Duration,
    ) {
        this.points = points
        this.lineColor = color
        this.span = null
        this.resets = emptyList()
        this.projection = emptyList()
        this.forecast = forecast
        this.ahead = ahead
        invalidate()
    }

    /**
     * When the combined level reaches zero, or null when it stays above it for
     * the whole range. The projection already carries every window's own burn and
     * every refill, so this is just the first sample that is empty.
     */
    fun projectedExhaustion(): Instant? = projection.firstOrNull { it.percent <= 0f }?.at

    override fun onDraw(canvas: Canvas) {
        val w = width.toFloat()
        val h = height.toFloat()
        if (w <= 0f || h <= 0f) return

        // Each mode sets these as it draws; a mode that draws nothing leaves them
        // empty, so a stale axis can never put a crosshair on a blank chart.
        axisFrom = null
        axisTo = null
        plotBottom = 0f
        drawnSeries = emptyList()
        drawnProjection = emptyList()

        val window = span
        val track = forecast
        when {
            window != null -> drawSpan(canvas, w, h, window)
            track != null -> drawWindow(canvas, w, h, track)
            else -> drawFitted(canvas, w, h)
        }

        drawScrub(canvas, w, h)
    }

    /**
     * Hold the chart and the level at that moment is read off it, the way a
     * trading app does it. A vertical drag is left to the page: the chart only
     * takes the gesture once it is clearly sideways.
     */
    override fun onTouchEvent(event: MotionEvent): Boolean {
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                downX = event.x
                downY = event.y
                scrubbing = false
                scrubX = event.x.coerceIn(0f, width.toFloat())
                invalidate()
                return true
            }

            MotionEvent.ACTION_MOVE -> {
                if (!scrubbing) {
                    val dx = abs(event.x - downX)
                    val dy = abs(event.y - downY)
                    if (dy > dx && dy > touchSlop) {
                        // The page scrolls; the crosshair was only a touch.
                        scrubX = null
                        invalidate()
                        return true
                    }
                    if (dx > touchSlop) {
                        scrubbing = true
                        parent?.requestDisallowInterceptTouchEvent(true)
                        performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK)
                    }
                }
                scrubX = event.x.coerceIn(0f, width.toFloat())
                invalidate()
                return true
            }

            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                if (scrubbing) performClick()
                scrubbing = false
                scrubX = null
                parent?.requestDisallowInterceptTouchEvent(false)
                invalidate()
                return true
            }
        }

        return super.onTouchEvent(event)
    }

    /**
     * The level at the finger. A level holds until the next reading, so the one
     * in force at a moment is the last reading at or before it. Past now it is
     * the forecast, marked with a tilde so it never reads as a measurement.
     */
    private fun drawScrub(canvas: Canvas, w: Float, h: Float) {
        val x = scrubX ?: return
        val from = axisFrom ?: return
        val to = axisTo ?: return
        val series = drawnSeries
        if (series.isEmpty() || plotBottom <= 0f) return

        val millis = Duration.between(from, to).toMillis().coerceAtLeast(1L)
        val at = from.plusMillis((x / w * millis).toLong().coerceIn(0L, millis))

        val level = Scrub.levelAt(series, drawnProjection, at) ?: return
        val point = level.point
        val projected = level.projected

        val y = plotBottom - point.percent.coerceIn(0f, 100f) / 100f * plotBottom
        canvas.drawLine(x, 0f, x, plotBottom, scrubLinePaint)

        dotPaint.color = ContextCompat.getColor(context, R.color.panel_raised)
        canvas.drawCircle(x, y, dp(5f), dotPaint)
        dotPaint.color = lineColor
        canvas.drawCircle(x, y, dp(3.5f), dotPaint)

        val label = (if (projected) "~" else "") + point.percent.roundToInt() + "%"
        val padH = dp(7f)
        val padV = dp(4f)
        val pillW = scrubLabelPaint.measureText(label) + padH * 2
        val pillH = scrubLabelPaint.textSize + padV * 2
        val left = (x - pillW / 2f).coerceIn(0f, (w - pillW).coerceAtLeast(0f))

        // Beside the dot rather than at the top, where the reset marks are.
        val top = if (y - pillH - dp(8f) >= 0f) y - pillH - dp(8f) else y + dp(8f)
        canvas.drawRoundRect(
            left,
            top,
            left + pillW,
            top + pillH,
            pillH / 2f,
            pillH / 2f,
            scrubPillPaint,
        )
        canvas.drawText(
            label,
            left + pillW / 2f,
            top + pillH / 2f - (scrubLabelPaint.ascent() + scrubLabelPaint.descent()) / 2f,
            scrubLabelPaint,
        )

        val moment = if (axisShort) Format.clock(at) else Format.day(at)
        val half = scrubTimePaint.measureText(moment) / 2f
        canvas.drawText(
            moment,
            x.coerceIn(half, (w - half).coerceAtLeast(half)),
            h - dp(3f),
            scrubTimePaint,
        )
    }

    /** A single window's card: the axis is exactly the readings it was given. */
    private fun drawFitted(canvas: Canvas, w: Float, h: Float) {
        drawGrid(canvas, w, h)

        val start = points.firstOrNull()?.at
        val end = points.lastOrNull()?.at
        if (start == null || end == null) {
            drawCollecting(canvas, w, h / 2f)
            return
        }

        val total = Duration.between(start, end).toMillis().coerceAtLeast(1L)
        val xOf: (Instant) -> Float = { at ->
            Duration.between(start, at).toMillis().toFloat() / total * w
        }
        val yOf: (Float) -> Float = { percent -> h - percent.coerceIn(0f, 100f) / 100f * h }

        // One reading cannot be a line, but it is still a reading: show it as a
        // dot instead of claiming there is nothing yet.
        if (points.size == 1) {
            dotPaint.color = lineColor
            canvas.drawCircle(w / 2f, yOf(points.first().percent), dp(3f), dotPaint)
            return
        }

        drawSeries(canvas, points, xOf, yOf, h)

        axisFrom = start
        axisTo = end
        plotBottom = h
        drawnSeries = points
        axisShort = total <= Duration.ofDays(2).toMillis()

        // Without a fixed span the axis stretches to fit the readings, so say how
        // much history that actually is: 7m and 7d would otherwise look the same.
        canvas.drawText(
            Format.span(Duration.between(start, end)),
            w,
            spanPaint.textSize,
            spanPaint,
        )
    }

    /**
     * A single window's card, as the server page draws it. The readings run from
     * the oldest kept up to now; past now the forecast goes on half as far again
     * as the history, or on to where the window runs dry, but never past its
     * reset or the span the history covers.
     */
    private fun drawWindow(canvas: Canvas, w: Float, h: Float, track: Projection.Track) {
        val axis = dp(16f)
        val plot = h - axis
        drawGrid(canvas, w, plot)

        if (points.size < 2) {
            if (points.size == 1) {
                dotPaint.color = lineColor
                canvas.drawCircle(w / 2f, plot - points.first().percent.coerceIn(0f, 100f) / 100f * plot, dp(3f), dotPaint)
            } else {
                drawCollecting(canvas, w, plot / 2f)
            }
            return
        }

        val now = Instant.now()
        val from = points.first().at
        val last = points.last()
        val until = if (last.at.isAfter(now)) last.at else now

        val empty = Projection.emptyAt(track, now.plus(Projection.HORIZON))
        var to = until
        if (track.measured) {
            val history = Duration.between(from, until)
            to = until.plus(maxOf(history.dividedBy(2), Duration.ofHours(1)))
            if (empty != null) {
                val past = empty.plusMillis((Duration.between(until, empty).toMillis() * 0.15).toLong())
                if (past.isAfter(to)) to = past
            }
            if (to.isAfter(until.plus(ahead))) to = until.plus(ahead)
            val reset = track.nextReset
            if (reset != null && reset.isAfter(until) && reset.isBefore(to)) to = reset
        }

        val millis = Duration.between(from, to).toMillis().coerceAtLeast(1L).toFloat()
        val xOf: (Instant) -> Float = { at -> Duration.between(from, at).toMillis() / millis * w }
        val yOf: (Float) -> Float = { percent -> plot - percent.coerceIn(0f, 100f) / 100f * plot }

        // The even spread through this cycle: from full at the last refill to empty
        // at the next. Above it the window is being used faster than that.
        val length = track.period
        val next = track.nextReset
        if (length != null && next != null && Duration.between(now, next) <= length) {
            val start = next.minus(length).let { if (it.isAfter(from)) it else from }
            val even: (Instant) -> Float = { at ->
                (Duration.between(at, next).toMillis().toDouble() / length.toMillis() * 100.0).toFloat()
            }
            canvas.drawLine(xOf(start), yOf(even(start)), xOf(to), yOf(even(to)), evenPaint)
        }

        // The level holds until the next reading, and after the last one until now.
        val held = if (until.isAfter(last.at)) points + UsagePoint(until, last.percent) else points
        drawSeries(canvas, held, xOf, yOf, plot)

        val nowX = xOf(until)
        val future = to.isAfter(until)
        val forward = if (future) Projection.path(track, until, to) else emptyList()
        if (future) {
            canvas.drawLine(nowX, 0f, nowX, plot, nowPaint)

            if (forward.size >= 2) {
                val path = Path()
                forward.forEachIndexed { index, point ->
                    val x = xOf(point.at)
                    val y = yOf(point.percent)
                    if (index == 0) path.moveTo(x, y) else path.lineTo(x, y)
                }
                trendPaint.color = withAlpha(lineColor, 217)
                canvas.drawPath(path, trendPaint)
            }

            if (empty != null && !empty.isAfter(to)) {
                val ex = xOf(empty)
                emptyRing.color = lineColor
                canvas.drawCircle(ex, yOf(0f), dp(3.5f), emptyFill)
                canvas.drawCircle(ex, yOf(0f), dp(3.5f), emptyRing)
                val nearRight = ex > w - dp(90f)
                emptyLabel.textAlign = if (nearRight) Paint.Align.RIGHT else Paint.Align.LEFT
                canvas.drawText(
                    "empty " + Format.`when`(empty),
                    if (nearRight) ex - dp(6f) else ex + dp(6f),
                    yOf(0f) - dp(6f),
                    emptyLabel,
                )
            }
        }

        // Along the bottom: where the history starts, now, and where the forecast ends.
        val shortRange = Duration.between(from, to) <= Duration.ofDays(2)
        val mark: (Instant) -> String = { at -> if (shortRange) Format.clock(at) else Format.day(at) }
        val baseline = h - dp(3f)
        axisPaint.textAlign = if (future) Paint.Align.CENTER else Paint.Align.RIGHT
        canvas.drawText("now", nowX, baseline, axisPaint)
        if (nowX > dp(70f)) {
            axisPaint.textAlign = Paint.Align.LEFT
            canvas.drawText(mark(from), 0f, baseline, axisPaint)
        }
        if (future && w - nowX > dp(70f)) {
            axisPaint.textAlign = Paint.Align.RIGHT
            canvas.drawText(mark(to), w, baseline, axisPaint)
        }

        axisFrom = from
        axisTo = to
        plotBottom = plot
        drawnSeries = held
        drawnProjection = forward
        axisShort = shortRange
    }

    /**
     * The combined view: the axis is the range the user picked, now sits in the
     * middle, and the right half is the future the fitted line projects into.
     */
    private fun drawSpan(canvas: Canvas, w: Float, h: Float, window: Duration) {
        val axis = dp(16f)
        val plot = h - axis
        drawGrid(canvas, w, plot)

        val now = Instant.now()

        // The axis is exactly the picked range with now in the middle: the left
        // half is what happened, the right half is what the burn rate expects.
        // "7d" therefore means seven days on screen, not seven days plus a strip.
        val half = window.toMillis() / 2
        val start = now.minusMillis(half)
        val end = now.plusMillis(half)
        val millis = window.toMillis().coerceAtLeast(1L)

        val xOf: (Instant) -> Float = { at ->
            (at.toEpochMilli() - start.toEpochMilli()).toFloat() / millis * w
        }
        val yOf: (Float) -> Float = { percent -> plot - percent.coerceIn(0f, 100f) / 100f * plot }

        val visible = points.filter { !it.at.isBefore(start) && !it.at.isAfter(now) }
        if (visible.isEmpty()) {
            drawCollecting(canvas, w, plot / 2f)
            return
        }

        val fit = Trendline.fit(visible)

        // Refills go under the series, so the line stays readable over them.
        fit?.refills?.forEach { refill ->
            val x = xOf(refill.at)
            if (x < -dp(2f) || x > w + dp(2f)) return@forEach
            canvas.drawLine(x, 0f, x, plot, refillPaint)
        }

        // Resets still to come. These are known dates, not a projection, so they
        // get a marker at the top as well as the line, and the provider's mark
        // above it so it reads as "this one refills" rather than a bare date.
        resets.forEach { reset ->
            if (reset.at.isBefore(now) || reset.at.isAfter(end)) return@forEach
            val x = xOf(reset.at)
            canvas.drawLine(x, 0f, x, plot, resetPaint)

            val iconSize = dp(11f)
            val gap = dp(1f)
            val total = reset.providers.size * iconSize + (reset.providers.size - 1) * gap
            var left = x - total / 2f
            reset.providers.forEach { provider ->
                val icon = icon(provider) ?: return@forEach
                icon.setBounds(
                    left.toInt(),
                    0,
                    (left + iconSize).toInt(),
                    iconSize.toInt(),
                )
                icon.draw(canvas)
                left += iconSize + gap
            }

            val top = iconSize + dp(1f)
            val marker = Path().apply {
                moveTo(x - dp(3f), top)
                lineTo(x + dp(3f), top)
                lineTo(x, top + dp(4f))
                close()
            }
            canvas.drawPath(marker, resetPaint)
        }

        if (visible.size == 1) {
            dotPaint.color = lineColor
            canvas.drawCircle(xOf(visible.first().at), yOf(visible.first().percent), dp(3f), dotPaint)
        } else {
            drawSeries(canvas, visible, xOf, yOf, plot)
        }

        drawProjection(canvas, xOf, yOf)

        // Now splits history from projection.
        val nowX = xOf(now)
        canvas.drawLine(nowX, 0f, nowX, plot, nowPaint)

        // Both ends of the axis, so the range on screen reads without a caption.
        axisPaint.textAlign = Paint.Align.LEFT
        canvas.drawText(
            DAY_FORMAT.format(start.atZone(ZoneId.systemDefault())),
            0f,
            h - dp(4f),
            axisPaint,
        )
        axisPaint.textAlign = Paint.Align.RIGHT
        canvas.drawText(
            DAY_FORMAT.format(end.atZone(ZoneId.systemDefault())),
            w,
            h - dp(4f),
            axisPaint,
        )

        axisFrom = start
        axisTo = end
        plotBottom = plot
        drawnSeries = visible
        drawnProjection = projection
        axisShort = window <= Duration.ofDays(2)
    }

    private fun drawGrid(canvas: Canvas, w: Float, bottom: Float) {
        // Empty, half, full.
        listOf(0f, 0.5f, 1f).forEach { fraction ->
            val y = bottom - fraction * bottom
            canvas.drawLine(0f, y, w, y, gridPaint)
        }
    }

    private fun drawSeries(
        canvas: Canvas,
        visible: List<UsagePoint>,
        xOf: (Instant) -> Float,
        yOf: (Float) -> Float,
        bottom: Float,
    ) {
        val line = Path()
        visible.forEachIndexed { index, point ->
            val x = xOf(point.at)
            val y = yOf(point.percent)
            if (index == 0) line.moveTo(x, y) else line.lineTo(x, y)
        }

        val area = Path(line).apply {
            lineTo(xOf(visible.last().at), bottom)
            lineTo(xOf(visible.first().at), bottom)
            close()
        }

        // Fade the area out towards the floor so a high series does not read as a slab.
        fillPaint.shader = LinearGradient(
            0f,
            0f,
            0f,
            bottom,
            intArrayOf(withAlpha(lineColor, 96), withAlpha(lineColor, 0)),
            floatArrayOf(0f, 1f),
            Shader.TileMode.CLAMP,
        )
        canvas.drawPath(area, fillPaint)

        linePaint.color = lineColor
        canvas.drawPath(line, linePaint)
    }

    /**
     * The expectation, drawn only where it belongs: ahead of now. The readings
     * are the history, so nothing is fitted back over them -- a dashed line
     * tracing the past would just be the solid line again, and it made the
     * future half look like it had already happened.
     *
     * The points arrive already worked out, one window at a time, so the line
     * steps up at every refill and sits on the floor wherever everything is
     * empty.
     */
    private fun drawProjection(
        canvas: Canvas,
        xOf: (Instant) -> Float,
        yOf: (Float) -> Float,
    ) {
        if (projection.size < 2) return

        val path = Path()
        projection.forEachIndexed { index, point ->
            val x = xOf(point.at)
            val y = yOf(point.percent)
            if (index == 0) path.moveTo(x, y) else path.lineTo(x, y)
        }

        trendPaint.color = ContextCompat.getColor(context, R.color.text_secondary)
        canvas.drawPath(path, trendPaint)
    }

    private fun drawCollecting(canvas: Canvas, w: Float, y: Float) {
        canvas.drawText(
            context.getString(R.string.chart_collecting),
            w / 2f,
            y + hintPaint.textSize / 3f,
            hintPaint,
        )
    }

    private fun dp(value: Float): Float = value * resources.displayMetrics.density

    /** Provider marks are loaded once and reused; onDraw runs on every scroll. */
    private fun icon(provider: ProviderId): Drawable? = icons.getOrPut(provider) {
        ContextCompat.getDrawable(context, ProviderArt.logo(provider))
    }

    private fun withAlpha(color: Int, alpha: Int): Int =
        (color and 0x00FFFFFF) or (alpha.coerceIn(0, 255) shl 24)

    private companion object {
        val DAY_FORMAT: DateTimeFormatter =
            DateTimeFormatter.ofPattern("d MMM", Locale.getDefault())
    }
}

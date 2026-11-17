package io.github.rzukic.usagetop.ui

import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import android.util.AttributeSet
import android.view.View
import androidx.core.content.ContextCompat
import io.github.rzukic.usagetop.R

/**
 * A limit's remaining level as a capsule, the counterpart of OpenUsage's
 * `LevelIndicator`: a rounded track with a fill that never shrinks below
 * [minFill], so a nearly empty window still reads as a sliver instead of
 * vanishing.
 */
class MeterView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
) : View(context, attrs) {

    private val trackPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = ContextCompat.getColor(context, R.color.meter_track)
    }

    private val fillPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = ContextCompat.getColor(context, R.color.meter_normal)
    }

    private val rect = RectF()

    private val minFill = MIN_FILL_DP * resources.displayMetrics.density

    /** Fraction of the window still available, 0-1. */
    var fill: Float = 0f
        set(value) {
            field = value.coerceIn(0f, 1f)
            invalidate()
        }

    var fillColor: Int = 0
        set(value) {
            field = value
            fillPaint.color = value
            invalidate()
        }

    override fun onDraw(canvas: Canvas) {
        val radius = minOf(height / 2f, CORNER_DP * resources.displayMetrics.density)

        rect.set(0f, 0f, width.toFloat(), height.toFloat())
        canvas.drawRoundRect(rect, radius, radius, trackPaint)

        if (fill <= 0f) return

        val filled = (width * fill).coerceAtLeast(minFill).coerceAtMost(width.toFloat())
        rect.set(0f, 0f, filled, height.toFloat())
        canvas.drawRoundRect(rect, radius, radius, fillPaint)
    }

    private companion object {
        const val MIN_FILL_DP = 4f
        const val CORNER_DP = 3f
    }
}

package io.github.rzukic.usagetop.ui

import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import android.graphics.drawable.Drawable

/**
 * The OpenUsage level ring: a dim track circle, a severity-coloured arc for the
 * remaining share, and the provider's mark in the middle.
 *
 * RemoteViews cannot draw an arc, so the widget gets a bitmap instead.
 */
object RingBitmap {

    fun draw(
        sizePx: Int,
        percent: Float?,
        color: Int,
        trackColor: Int,
        logo: Drawable?,
        logoColor: Int,
    ): Bitmap {
        val bitmap = Bitmap.createBitmap(sizePx, sizePx, Bitmap.Config.ARGB_8888)
        val canvas = Canvas(bitmap)

        val stroke = sizePx * 0.10f
        val inset = stroke / 2f
        val box = RectF(inset, inset, sizePx - inset, sizePx - inset)

        val arc = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.STROKE
            strokeWidth = stroke
            strokeCap = Paint.Cap.ROUND
        }

        arc.color = trackColor
        canvas.drawArc(box, 0f, 360f, false, arc)

        val sweep = (percent ?: 0f).coerceIn(0f, 100f) / 100f * 360f
        if (sweep > 0f) {
            arc.color = color
            canvas.drawArc(box, -90f, sweep, false, arc)
        }

        logo?.let {
            val target = (sizePx * 0.42f).toInt()
            val left = (sizePx - target) / 2
            it.setBounds(left, left, left + target, left + target)
            it.setTint(logoColor)
            it.draw(canvas)
        }

        return bitmap
    }
}

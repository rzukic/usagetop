package io.github.rzukic.usagetop.ui

import android.view.View
import android.view.animation.Animation
import android.view.animation.LinearInterpolator
import android.view.animation.RotateAnimation

/**
 * The refresh icon turns while a fetch is in flight, so a slow provider reads as
 * work in progress instead of a tap that did nothing.
 *
 * A view animation rather than a property animator: [View.startAnimation] replaces
 * whatever was already running and [View.clearAnimation] stops it, so no animator
 * has to be tracked per screen or cancelled when one closes.
 */
object RefreshSpinner {

    private const val TURN_MS = 900L

    fun start(view: View) {
        val spin = RotateAnimation(
            0f,
            360f,
            Animation.RELATIVE_TO_SELF,
            0.5f,
            Animation.RELATIVE_TO_SELF,
            0.5f,
        ).apply {
            duration = TURN_MS
            repeatCount = Animation.INFINITE
            interpolator = LinearInterpolator()
        }
        view.startAnimation(spin)
    }

    fun stop(view: View) {
        view.clearAnimation()
    }
}

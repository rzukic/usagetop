package io.github.rzukic.usagetop.ui

import io.github.rzukic.usagetop.data.Projection
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale

/** The two relative-time strings the app and the widget both need. */
object Format {

    private val DAY: DateTimeFormatter =
        DateTimeFormatter.ofPattern("d MMM", Locale.getDefault())

    /** A calendar day, e.g. "12 Nov.". */
    fun day(instant: Instant): String = DAY.format(instant.atZone(ZoneId.systemDefault()))

    private val WEEKDAY_TIME: DateTimeFormatter =
        DateTimeFormatter.ofPattern("EEE HH:mm", Locale.getDefault())
    private val DAY_TIME: DateTimeFormatter =
        DateTimeFormatter.ofPattern("d MMM HH:mm", Locale.getDefault())
    private val CLOCK: DateTimeFormatter =
        DateTimeFormatter.ofPattern("HH:mm", Locale.getDefault())

    /** A moment ahead: "Thu 21:54" within the week, "12 Nov 21:54" past it. */
    fun `when`(instant: Instant): String {
        val format = if (Duration.between(Instant.now(), instant) < Duration.ofDays(6)) WEEKDAY_TIME else DAY_TIME
        return format.format(instant.atZone(ZoneId.systemDefault()))
    }

    /** A clock time, e.g. "21:54". */
    fun clock(instant: Instant): String = CLOCK.format(instant.atZone(ZoneId.systemDefault()))

    /** A burn rate, e.g. "2.1%/h", or "14%/h" once it is large. */
    fun rate(perHour: Double): String =
        if (perHour < 10) String.format(Locale.getDefault(), "%.1f%%/h", perHour)
        else String.format(Locale.getDefault(), "%.0f%%/h", perHour)

    /**
     * "Burning 2.1%/h · usually 0.6%/h · " and "empty in 3h 20m, Thu 21:54" --
     * the rates the forecast is drawn from and where it ends up, apart so the
     * warning can be coloured. The server page words it the same way.
     */
    data class Burn(val rates: String, val outcome: String, val empties: Boolean)

    fun burn(track: Projection.Track, emptyAt: Instant?): Burn? {
        if (!track.measured) return null
        if (!track.draining) return Burn("Not draining lately", "", false)

        val hours = track.easeHours.let { if (it % 1.0 == 0.0) it.toLong().toString() else "%.1f".format(it) }
        val parts = mutableListOf<String>()
        track.recent?.let {
            parts += if (it > Projection.IDLE) "Burning ${rate(it)}" else "Idle for the last ${hours}h"
        }
        track.usual?.let { parts += if (parts.isEmpty()) "Usually ${rate(it)}" else "usually ${rate(it)}" }

        val outcome = when {
            emptyAt != null -> "empty in ${span(Duration.between(Instant.now(), emptyAt))}, ${`when`(emptyAt)}"
            track.nextReset != null -> "lasts past the reset"
            else -> "no reset reported"
        }
        return Burn(parts.joinToString(" · ") + " · ", outcome, emptyAt != null)
    }

    /** How long until a window resets, e.g. "3d 23h". */
    fun reset(resetAt: Instant?): String? {
        if (resetAt == null) return null
        val remaining = Duration.between(Instant.now(), resetAt)
        if (remaining.isNegative) return "now"
        return span(remaining)
    }

    /** A length of time in the same shape, e.g. "3d 23h" or "12m". */
    fun span(duration: Duration): String {
        val hours = duration.toHours()
        val minutes = duration.toMinutes() % 60
        return when {
            hours >= 24 -> {
                val days = duration.toDays()
                val rest = hours % 24
                if (rest == 0L) "${days}d" else "${days}d ${rest}h"
            }
            hours >= 1 -> "${hours}h ${minutes}m"
            else -> "${minutes}m"
        }
    }

    /** How long ago a reading was taken, e.g. "12m ago". */
    fun ago(instant: Instant): String {
        val minutes = Duration.between(instant, Instant.now()).toMinutes()
        return when {
            minutes < 1 -> "just now"
            minutes < 60 -> "${minutes}m ago"
            else -> "${minutes / 60}h ago"
        }
    }
}

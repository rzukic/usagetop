package io.github.rzukic.usagetop.data

import java.time.Duration
import java.time.Instant
import kotlin.math.exp
import kotlin.math.floor
import kotlin.math.max
import kotlin.math.min

/**
 * Where a window is heading, and the combined level carried forward one window
 * at a time. The server page works the same way (app.js), so the apps and the
 * page draw the same forecast.
 *
 * A window gets two burn rates from its own readings: the recent one, over the
 * last few hours, and the usual one, over the last week. Neither is the
 * forecast on its own. Use comes in bursts, so the recent rate alone says
 * "empty in two hours" after an hour of heavy work and "never" over lunch; the
 * usual rate already counts the nights and the idle stretches, but it is slow
 * to see a change of pace. So the line leaves now at the recent rate and eases
 * into the usual one:
 *
 *   rate(t) = usual + (recent - usual) * e^(-t / ease)
 *
 * with the ease as long as the recent rate was measured over: a rate taken over
 * three hours says something about the next few hours, and little about the
 * week after. A five-hour window is therefore forecast mostly on its recent
 * rate, a weekly one mostly on its usual one, without a rule for either.
 *
 * For the combined figure, every window drains at its own pace and the average
 * is taken afterwards: a single rate fitted to the average would claim Mistral
 * is spent as fast as Claude. A window that reaches zero stays at zero, and it
 * stays in the average -- "62% left" must never rise because something emptied.
 */
object Projection {

    /** How far back the recent rate looks, and so how long it holds sway. */
    val RECENT_BASIS: Duration = Duration.ofHours(3)

    /** How far back the usual rate looks: long enough to take in the nights. */
    val USUAL_BASIS: Duration = Duration.ofDays(7)

    /** How far ahead a window is looked at for when it runs dry. */
    val HORIZON: Duration = Duration.ofDays(35)

    /** Below this, in points an hour, a window counts as not draining. */
    const val IDLE = 0.05

    /** A jump this large is a reset, not a reading drifting. */
    private const val REFILL_JUMP = 1.5f

    /** How many samples a forward line is drawn from. */
    private const val STEPS = 160

    private const val MILLIS_PER_HOUR = 3_600_000.0

    /** One window, and how fast it is being spent. */
    data class Track(
        /** Points an hour over [RECENT_BASIS]; null with too little to go on. */
        val recent: Double?,
        /** Points an hour over [USUAL_BASIS]; null with too little to go on. */
        val usual: Double?,
        /** The level now, and when now is. */
        val percent: Double,
        val at: Instant,
        /** The next refill, and how often it repeats when the label says so. */
        val resetAt: Instant?,
        val period: Duration?,
        /** How quickly the recent rate gives way to the usual one, in hours. */
        val easeHours: Double = 3.0,
    ) {
        /** True when either rate says something, so there is a forecast to draw. */
        val measured: Boolean get() = recent != null || usual != null

        /** True when the forecast moves at all. */
        val draining: Boolean get() = (recent ?: 0.0) > IDLE || (usual ?: 0.0) > IDLE

        /** The next refill after now, if one is known. */
        val nextReset: Instant? get() = resetAt?.takeIf { it.isAfter(at) }
    }

    /**
     * A window's track. A window with no readings to go on still gets one -- it
     * holds its level -- so the combined average keeps counting it.
     */
    fun track(
        window: UsageWindow,
        history: List<UsagePoint>,
        now: Instant,
        recent: Duration = RECENT_BASIS,
    ): Track? {
        val percent = window.remainingPercent ?: return null
        return Track(
            recent = rateOver(history, recent, now),
            usual = rateOver(history, USUAL_BASIS, now),
            percent = percent.toDouble(),
            at = now,
            resetAt = window.resetAt,
            period = period(window.label),
            easeHours = recent.toMillis() / MILLIS_PER_HOUR,
        )
    }

    /**
     * Points of remaining-percent drained an hour over the last [basis], refills
     * left out. A level holds until the next reading. Null with under half an
     * hour of readings (or half the basis, if that is shorter) to go on.
     */
    fun rateOver(points: List<UsagePoint>, basis: Duration, now: Instant): Double? {
        if (points.isEmpty()) return null
        val cutoff = now.minus(basis)
        val start = if (points.first().at.isAfter(cutoff)) points.first().at else cutoff
        val hours = hours(start, now)
        if (hours < min(0.5, basis.toMillis() / MILLIS_PER_HOUR / 2)) return null

        var drained = 0.0
        for (i in 1 until points.size) {
            val at = points[i].at
            if (!at.isAfter(cutoff) || at.isAfter(now)) continue
            val step = points[i - 1].percent - points[i].percent
            if (-step >= REFILL_JUMP) continue
            drained += step
        }
        return max(drained / hours, 0.0)
    }

    /** Points drained from the track's now until [hours] later. */
    fun drained(track: Track, hours: Double): Double {
        if (hours <= 0) return 0.0
        val recent = track.recent ?: track.usual ?: 0.0
        val usual = track.usual ?: track.recent ?: 0.0
        val ease = max(track.easeHours, 0.01)
        return usual * hours + (recent - usual) * ease * (1 - exp(-hours / ease))
    }

    /** The points an hour the forecast falls at, [hours] from now. */
    fun rateAt(track: Track, hours: Double): Double {
        val recent = track.recent ?: track.usual ?: 0.0
        val usual = track.usual ?: track.recent ?: 0.0
        return usual + (recent - usual) * exp(-max(hours, 0.0) / max(track.easeHours, 0.01))
    }

    /** Where a window stands at [at], refilling at every reset on the way. */
    fun levelAt(track: Track, at: Instant): Double {
        if (!at.isAfter(track.at)) return track.percent
        val drained = drained(track, hours(track.at, at))
        val start = cycleStart(track, at)
        return if (start != null) {
            max(0.0, 100.0 - (drained - drained(track, hours(track.at, start))))
        } else {
            max(0.0, track.percent - drained)
        }
    }

    /** When the window runs dry before its next refill, looking no further than [until]. Null when it lasts. */
    fun emptyAt(track: Track, until: Instant): Instant? {
        if (track.percent <= 0) return track.at
        if (!track.draining) return null

        val reset = track.nextReset
        val end = if (reset != null && reset.isBefore(until)) reset else until
        if (!end.isAfter(track.at) || track.percent - drained(track, hours(track.at, end)) > 0) return null

        // The level only falls between refills, so halving the gap finds it.
        var low = 0.0
        var high = hours(track.at, end)
        repeat(48) {
            val middle = (low + high) / 2
            if (track.percent - drained(track, middle) > 0) low = middle else high = middle
        }
        return track.at.plusMillis((high * MILLIS_PER_HOUR).toLong())
    }

    /** One window's forecast from [from] to [to], with both sides of every refill so the line steps straight up. */
    fun path(track: Track, from: Instant, to: Instant): List<UsagePoint> =
        sample(listOf(track), from, to) { levelAt(track, it) }

    /**
     * The combined level from [from] to [to]. [anchor] is the last recorded
     * reading: a forecast is a shape, so it starts where the measured line ended
     * instead of at the model's own idea of the current level.
     */
    fun combined(
        tracks: List<Track>,
        from: Instant,
        to: Instant,
        anchor: Float? = null,
    ): List<UsagePoint> {
        if (tracks.isEmpty() || !to.isAfter(from)) return emptyList()

        val raw = sample(tracks, from, to) { at -> tracks.map { levelAt(it, at) }.average() }
        val offset = anchor?.let { it.toDouble() - raw.first().percent } ?: 0.0
        return raw.map { UsagePoint(it.at, (it.percent + offset).coerceIn(0.0, 100.0).toFloat()) }
    }

    private fun sample(
        tracks: List<Track>,
        from: Instant,
        to: Instant,
        level: (Instant) -> Double,
    ): List<UsagePoint> {
        if (!to.isAfter(from)) return emptyList()
        val span = Duration.between(from, to).toMillis()

        val times = sortedSetOf<Instant>()
        for (index in 0..STEPS) times.add(from.plusMillis(span * index / STEPS))

        // A refill is a step, not a slope: the moment before it and the moment of it.
        tracks.forEach { track ->
            resets(track, from, to).forEach { reset ->
                val just = reset.minusMillis(1)
                if (just.isAfter(from)) times.add(just)
                times.add(reset)
            }
        }

        return times.map { UsagePoint(it, level(it).toFloat()) }
    }

    /** Every refill of the window after [from], up to [to]. */
    fun resets(track: Track, from: Instant, to: Instant): List<Instant> {
        var at = track.nextReset ?: return emptyList()
        val list = mutableListOf<Instant>()
        var n = 0
        while (!at.isAfter(to) && n < 1000) {
            if (at.isAfter(from)) list.add(at)
            val period = track.period?.takeIf { !it.isZero && !it.isNegative } ?: break
            at = at.plus(period)
            n++
        }
        return list
    }

    /** The last refill at or before [at], or null if none has come yet. */
    private fun cycleStart(track: Track, at: Instant): Instant? {
        val first = track.nextReset ?: return null
        if (first.isAfter(at)) return null
        val period = track.period?.takeIf { !it.isZero && !it.isNegative } ?: return first
        val cycles = floor(Duration.between(first, at).toMillis().toDouble() / period.toMillis())
        return first.plusMillis((cycles * period.toMillis()).toLong())
    }

    private fun hours(from: Instant, to: Instant): Double =
        Duration.between(from, to).toMillis() / MILLIS_PER_HOUR

    /**
     * How often the window refills, when the label says so. The duration may
     * follow a name -- "Fable 7d" refills weekly just as "7d" does.
     */
    fun period(label: String): Duration? {
        val text = label.trim().lowercase()
        when (text) {
            "weekly" -> return Duration.ofDays(7)
            // Mistral's two budgets, "Vibe" and "API", run on its monthly billing cycle.
            "monthly", "vibe", "api" -> return Duration.ofDays(30)
        }
        val match = DURATION_LABEL.matchEntire(text) ?: return null
        val amount = match.groupValues[1].toLongOrNull() ?: return null
        return when (match.groupValues[2]) {
            "m" -> Duration.ofMinutes(amount)
            "h" -> Duration.ofHours(amount)
            "d" -> Duration.ofDays(amount)
            else -> null
        }
    }

    private val DURATION_LABEL = Regex("""(?:.*\s)?(\d+)\s*([mhd])""", RegexOption.IGNORE_CASE)
}

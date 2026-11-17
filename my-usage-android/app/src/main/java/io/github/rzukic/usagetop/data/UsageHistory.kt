package io.github.rzukic.usagetop.data

import android.content.Context
import org.json.JSONArray
import java.time.Duration
import java.time.Instant
import kotlin.math.abs

/** One reading of a window's remaining level. */
data class UsagePoint(val at: Instant, val percent: Float)

/**
 * A rolling record of how each window's remaining level has moved, so the detail
 * screen can draw a drawdown. Plain prefs: it holds percentages, no secrets.
 *
 * Two kinds of series live here. Per-window ones keep five weeks, so a monthly
 * allowance can show a whole cycle. The combined one keeps a quarter. Both thin
 * out with age, because every reading kept at full resolution would be thousands
 * of points no chart can show.
 */
class UsageHistory(context: Context) {

    private val prefs = context.applicationContext
        .getSharedPreferences("ai_usage_history", Context.MODE_PRIVATE)

    /** Appends the snapshot's readings, skipping ones that add no information. */
    fun record(snapshot: Snapshot) {
        val now = snapshot.fetchedAt
        val editor = prefs.edit()

        snapshot.usages
            .filter { it.state == UsageState.OK }
            .forEach { usage ->
                usage.windows.forEach windowLoop@{ window ->
                    val percent = window.remainingPercent ?: return@windowLoop
                    val key = key(usage.accountKey, window.label)
                    val series = read(key)
                    val kept = append(series, now, percent, RETAIN)
                    if (kept !== series) editor.putString(key, encode(kept))
                }
            }

        CombinedUsage.of(snapshot).percent?.let { percent ->
            editor.putString(COMBINED_KEY, encode(appendCombined(read(COMBINED_KEY), now, percent)))
        }

        editor.apply()
    }

    /**
     * Takes in what a server sent. Its readings replace ours from where they
     * start, and whatever we hold from before that is kept: a server set up
     * last week should not cost the month the app had already drawn.
     */
    fun merge(state: ServerState, now: Instant) {
        val editor = prefs.edit()
        state.series.forEach { (key, points) ->
            if (points.isNotEmpty()) editor.putString(key, encode(splice(read(key), points, now, RETAIN)))
        }
        if (state.combined.isNotEmpty()) {
            editor.putString(
                COMBINED_KEY,
                encode(splice(read(COMBINED_KEY), state.combined, now, RETAIN_COMBINED)),
            )
        }
        editor.apply()
    }

    /** One window's readings, optionally only the most recent [span]. */
    fun series(id: ProviderId, label: String, span: Duration? = null): List<UsagePoint> =
        series(id.key, label, span)

    /** One account's window, by its key: "claude" or "claude-2". */
    fun series(accountKey: String, label: String, span: Duration? = null): List<UsagePoint> {
        val series = read(key(accountKey, label))
        if (span == null) return series
        val since = Instant.now().minus(span)
        return series.filter { !it.at.isBefore(since) }
    }

    /** Every subscription's long windows averaged, up to a quarter back. */
    fun combined(): List<UsagePoint> = read(COMBINED_KEY)

    internal companion object {
        /** Five weeks: one full cycle of the longest window anyone sells, plus slack. */
        val RETAIN: Duration = Duration.ofDays(35)
        val RETAIN_COMBINED: Duration = Duration.ofDays(90)

        /**
         * Shorter than the 15-minute refresh period, so a window that never moves
         * still gains a reading on every refresh instead of waiting for a change
         * that may never come.
         */
        val SAMPLE_GAP: Duration = Duration.ofMinutes(5)

        /** A ceiling on any one series: 2016 + 552 + 240 at the tiers below. */
        const val MAX_POINTS = 3000

        const val COMBINED_KEY = "combined"

        /**
         * How far back one window's own chart looks. A window that refills within
         * a week shows a week; five weeks of a five-hour sawtooth would be a solid
         * block. Anything slower, or with no period in its label, shows everything
         * kept.
         */
        fun chartSpan(window: UsageWindow): Duration {
            val period = Projection.period(window.label)
            return if (period != null && period <= Duration.ofDays(7)) Duration.ofDays(7) else RETAIN
        }

        /**
         * [existing] up to where [incoming] begins, then [incoming]. The same
         * rule serves a first call that brings weeks and a later one that brings
         * the last few minutes.
         */
        fun splice(
            existing: List<UsagePoint>,
            incoming: List<UsagePoint>,
            now: Instant,
            retain: Duration,
        ): List<UsagePoint> {
            if (incoming.isEmpty()) return existing
            val from = incoming.first().at
            return compact(existing.filter { it.at.isBefore(from) } + incoming, now, retain)
        }

        /** The next combined series after a reading. */
        fun appendCombined(existing: List<UsagePoint>, now: Instant, percent: Float): List<UsagePoint> =
            append(existing, now, percent, RETAIN_COMBINED)

        /**
         * The next series after a reading. A reading that adds nothing is skipped
         * rather than appended: appending it and then thinning would drop the
         * previous point for being too close to the new one, so a run of quick
         * refreshes would keep the series at one point forever and the chart
         * would never leave "Collecting readings…". The same list comes back
         * untouched in that case, so the caller can tell there is nothing to save.
         */
        fun append(
            existing: List<UsagePoint>,
            now: Instant,
            percent: Float,
            retain: Duration,
        ): List<UsagePoint> {
            val last = existing.lastOrNull()

            // A flat reading a minute after the last one is noise; a flat reading
            // an hour later is the shape of the drawdown.
            val worthKeeping = last == null ||
                abs(last.percent - percent) >= 0.5f ||
                Duration.between(last.at, now) >= SAMPLE_GAP
            if (!worthKeeping) return existing
            return compact(existing + UsagePoint(now, percent), now, retain)
        }

        /**
         * Thins the series out with age: every reading for the last week, hourly for
         * the month before it, six-hourly for the rest. Walking backwards means the
         * newest reading always survives and the gaps only ever grow towards the past.
         */
        fun compact(
            points: List<UsagePoint>,
            now: Instant,
            retain: Duration = RETAIN_COMBINED,
        ): List<UsagePoint> {
            val kept = ArrayList<UsagePoint>(points.size)
            var newer: UsagePoint? = null

            for (point in points.asReversed()) {
                val age = Duration.between(point.at, now)
                if (age > retain) continue
                if (newer == null || Duration.between(point.at, newer.at) >= gapFor(age)) {
                    kept.add(point)
                    newer = point
                }
            }

            kept.reverse()
            return kept.takeLast(MAX_POINTS)
        }

        private fun gapFor(age: Duration): Duration = when {
            age < Duration.ofDays(7) -> SAMPLE_GAP
            age < Duration.ofDays(30) -> Duration.ofHours(1)
            else -> Duration.ofHours(6)
        }
    }

    private fun read(key: String): List<UsagePoint> {
        val raw = prefs.getString(key, null) ?: return emptyList()
        return runCatching {
            val array = JSONArray(raw)
            (0 until array.length()).mapNotNull { index ->
                val point = array.optJSONArray(index) ?: return@mapNotNull null
                UsagePoint(
                    at = Instant.ofEpochMilli(point.optLong(0)),
                    percent = point.optDouble(1).toFloat(),
                )
            }
        }.getOrDefault(emptyList())
    }

    private fun encode(points: List<UsagePoint>): String {
        val array = JSONArray()
        points.forEach { point ->
            array.put(JSONArray().put(point.at.toEpochMilli()).put(point.percent.toDouble()))
        }
        return array.toString()
    }

    private fun key(accountKey: String, label: String) = "$accountKey|$label"
}

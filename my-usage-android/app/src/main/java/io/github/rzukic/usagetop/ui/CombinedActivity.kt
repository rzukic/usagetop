package io.github.rzukic.usagetop.ui

import android.content.SharedPreferences
import android.os.Bundle
import android.view.View
import android.widget.ImageView
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.lifecycle.lifecycleScope
import io.github.rzukic.usagetop.R
import io.github.rzukic.usagetop.data.CombinedUsage
import io.github.rzukic.usagetop.data.ProviderId
import io.github.rzukic.usagetop.data.ProviderUsage
import io.github.rzukic.usagetop.data.Projection
import io.github.rzukic.usagetop.data.UsageHistory
import io.github.rzukic.usagetop.data.UsagePoint
import io.github.rzukic.usagetop.data.UsageRepository
import io.github.rzukic.usagetop.data.UsageWindow
import io.github.rzukic.usagetop.databinding.ActivityCombinedBinding
import io.github.rzukic.usagetop.widget.UsageWidgetProvider
import kotlinx.coroutines.launch
import java.time.Duration
import java.time.Instant
import kotlin.math.roundToInt

/**
 * Every subscription's long window averaged into one drawdown, over a range the
 * user picks. The main screen answers "am I about to run out"; this answers "how
 * much of everything do I have left, and how fast is it going".
 */
class CombinedActivity : AppCompatActivity() {

    private lateinit var binding: ActivityCombinedBinding
    private val history by lazy { UsageHistory(this) }
    private val repository by lazy { UsageRepository(this) }
    private var cacheWatcher: SharedPreferences.OnSharedPreferenceChangeListener? = null
    private var refreshing = false
    private var range = 0

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityCombinedBinding.inflate(layoutInflater)
        setContentView(binding.root)

        binding.btnBack.setOnClickListener { finish() }
        binding.btnRefresh.setOnClickListener { refresh(force = true) }
        binding.range7d.setOnClickListener { selectRange(0) }
        binding.range30d.setOnClickListener { selectRange(1) }
        binding.range3m.setOnClickListener { selectRange(2) }

        selectRange(0)
    }

    /** A refresh from anywhere else in the app redraws this screen too. */
    override fun onResume() {
        super.onResume()
        render()
        cacheWatcher = repository.watch { render() }
        if (!repository.isFresh()) refresh()
    }

    override fun onPause() {
        super.onPause()
        cacheWatcher?.let(repository::unwatch)
        cacheWatcher = null
    }

    /** A fresh fetch, then the same render path the screen opened with. */
    private fun refresh(force: Boolean = false) {
        if (refreshing) return
        refreshing = true
        binding.btnRefresh.isEnabled = false
        RefreshSpinner.start(binding.btnRefresh)

        lifecycleScope.launch {
            try {
                repository.refresh(force)
            } catch (t: Throwable) {
                // The cached reading stands; render() shows what it has.
            }
            refreshing = false
            binding.btnRefresh.isEnabled = true
            RefreshSpinner.stop(binding.btnRefresh)
            render()
            UsageWidgetProvider.render(this@CombinedActivity)
        }
    }

    private fun selectRange(index: Int) {
        range = index
        listOf(binding.range7d, binding.range30d, binding.range3m)
            .forEachIndexed { position, view -> styleSegment(view, position == index) }
        render()
    }

    private fun styleSegment(view: TextView, selected: Boolean) {
        view.setBackgroundResource(if (selected) R.drawable.segment_thumb else 0)
        view.setTextColor(
            ContextCompat.getColor(this, if (selected) R.color.text_primary else R.color.text_secondary),
        )
        view.typeface = if (selected) {
            android.graphics.Typeface.create("sans-serif-medium", android.graphics.Typeface.NORMAL)
        } else {
            android.graphics.Typeface.create("sans-serif", android.graphics.Typeface.NORMAL)
        }
    }

    private fun render() {
        val snapshot = repository.cached()
        val combined = snapshot?.let { CombinedUsage.of(it) }
        val percent = combined?.percent

        binding.combinedPercent.text = percent?.let { it.roundToInt().toString() } ?: "—"
        binding.combinedPercent.setTextColor(
            percent?.let { Severity.textColor(this, it) }
                ?: ContextCompat.getColor(this, R.color.text_tertiary),
        )
        binding.combinedCaption.text = when {
            combined == null || combined.providers == 0 -> getString(R.string.combined_none)
            else -> getString(R.string.combined_caption, combined.providers)
        }

        val span = SPANS[range]
        val now = Instant.now()
        val points = history.combined().filter { Duration.between(it.at, now) <= span }

        // Each window is carried forward on its own burn, the recent rate easing
        // into the usual one. One rate fitted to the average would claim Mistral
        // is being spent as fast as Claude, which is what made the numbers nonsense.
        val contributions = snapshot?.let { CombinedUsage.contributions(it) }.orEmpty()
        val tracks = contributions.mapNotNull { contribution ->
            Projection.track(
                contribution.window,
                history.series(contribution.account, contribution.window.label, Projection.USUAL_BASIS),
                now,
            )
        }

        val resets = contributions
            .mapNotNull { contribution ->
                val at = contribution.window.resetAt ?: return@mapNotNull null
                if (!at.isAfter(now)) return@mapNotNull null
                at to contribution.provider
            }
            .groupBy({ it.first }, { it.second })
            .map { (at, providers) -> DrawdownChart.Reset(at, providers.distinct()) }
            .sortedBy { it.at }

        val projection = Projection.combined(
            tracks = tracks,
            from = now,
            to = now.plus(span.dividedBy(2)),
            anchor = points.lastOrNull()?.percent,
        )

        binding.combinedChart.setSeries(
            points,
            Severity.color(this, percent ?: 100f),
            span,
            resets,
            projection,
        )

        // The chart projects the fitted burn rate past now, so the caption can
        // answer the question the screen exists for: when does it run out?
        val exhaustion = binding.combinedChart.projectedExhaustion()
        binding.combinedCoverage.text = when {
            points.size < 2 -> ""
            exhaustion == null -> getString(R.string.combined_projection_flat)
            else -> getString(R.string.combined_projection, Format.day(exhaustion))
        }

        renderProviders(snapshot?.usages.orEmpty())
    }

    private fun renderProviders(usages: List<ProviderUsage>) {
        binding.combinedProviders.removeAllViews()

        var rows = 0
        usages.forEach { usage ->
            val id = usage.id
            val windows = CombinedUsage.longWindows(usage)
            val percent = CombinedUsage.level(windows) ?: return@forEach

            val row = layoutInflater.inflate(
                R.layout.combined_provider_row,
                binding.combinedProviders,
                false,
            )
            row.findViewById<ImageView>(R.id.row_logo).setImageResource(ProviderArt.logo(id))
            row.findViewById<TextView>(R.id.row_name).text = usage.displayName
            row.findViewById<TextView>(R.id.row_window).text = describe(windows)

            row.findViewById<TextView>(R.id.row_percent).apply {
                text = getString(R.string.percent_left, percent.roundToInt())
                setTextColor(Severity.textColor(this@CombinedActivity, percent))
            }
            row.findViewById<MeterView>(R.id.row_meter).apply {
                fill = percent / 100f
                fillColor = Severity.color(this@CombinedActivity, percent)
            }
            row.setOnClickListener { startActivity(ProviderDetailActivity.intent(this, usage.accountKey)) }

            binding.combinedProviders.addView(row)
            rows++
        }

        if (rows == 0) {
            binding.combinedProviders.addView(
                TextView(this).apply {
                    text = getString(R.string.combined_none)
                    setTextColor(ContextCompat.getColor(context, R.color.text_tertiary))
                    textSize = 12f
                    setPadding(0, dp(8), 0, dp(8))
                },
            )
        }
    }

    /** "7d · resets 3d 4h", or just the labels when the provider gives no reset. */
    private fun describe(windows: List<UsageWindow>): String {
        val labels = windows.joinToString(" · ") { it.label }.takeIf { it.isNotBlank() }
        val reset = windows.mapNotNull { it.resetAt }.maxOrNull()
            ?.let { Format.reset(it) }
            ?.let { getString(R.string.resets_in, it) }
        return listOfNotNull(labels, reset).joinToString(" · ")
    }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).roundToInt()

    private companion object {
        val SPANS = listOf(
            Duration.ofDays(7),
            Duration.ofDays(30),
            Duration.ofDays(90),
        )
    }
}

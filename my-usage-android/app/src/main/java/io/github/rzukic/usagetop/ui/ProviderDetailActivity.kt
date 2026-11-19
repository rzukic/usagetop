package io.github.rzukic.usagetop.ui

import android.content.SharedPreferences
import android.os.Bundle
import android.text.SpannableStringBuilder
import android.text.Spanned
import android.text.style.ForegroundColorSpan
import android.view.View
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.lifecycle.lifecycleScope
import io.github.rzukic.usagetop.R
import io.github.rzukic.usagetop.data.Accounts
import io.github.rzukic.usagetop.data.HiddenLimitStore
import io.github.rzukic.usagetop.data.HiddenLimits
import io.github.rzukic.usagetop.data.Projection
import io.github.rzukic.usagetop.data.ProviderId
import io.github.rzukic.usagetop.data.ProviderUsage
import io.github.rzukic.usagetop.data.UsageHistory
import io.github.rzukic.usagetop.data.UsageRepository
import io.github.rzukic.usagetop.data.UsageState
import io.github.rzukic.usagetop.data.UsageWindow
import io.github.rzukic.usagetop.databinding.ActivityProviderDetailBinding
import io.github.rzukic.usagetop.widget.UsageWidgetProvider
import kotlinx.coroutines.launch
import java.time.Instant
import kotlin.math.roundToInt

/**
 * One provider in full: its tier, every window, and how each window has drawn
 * down over time. The main screen stays a summary; the money figures and the
 * history live here.
 */
class ProviderDetailActivity : AppCompatActivity() {

    private lateinit var binding: ActivityProviderDetailBinding
    private lateinit var id: ProviderId

    /** "claude", or "claude-2" for an account added on the server. */
    private lateinit var account: String
    private val repository by lazy { UsageRepository(this) }
    private val hiddenLimits by lazy { HiddenLimitStore(this) }
    private var cacheWatcher: SharedPreferences.OnSharedPreferenceChangeListener? = null
    private var refreshing = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val parsed = Accounts.parse(intent.getStringExtra(EXTRA_PROVIDER))
        if (parsed == null) {
            finish()
            return
        }
        id = parsed.first
        account = Accounts.keyOf(parsed.first, parsed.second)

        binding = ActivityProviderDetailBinding.inflate(layoutInflater)
        setContentView(binding.root)

        binding.btnBack.setOnClickListener { finish() }
        binding.btnRefresh.setOnClickListener { refresh(force = true) }
        binding.detailName.text = repository.cached()?.usages
            ?.firstOrNull { it.accountKey == account }?.displayName ?: id.displayName

        render()
    }

    /** Same cache as the widget and the dashboard, so it shows the same reading. */
    override fun onResume() {
        super.onResume()
        if (!refreshing) render()
        cacheWatcher = repository.watch {
            if (!refreshing) render()
        }
        if (!repository.isFresh()) refresh()
    }

    override fun onPause() {
        super.onPause()
        cacheWatcher?.let(repository::unwatch)
        cacheWatcher = null
    }

    private fun render() {
        val snapshot = repository.cached()
        val usage = snapshot?.usages?.firstOrNull { it.accountKey == account }
        val history = UsageHistory(this)

        val plan = usage?.plan
        binding.detailPlan.text = plan.orEmpty()
        binding.detailPlan.visibility = if (plan.isNullOrBlank()) View.GONE else View.VISIBLE
        binding.detailUpdated.text = describe(usage)

        binding.windowContainer.removeAllViews()
        // The shown limits in their usual order, then the hidden ones.
        val windows = usage?.let { HiddenLimits.shownFirst(it, hiddenLimits.all()) }.orEmpty()
        if (windows.isEmpty()) {
            binding.windowContainer.addView(note(usage))
        } else {
            windows.forEach { window ->
                val card = layoutInflater.inflate(
                    R.layout.detail_window_card,
                    binding.windowContainer,
                    false,
                )
                bindWindow(card, window, history)
                binding.windowContainer.addView(card)
            }
        }
    }

    /** A fresh fetch, then the same render path the screen opened with. */
    private fun refresh(force: Boolean = false) {
        if (refreshing) return
        refreshing = true
        binding.btnRefresh.isEnabled = false
        binding.detailUpdated.text = getString(R.string.refreshing)
        RefreshSpinner.start(binding.btnRefresh)

        lifecycleScope.launch {
            try {
                repository.refresh(force)
            } catch (t: Throwable) {
                // The cached reading stands; render() reports what it has.
            }
            refreshing = false
            binding.btnRefresh.isEnabled = true
            RefreshSpinner.stop(binding.btnRefresh)
            render()
            UsageWidgetProvider.render(this@ProviderDetailActivity)
        }
    }

    private fun bindWindow(card: View, window: UsageWindow, history: UsageHistory) {
        // The same forecast the server page draws: the recent burn easing into
        // the usual one, from a week of this window's own readings.
        val now = Instant.now()
        val span = UsageHistory.chartSpan(window)
        val series = history.series(account, window.label, span)
        val track = Projection.track(window, history.series(account, window.label, Projection.USUAL_BASIS), now)

        card.findViewById<TextView>(R.id.window_label).text =
            window.label.ifBlank { getString(R.string.usage) }

        // Hidden: still here, dimmed and marked; a long press on the card brings it back.
        val key = HiddenLimits.key(account, window.label)
        val hidden = key in hiddenLimits.all()
        card.findViewById<TextView>(R.id.window_hidden).visibility = if (hidden) View.VISIBLE else View.GONE
        listOf(R.id.window_label, R.id.window_percent, R.id.window_reset, R.id.window_burn, R.id.window_chart, R.id.window_detail)
            .forEach { card.findViewById<View>(it).alpha = if (hidden) 0.45f else 1f }
        HideMenu.attach(card, window.label.ifBlank { getString(R.string.usage) }, hidden) {
            // At once on screen; with a server keeping the list, then there too.
            lifecycleScope.launch {
                val redraw = {
                    render()
                    UsageWidgetProvider.render(this@ProviderDetailActivity)
                }
                val problem = hiddenLimits.set(key, !hidden, redraw)
                if (problem != null) repository.reportServerProblem(problem)
                redraw()
            }
        }

        val percent = window.remainingPercent
        val value = card.findViewById<TextView>(R.id.window_percent)
        val chart = card.findViewById<DrawdownChart>(R.id.window_chart)
        val reset = card.findViewById<TextView>(R.id.window_reset)
        val detail = card.findViewById<TextView>(R.id.window_detail)

        if (percent != null) {
            value.text = getString(R.string.percent_left, percent.roundToInt())
            value.setTextColor(Severity.textColor(this, percent))
            chart.setWindow(series, Severity.color(this, percent), track, span)
        } else {
            value.text = window.detail ?: "—"
            value.setTextColor(ContextCompat.getColor(this, R.color.text_secondary))
            chart.setSeries(series, ContextCompat.getColor(this, R.color.meter_normal))
        }

        reset.text = Format.reset(window.resetAt).orEmpty()

        val burn = card.findViewById<TextView>(R.id.window_burn)
        val words = track?.let { Format.burn(it, Projection.emptyAt(it, now.plus(Projection.HORIZON))) }
        if (words != null) {
            val text = SpannableStringBuilder(words.rates).append(words.outcome)
            if (words.empties) {
                text.setSpan(
                    ForegroundColorSpan(ContextCompat.getColor(this, R.color.meter_warning)),
                    words.rates.length,
                    text.length,
                    Spanned.SPAN_EXCLUSIVE_EXCLUSIVE,
                )
            }
            burn.text = text
            burn.visibility = View.VISIBLE
        } else {
            burn.visibility = View.GONE
        }

        if (percent != null && window.detail != null) {
            detail.text = window.detail
            detail.visibility = View.VISIBLE
        } else {
            detail.visibility = View.GONE
        }
    }

    private fun describe(usage: ProviderUsage?): String = when {
        usage == null -> getString(R.string.not_configured)
        usage.state == UsageState.NOT_CONFIGURED -> usage.message ?: getString(R.string.not_configured)
        usage.state == UsageState.ERROR -> usage.message ?: getString(R.string.refresh_failed)
        // Numbers kept from an earlier fetch say why they are old, not just how old.
        usage.stale && usage.message != null ->
            getString(R.string.stale_reason, usage.message, Format.ago(usage.fetchedAt))
        else -> getString(R.string.updated_ago, Format.ago(usage.fetchedAt))
    }

    /** A dim line standing in for the cards a provider has not reported yet. */
    private fun note(usage: ProviderUsage?): TextView = TextView(this).apply {
        text = when {
            usage == null -> getString(R.string.not_configured)
            usage.state == UsageState.NOT_CONFIGURED -> usage.message ?: getString(R.string.not_configured)
            else -> usage.message ?: getString(R.string.refresh_failed)
        }
        setTextColor(ContextCompat.getColor(context, R.color.text_tertiary))
        textSize = 12f
        setPadding(0, dp(8), 0, dp(8))
    }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).roundToInt()

    companion object {
        const val EXTRA_PROVIDER = "provider"

        /** [account] is "claude", or "claude-2"; the extra kept its old name so old widget taps still open. */
        fun intent(context: android.content.Context, account: String): android.content.Intent =
            android.content.Intent(context, ProviderDetailActivity::class.java)
                .putExtra(EXTRA_PROVIDER, account)

        fun intent(context: android.content.Context, id: ProviderId): android.content.Intent = intent(context, id.key)
    }
}

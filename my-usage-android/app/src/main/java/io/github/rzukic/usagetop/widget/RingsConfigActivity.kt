package io.github.rzukic.usagetop.widget

import android.app.Activity
import android.appwidget.AppWidgetManager
import android.content.Intent
import android.content.res.ColorStateList
import android.graphics.drawable.GradientDrawable
import android.os.Bundle
import android.view.Gravity
import android.view.View
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import com.google.android.material.button.MaterialButton
import io.github.rzukic.usagetop.R
import io.github.rzukic.usagetop.data.ProviderUsage
import io.github.rzukic.usagetop.data.UsageRepository
import io.github.rzukic.usagetop.data.UsageWindow
import io.github.rzukic.usagetop.ui.ProviderArt
import kotlin.math.roundToInt

/**
 * The rings widget's settings: every window of every account, pick up to four,
 * each once. Tapping picks it next or takes it out; the numbers are the order
 * the rings appear in. Opens when the widget is placed on Android 11 and older,
 * and from its long-press menu from Android 12.
 */
class RingsConfigActivity : Activity() {

    private var widgetId = AppWidgetManager.INVALID_APPWIDGET_ID
    private val picks = mutableListOf<RingPicks.Pick>()
    private val rows = mutableListOf<Pair<RingPicks.Pick, View>>()
    private lateinit var count: TextView
    private lateinit var done: MaterialButton

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        widgetId = intent?.extras?.getInt(AppWidgetManager.EXTRA_APPWIDGET_ID, AppWidgetManager.INVALID_APPWIDGET_ID)
            ?: AppWidgetManager.INVALID_APPWIDGET_ID
        // Leaving without Done keeps what the widget had.
        setResult(RESULT_CANCELED, result())
        if (widgetId == AppWidgetManager.INVALID_APPWIDGET_ID) {
            finish()
            return
        }

        val snapshot = UsageRepository(this).cached()
        val choices = RingPicks.choices(snapshot)
        picks += (RingPicks.read(this, widgetId) ?: RingPicks.defaults(snapshot))
            .filter { pick -> choices.any { (usage, window) -> RingPicks.Pick.of(usage, window) == pick } }

        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(18), dp(20), dp(18), dp(28))
        }

        val header = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        header.addView(text(getString(R.string.rings_title), 20f, R.color.text_primary, medium = true),
            LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        count = text("", 13f, R.color.text_secondary)
        header.addView(count)
        column.addView(header)

        column.addView(text(getString(R.string.rings_hint), 13f, R.color.text_secondary).apply {
            setPadding(0, dp(6), 0, dp(14))
        })

        if (choices.isEmpty()) {
            column.addView(text(getString(R.string.rings_nothing), 14f, R.color.text_secondary))
        }

        val card = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            background = getDrawable(R.drawable.card_surface)
            setPadding(dp(14), dp(4), dp(14), dp(4))
        }
        choices.forEach { (usage, window) ->
            val pick = RingPicks.Pick.of(usage, window)
            val row = row(usage, window)
            row.setOnClickListener { toggle(pick) }
            rows += pick to row
            card.addView(row)
        }
        if (choices.isNotEmpty()) column.addView(card)

        done = MaterialButton(this, null, com.google.android.material.R.attr.materialButtonStyle).apply {
            text = getString(R.string.rings_done)
            isAllCaps = false
            setOnClickListener { save() }
        }
        column.addView(done, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(44)).apply {
            topMargin = dp(18)
        })

        setContentView(ScrollView(this).apply {
            setBackgroundColor(getColor(R.color.panel))
            addView(column)
        })
        refresh()
    }

    private fun row(usage: ProviderUsage, window: UsageWindow): View {
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            minimumHeight = dp(48)
        }
        row.addView(ImageView(this).apply {
            setImageResource(ProviderArt.logo(usage.id))
            imageTintList = ColorStateList.valueOf(getColor(R.color.text_primary))
        }, LinearLayout.LayoutParams(dp(18), dp(18)))

        val label = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        label.addView(text(usage.displayName, 15f, R.color.text_primary))
        label.addView(text("  " + WidgetModel.name(window), 15f, R.color.text_secondary))
        row.addView(label, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f).apply {
            marginStart = dp(12)
        })

        window.remainingPercent?.let {
            row.addView(text("${it.roundToInt()}%", 13f, R.color.text_secondary).apply {
                setPadding(0, 0, dp(12), 0)
            })
        }
        row.addView(TextView(this).apply {
            tag = MARK
            gravity = Gravity.CENTER
            textSize = 12f
            setTextColor(getColor(R.color.panel))
        }, LinearLayout.LayoutParams(dp(22), dp(22)))
        return row
    }

    private fun toggle(pick: RingPicks.Pick) {
        when {
            pick in picks -> picks.remove(pick)
            picks.size < RingPicks.MAX -> picks.add(pick)
            else -> return
        }
        refresh()
    }

    private fun refresh() {
        count.text = getString(R.string.rings_count, picks.size, RingPicks.MAX)
        val full = picks.size >= RingPicks.MAX
        rows.forEach { (pick, row) ->
            val position = picks.indexOf(pick)
            val mark = row.findViewWithTag<TextView>(MARK)
            if (position >= 0) {
                mark.text = "${position + 1}"
                mark.background = GradientDrawable().apply {
                    shape = GradientDrawable.OVAL
                    setColor(getColor(R.color.meter_normal))
                }
            } else {
                mark.text = ""
                mark.background = GradientDrawable().apply {
                    shape = GradientDrawable.OVAL
                    setStroke(dp(1.5f), getColor(R.color.text_tertiary))
                }
            }
            // Four picked: the rest wait until one is taken out.
            val open = position >= 0 || !full
            row.alpha = if (open) 1f else 0.38f
            row.isEnabled = open
        }
        done.isEnabled = picks.isNotEmpty()
        done.alpha = if (picks.isNotEmpty()) 1f else 0.5f
    }

    private fun save() {
        RingPicks.write(this, widgetId, picks)
        UsageWidgetProvider.render(this)
        setResult(RESULT_OK, result())
        finish()
    }

    private fun result() = Intent().putExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, widgetId)

    private fun text(value: String, size: Float, color: Int, medium: Boolean = false) = TextView(this).apply {
        text = value
        textSize = size
        setTextColor(getColor(color))
        if (medium) typeface = android.graphics.Typeface.create("sans-serif-medium", android.graphics.Typeface.NORMAL)
    }

    private fun dp(value: Int) = (value * resources.displayMetrics.density).roundToInt()
    private fun dp(value: Float) = (value * resources.displayMetrics.density).roundToInt()

    private companion object {
        const val MARK = "mark"
    }
}

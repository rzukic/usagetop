package io.github.rzukic.usagetop.widget

import android.app.Activity
import android.appwidget.AppWidgetManager
import android.content.Intent
import android.content.res.ColorStateList
import android.graphics.Typeface
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
import io.github.rzukic.usagetop.data.HiddenLimitStore
import io.github.rzukic.usagetop.data.HiddenLimits
import io.github.rzukic.usagetop.data.UsageRepository
import io.github.rzukic.usagetop.data.UsageState
import io.github.rzukic.usagetop.ui.ProviderArt
import kotlin.math.roundToInt

/**
 * The usage widget's settings: every limit of every account, each on or off for
 * this widget. It starts as the app shows them, so a limit hidden in the app
 * starts off, marked as such, and can be turned on here for this widget alone.
 * Among what is on, the widget still puts the account closest to empty first;
 * an account with nothing on drops out of it.
 */
class UsageConfigActivity : Activity() {

    private var widgetId = AppWidgetManager.INVALID_APPWIDGET_ID
    private val shown = linkedMapOf<String, Boolean>()
    private val marks = mutableMapOf<String, TextView>()
    private var appHidden: Set<String> = emptySet()

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
        appHidden = HiddenLimitStore(this).all()
        val hidden = LimitPicks.hiddenFor(appHidden, LimitPicks.read(this, widgetId))
        val accounts = snapshot?.usages.orEmpty().filter { it.state == UsageState.OK && it.windows.isNotEmpty() }

        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(18), dp(20), dp(18), dp(28))
        }
        column.addView(text(getString(R.string.usage_config_title), 20f, R.color.text_primary, medium = true))
        column.addView(text(getString(R.string.usage_config_hint), 13f, R.color.text_secondary).apply {
            setPadding(0, dp(6), 0, dp(10))
        })
        if (accounts.isEmpty()) {
            column.addView(text(getString(R.string.rings_nothing), 14f, R.color.text_secondary))
        }

        accounts.forEach { usage ->
            column.addView(LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                setPadding(dp(2), dp(14), 0, dp(6))
                addView(ImageView(context).apply {
                    setImageResource(ProviderArt.logo(usage.id))
                    imageTintList = ColorStateList.valueOf(getColor(R.color.text_secondary))
                }, LinearLayout.LayoutParams(dp(14), dp(14)))
                addView(text(usage.displayName, 13f, R.color.text_secondary, medium = true).apply {
                    setPadding(dp(8), 0, 0, 0)
                })
            })

            val card = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                background = getDrawable(R.drawable.card_surface)
                setPadding(dp(14), dp(2), dp(14), dp(2))
            }
            usage.windows.forEach { window ->
                val key = HiddenLimits.key(usage, window)
                shown[key] = key !in hidden
                card.addView(row(key, WidgetModel.name(window), window.remainingPercent, key in appHidden))
            }
            column.addView(card)
        }

        column.addView(MaterialButton(this, null, com.google.android.material.R.attr.materialButtonStyle).apply {
            text = getString(R.string.rings_done)
            isAllCaps = false
            setOnClickListener { save() }
        }, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(44)).apply { topMargin = dp(18) })

        setContentView(ScrollView(this).apply {
            setBackgroundColor(getColor(R.color.panel))
            addView(column)
        })
        refresh()
    }

    private fun row(key: String, name: String, percent: Float?, hiddenInApp: Boolean): View {
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            minimumHeight = dp(48)
            setOnClickListener {
                shown[key] = shown[key] != true
                refresh()
            }
        }
        val label = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        label.addView(text(name, 15f, R.color.text_primary))
        if (hiddenInApp) label.addView(text(getString(R.string.usage_config_hidden_in_app), 11f, R.color.text_tertiary))
        row.addView(label, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        percent?.let {
            row.addView(text("${it.roundToInt()}%", 13f, R.color.text_secondary).apply { setPadding(0, 0, dp(12), 0) })
        }
        val mark = TextView(this).apply {
            gravity = Gravity.CENTER
            textSize = 12f
            setTextColor(getColor(R.color.panel))
        }
        marks[key] = mark
        row.addView(mark, LinearLayout.LayoutParams(dp(22), dp(22)))
        return row
    }

    private fun refresh() {
        marks.forEach { (key, mark) ->
            val on = shown[key] == true
            mark.text = if (on) "✓" else ""
            mark.background = GradientDrawable().apply {
                shape = GradientDrawable.OVAL
                if (on) setColor(getColor(R.color.meter_normal)) else setStroke(dp(1.5f), getColor(R.color.text_tertiary))
            }
            mark.contentDescription = getString(if (on) R.string.usage_config_on else R.string.usage_config_off)
        }
    }

    private fun save() {
        LimitPicks.write(this, widgetId, LimitPicks.overridesFrom(appHidden, shown))
        UsageWidgetProvider.render(this)
        setResult(RESULT_OK, result())
        finish()
    }

    private fun result() = Intent().putExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, widgetId)

    private fun text(value: String, size: Float, color: Int, medium: Boolean = false) = TextView(this).apply {
        text = value
        textSize = size
        setTextColor(getColor(color))
        if (medium) typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
    }

    private fun dp(value: Int) = (value * resources.displayMetrics.density).roundToInt()
    private fun dp(value: Float) = (value * resources.displayMetrics.density).roundToInt()
}

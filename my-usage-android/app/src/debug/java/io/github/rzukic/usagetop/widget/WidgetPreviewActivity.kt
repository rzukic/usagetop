package io.github.rzukic.usagetop.widget

import android.app.Activity
import android.content.Context
import android.content.res.Configuration
import android.graphics.drawable.GradientDrawable
import android.os.Bundle
import android.view.View
import android.view.ViewGroup
import android.widget.BaseAdapter
import android.widget.LinearLayout
import android.widget.ListView
import android.widget.ScrollView
import io.github.rzukic.usagetop.R
import io.github.rzukic.usagetop.data.UsageRepository
import kotlin.math.roundToInt

/**
 * Draws both widgets at the sizes a phone's launcher gives them, from the cached
 * reading, on a wallpaper-like backdrop: the rings at one and four picks, then
 * the usage widget at 2x2, 4x2 and 4x4. One theme at a time, for screenshots:
 * `adb shell am start -n <package>/io.github.rzukic.usagetop.widget.WidgetPreviewActivity --ez dark true`.
 */
class WidgetPreviewActivity : Activity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val snapshot = UsageRepository(this).cached()
        val dark = intent.getBooleanExtra("dark", isDark())
        val themed = themed(dark)

        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            background = GradientDrawable(
                GradientDrawable.Orientation.TL_BR,
                intArrayOf(0xFF2E66C9.toInt(), 0xFF8FB0E0.toInt(), 0xFF2A5DBF.toInt()),
            )
            setPadding(dp(12), dp(16), dp(12), dp(16))
        }
        // About a 4x5 home screen on a 411 dp wide phone: four cells of ~97 dp.
        val width = resources.displayMetrics.widthPixels - dp(24)
        val cell = (width - dp(12)) / 2

        val picks = RingPicks.defaults(snapshot)
        column.addView(pair(
            RingsWidgetProvider.build(themed, snapshot, picks.take(1)).apply(themed, column),
            RingsWidgetProvider.build(themed, snapshot, picks).apply(themed, column),
            cell,
        ))
        column.addView(gap())
        column.addView(pair(WidgetRenderer.small(themed, snapshot).apply(themed, column), null, cell))
        column.addView(gap())
        column.addView(WidgetRenderer.medium(themed, snapshot).apply(themed, column), LinearLayout.LayoutParams(width, cell))
        column.addView(gap())
        val large = WidgetRenderer.large(themed, snapshot).apply(themed, column)
        fillList(themed, large)
        column.addView(large, LinearLayout.LayoutParams(width, width))
        setContentView(ScrollView(this).apply { addView(column) })
    }

    private fun pair(left: View, right: View?, cell: Int) = LinearLayout(this).apply {
        orientation = LinearLayout.HORIZONTAL
        addView(left, LinearLayout.LayoutParams(cell, cell).apply { marginEnd = dp(12) })
        right?.let { addView(it, LinearLayout.LayoutParams(cell, cell)) }
    }

    /** The list needs a launcher behind it; here its rows are drawn directly. */
    private fun fillList(context: Context, large: View) {
        val list = large.findViewById<ListView>(R.id.widget_list) ?: return
        val entries = WidgetRenderer.entries(UsageRepository(context).cached())
        large.findViewById<View>(R.id.widget_empty)?.visibility = if (entries.isEmpty()) View.VISIBLE else View.GONE
        list.adapter = object : BaseAdapter() {
            override fun getCount() = entries.size
            override fun getItem(position: Int) = entries[position]
            override fun getItemId(position: Int) = position.toLong()
            override fun getView(position: Int, convertView: View?, parent: ViewGroup): View =
                when (val entry = entries[position]) {
                    is WidgetRenderer.Entry.Line -> WidgetRenderer.rowViews(context, entry.line, compact = false)
                    is WidgetRenderer.Entry.Out -> WidgetRenderer.outViews(context, entry.out)
                }.apply(context, parent)
        }
    }

    private fun isDark() =
        resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK == Configuration.UI_MODE_NIGHT_YES

    private fun themed(dark: Boolean): Context {
        val config = Configuration(resources.configuration)
        config.uiMode = (config.uiMode and Configuration.UI_MODE_NIGHT_MASK.inv()) or
            if (dark) Configuration.UI_MODE_NIGHT_YES else Configuration.UI_MODE_NIGHT_NO
        return createConfigurationContext(config)
    }

    private fun gap() = View(this).apply { layoutParams = LinearLayout.LayoutParams(1, dp(12)) }

    private fun dp(value: Int) = (value * resources.displayMetrics.density).roundToInt()
}

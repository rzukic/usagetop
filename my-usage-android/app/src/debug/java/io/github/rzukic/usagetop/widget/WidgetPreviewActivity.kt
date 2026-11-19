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
 * Draws the widget at 2x2, 4x2 and 4x4, light beside dark, from the cached
 * reading, on a wallpaper-like backdrop. For screenshots while working on it:
 * `adb shell am start -n <package>/io.github.rzukic.usagetop.widget.WidgetPreviewActivity`.
 */
class WidgetPreviewActivity : Activity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val snapshot = UsageRepository(this).cached()

        val columns = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            background = GradientDrawable(
                GradientDrawable.Orientation.TL_BR,
                intArrayOf(0xFF2E66C9.toInt(), 0xFF8FB0E0.toInt(), 0xFF2A5DBF.toInt()),
            )
            setPadding(dp(8), dp(28), dp(8), dp(16))
        }
        listOf(false, true).forEach { dark ->
            val themed = themed(dark)
            val column = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
            val width = (resources.displayMetrics.widthPixels - dp(16)) / 2 - dp(8)
            val cell = width / 2 - dp(4)

            column.addView(WidgetRenderer.small(themed, snapshot).apply(themed, column), LinearLayout.LayoutParams(cell, cell))
            column.addView(gap())
            column.addView(WidgetRenderer.medium(themed, snapshot).apply(themed, column), LinearLayout.LayoutParams(width, cell))
            column.addView(gap())
            val large = WidgetRenderer.large(themed, snapshot).apply(themed, column)
            fillList(themed, large)
            column.addView(large, LinearLayout.LayoutParams(width, (width * 1.1f).roundToInt()))
            columns.addView(column, LinearLayout.LayoutParams(width, ViewGroup.LayoutParams.WRAP_CONTENT).apply { marginEnd = dp(8) })
        }
        setContentView(ScrollView(this).apply { addView(columns) })
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

    private fun themed(dark: Boolean): Context {
        val config = Configuration(resources.configuration)
        config.uiMode = (config.uiMode and Configuration.UI_MODE_NIGHT_MASK.inv()) or
            if (dark) Configuration.UI_MODE_NIGHT_YES else Configuration.UI_MODE_NIGHT_NO
        return createConfigurationContext(config)
    }

    private fun gap() = View(this).apply { layoutParams = LinearLayout.LayoutParams(1, dp(10)) }

    private fun dp(value: Int) = (value * resources.displayMetrics.density).roundToInt()
}

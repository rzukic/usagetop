package io.github.rzukic.usagetop.widget

import android.content.Context

/**
 * Which limits one placed usage widget shows, chosen in its settings. Kept as
 * what differs from the app: limits shown here although hidden in the app, and
 * limits left out here although shown in the app. Everything else follows the
 * app, so a limit that appears later shows up by itself, and a limit hidden in
 * the app later is hidden here too, unless this widget asked for it.
 */
object LimitPicks {

    /** What this widget changes: [show] wins over the app hiding it, [hide] leaves it out. */
    data class Overrides(val show: Set<String> = emptySet(), val hide: Set<String> = emptySet())

    /** The limits this widget leaves out: the app's hidden ones, with its own choices on top. */
    fun hiddenFor(appHidden: Set<String>, overrides: Overrides): Set<String> =
        (appHidden - overrides.show) + overrides.hide

    /**
     * What to keep from a settings screen where [shown] says, for every limit
     * offered, whether this widget shows it: only what differs from the app.
     */
    fun overridesFrom(appHidden: Set<String>, shown: Map<String, Boolean>): Overrides = Overrides(
        show = shown.filter { (key, on) -> on && key in appHidden }.keys,
        hide = shown.filter { (key, on) -> !on && key !in appHidden }.keys,
    )

    fun read(context: Context, widgetId: Int): Overrides {
        val prefs = prefs(context)
        return Overrides(
            show = prefs.getStringSet("show_$widgetId", null)?.toSet().orEmpty(),
            hide = prefs.getStringSet("hide_$widgetId", null)?.toSet().orEmpty(),
        )
    }

    fun write(context: Context, widgetId: Int, overrides: Overrides) {
        prefs(context).edit()
            .putStringSet("show_$widgetId", HashSet(overrides.show))
            .putStringSet("hide_$widgetId", HashSet(overrides.hide))
            .apply()
    }

    fun forget(context: Context, widgetId: Int) {
        prefs(context).edit().remove("show_$widgetId").remove("hide_$widgetId").apply()
    }

    private fun prefs(context: Context) = context.getSharedPreferences("widget_limits", Context.MODE_PRIVATE)
}

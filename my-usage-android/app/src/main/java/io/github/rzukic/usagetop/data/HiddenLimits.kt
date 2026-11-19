package io.github.rzukic.usagetop.data

import android.content.Context
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/**
 * Limits someone has chosen not to see: one window of one account, keyed the
 * way history files its series, "antigravity|Claude & GPT 7d". Hiding is about
 * what is shown, never the arithmetic: the combined figure counts a hidden
 * limit as before. A server keeps one list for every app; without one, this
 * device keeps its own (see [HiddenLimitStore]).
 */
object HiddenLimits {

    fun key(account: String, label: String): String = "$account|$label"

    fun key(usage: ProviderUsage, window: UsageWindow): String = key(usage.accountKey, window.label)

    fun isHidden(hidden: Set<String>, usage: ProviderUsage, window: UsageWindow): Boolean =
        key(usage, window) in hidden

    /** The account's windows still to be shown, in their own order. */
    fun visible(usage: ProviderUsage, hidden: Set<String>): List<UsageWindow> =
        usage.windows.filter { !isHidden(hidden, usage, it) }

    /** How many of the account's windows are hidden. */
    fun hiddenCount(usage: ProviderUsage, hidden: Set<String>): Int =
        usage.windows.count { isHidden(hidden, usage, it) }

    /**
     * Which list is in charge: the server's while a server is set and its last
     * reading carried one, otherwise this device's. Never a merge of the two.
     */
    fun inUse(usesServer: Boolean, server: Set<String>?, local: Set<String>): Set<String> =
        if (usesServer && server != null) server else local

    /** The account a key belongs to, and the window label: "claude-2|7d" → ("claude-2", "7d"). */
    fun split(key: String): Pair<String, String>? {
        val bar = key.indexOf('|')
        if (bar <= 0 || bar == key.length - 1) return null
        return key.substring(0, bar) to key.substring(bar + 1)
    }
}

/**
 * Where the hidden limits live. With a server whose last reading carried a list,
 * that list is the one shown and changed, for every app; otherwise (no server,
 * or a server too old to keep one) this device's own list in the app's prefs.
 */
class HiddenLimitStore(context: Context) {

    private val appContext = context.applicationContext
    private val prefs = appContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
    private val credentials by lazy { Credentials(appContext) }

    /** The list in charge. */
    fun all(): Set<String> = HiddenLimits.inUse(credentials.serverUrl() != null, server(), local())

    /** True while the server keeps the list, for every app. */
    fun serverKeeps(): Boolean = credentials.serverUrl() != null && server() != null

    /** What a reading from the server said: its list, or null from a server without one. */
    fun fromServer(list: List<String>?) {
        val edit = prefs.edit()
        if (list == null) edit.remove(KEY_SERVER).putBoolean(KEY_SERVER_KNOWN, false)
        else edit.putStringSet(KEY_SERVER, HashSet(list)).putBoolean(KEY_SERVER_KNOWN, true)
        edit.apply()
    }

    /**
     * Hides a limit or shows it again. The change shows at once ([shown] is
     * called when it does); with a server it is then sent, and the list the server
     * answers replaces it. On failure the server's last list stands again and the
     * reason is returned, for where server problems are shown.
     */
    suspend fun set(key: String, hidden: Boolean, shown: () -> Unit = {}): String? {
        if (!serverKeeps()) {
            writeLocal(change(local(), key, hidden))
            shown()
            return null
        }
        val before = server().orEmpty()
        fromServer(change(before, key, hidden).toList())
        shown()
        val url = credentials.serverUrl() ?: return null
        return withContext(Dispatchers.IO) {
            try {
                fromServer(ServerClient.setHidden(url, credentials.serverToken(), key, hidden))
                null
            } catch (problem: ServerException) {
                fromServer(before.toList())
                problem.message
            }
        }
    }

    private fun local(): Set<String> = prefs.getStringSet(KEY_LOCAL, null)?.toSet().orEmpty()

    private fun server(): Set<String>? =
        if (prefs.getBoolean(KEY_SERVER_KNOWN, false)) prefs.getStringSet(KEY_SERVER, null)?.toSet().orEmpty() else null

    // A fresh set: SharedPreferences must never be handed back the one it gave out.
    private fun writeLocal(keys: Set<String>) = prefs.edit().putStringSet(KEY_LOCAL, HashSet(keys)).apply()

    private fun change(keys: Set<String>, key: String, hidden: Boolean): Set<String> =
        if (hidden) keys + key else keys - key

    private companion object {
        const val PREFS = "hidden_limits"
        const val KEY_LOCAL = "keys"
        const val KEY_SERVER = "server_keys"
        const val KEY_SERVER_KNOWN = "server_keeps"
    }
}

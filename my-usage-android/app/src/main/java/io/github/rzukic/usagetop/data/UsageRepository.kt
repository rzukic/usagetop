package io.github.rzukic.usagetop.data

import android.content.Context
import android.content.SharedPreferences
import io.github.rzukic.usagetop.auth.TokenRefresher
import io.github.rzukic.usagetop.providers.ChatGptProvider
import io.github.rzukic.usagetop.providers.ClaudeProvider
import io.github.rzukic.usagetop.providers.KimiProvider
import io.github.rzukic.usagetop.providers.MistralProvider
import io.github.rzukic.usagetop.providers.AntigravityProvider
import io.github.rzukic.usagetop.providers.MuseProvider
import io.github.rzukic.usagetop.providers.UsageProvider
import io.github.rzukic.usagetop.providers.networkError
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.withContext
import java.time.Duration
import java.time.Instant

class UsageRepository(context: Context) {

    private val appContext = context.applicationContext
    private val credentials = Credentials(appContext)

    private val providers: List<UsageProvider> = listOf(
        ClaudeProvider(),
        ChatGptProvider(),
        MistralProvider(),
        KimiProvider(),
        MuseProvider(),
        AntigravityProvider(),
    )

    /**
     * Fetches every provider in parallel, then caches the result for the widget.
     * Every screen and the worker make their own repository, so the sharing has
     * to live above any one of them: see [SharedFetch].
     */
    suspend fun refresh(force: Boolean = false): Snapshot = inFlight.share {
        val url = credentials.serverUrl()
        val snapshot = if (url != null) fromServer(url, force) else fetch()
        prefs().edit().putLong(KEY_SYNCED_AT, System.currentTimeMillis()).apply()
        snapshot
    }

    /** True while the readings come from a server rather than from the providers. */
    fun usesServer(): Boolean = credentials.serverUrl() != null

    /**
     * Why the last refresh could not reach the server, or null. The reading on
     * screen is then the last one that did arrive, and the screen should say so.
     */
    fun serverProblem(): String? =
        prefs().getString(KEY_SERVER_PROBLEM, null).takeIf { usesServer() }

    /** A server call outside a refresh failed (hiding a limit): said where refresh problems are. */
    fun reportServerProblem(message: String?) {
        prefs().edit().putString(KEY_SERVER_PROBLEM, message).apply()
    }

    /**
     * The server's reading in place of our own. Nothing here touches a provider
     * or a token: while a server is set, it is the one holder of every session.
     * [force] is the refresh button; it has the server ask the providers now.
     */
    private suspend fun fromServer(url: String, force: Boolean): Snapshot = withContext(Dispatchers.IO) {
        val prefs = prefs()
        try {
            // History already synced with this server only needs topping up. The
            // overlap covers a reading the server stored just after it answered.
            val syncedTo = prefs.getLong(KEY_SERVER_SYNCED_TO, 0L)
            val since = if (syncedTo > 0L && prefs.getString(KEY_SERVER_SYNCED_URL, null) == url) {
                Instant.ofEpochMilli(syncedTo).minus(SYNC_OVERLAP)
            } else {
                null
            }

            val state = ServerClient.fetch(url, credentials.serverToken(), since, force)

            UsageHistory(appContext).merge(state, Instant.now())
            HiddenLimitStore(appContext).fromServer(state.hiddenLimits)
            prefs.edit()
                .putString(KEY_CACHE, SnapshotCodec.encode(state.snapshot))
                .putString(KEY_SERVER_SYNCED_URL, url)
                .putLong(KEY_SERVER_SYNCED_TO, state.serverTime.toEpochMilli())
                .remove(KEY_SERVER_PROBLEM)
                .apply()
            state.snapshot
        } catch (problem: ServerException) {
            prefs.edit().putString(KEY_SERVER_PROBLEM, problem.message).apply()
            cached() ?: Snapshot(emptyList(), Instant.now())
        }
    }

    private suspend fun fetch(): Snapshot = coroutineScope {
        prefs().edit().remove(KEY_SERVER_PROBLEM).apply()
        TokenRefresher.ensureFresh(credentials)

        val previous = cached()?.usages.orEmpty().associateBy { it.accountKey }
        val now = Instant.now()

        // Only the providers that have been added: the rest are not asked, and
        // do not show.
        val added = credentials.added()
        val usages = providers
            .filter { it.id in added }
            .map { provider -> async(Dispatchers.IO) { fetchOne(provider) } }
            .awaitAll()
            .map { usage -> keepLastGood(usage, previous[usage.accountKey]) }
            // Meta reports nothing at all while the five-hour window is idle or
            // spent, so a reading kept from before its reset has to be rolled
            // forward by hand.
            .map { usage -> MuseProvider.rollOver(usage, now) }

        val snapshot = Snapshot(usages, now)
        store(snapshot)
        UsageHistory(appContext).record(snapshot)
        snapshot
    }

    /**
     * A refused token is renewed and tried once more before the card is allowed
     * to say "Token rejected": most refusals are only an expiry nobody had on
     * file.
     */
    private suspend fun fetchOne(provider: UsageProvider): ProviderUsage {
        val usage = guarded(provider)
        if (!usage.rejected) return usage

        val renewed = runCatching { TokenRefresher.renewRejected(provider.id, credentials) }
            .getOrDefault(false)
        return if (renewed) guarded(provider) else usage
    }

    private suspend fun guarded(provider: UsageProvider): ProviderUsage = try {
        provider.fetch(credentials)
    } catch (t: Throwable) {
        networkError(provider.id, t)
    }

    /**
     * A rate limit or a dropped connection says nothing about the quota itself, so
     * the last reading that got through stands in for it instead of blanking the
     * card. A rejected token or a moved endpoint is a real problem and is shown.
     *
     * The reason travels with the numbers: a reading that has been standing in for
     * hours is a different thing from one that missed a single fetch, and the screen
     * has to be able to say which.
     */
    private fun keepLastGood(usage: ProviderUsage, previous: ProviderUsage?): ProviderUsage {
        if (usage.state != UsageState.ERROR || !usage.transient) return usage
        val last = previous?.takeIf { it.state == UsageState.OK && it.windows.isNotEmpty() }
            ?: return usage
        // Only the numbers are old; the account's name is today's.
        return last.copy(stale = true, message = usage.message, accountName = usage.accountName)
    }

    fun cached(): Snapshot? = SnapshotCodec.decode(prefs().getString(KEY_CACHE, null))

    /**
     * True when the cached reading is younger than [maxAge]. A screen opening asks
     * this before fetching: a reading from the last minute is already what the user
     * came to see, and refetching it would only make the numbers flicker as they
     * walk between screens.
     */
    fun isFresh(maxAge: Duration = AUTO_REFRESH_WINDOW): Boolean {
        val read = cached()?.fetchedAt ?: return false

        // The server's reading can be minutes old the moment it arrives, so what
        // counts is when we last asked, not when it was taken.
        val asked = Instant.ofEpochMilli(prefs().getLong(KEY_SYNCED_AT, 0L))
        return Duration.between(maxOf(read, asked), Instant.now()) < maxAge
    }

    /**
     * Calls [onChange] whenever a refresh lands, wherever it was started from, so a
     * screen that is already open shows the same reading as the widget instead of
     * the one it happened to open with. Keep the returned listener alive and hand
     * it back to [unwatch]; SharedPreferences only holds it weakly.
     */
    fun watch(onChange: () -> Unit): SharedPreferences.OnSharedPreferenceChangeListener {
        val listener = SharedPreferences.OnSharedPreferenceChangeListener { _, key ->
            if (key == KEY_CACHE || key == KEY_SERVER_PROBLEM) onChange()
        }
        prefs().registerOnSharedPreferenceChangeListener(listener)
        return listener
    }

    fun unwatch(listener: SharedPreferences.OnSharedPreferenceChangeListener) {
        prefs().unregisterOnSharedPreferenceChangeListener(listener)
    }

    private fun store(snapshot: Snapshot) {
        prefs().edit().putString(KEY_CACHE, SnapshotCodec.encode(snapshot)).apply()
    }

    private fun prefs() = appContext.getSharedPreferences("ai_usage_cache", Context.MODE_PRIVATE)

    private companion object {
        const val KEY_CACHE = "snapshot"
        const val KEY_SYNCED_AT = "synced_at"
        const val KEY_SERVER_PROBLEM = "server_problem"
        const val KEY_SERVER_SYNCED_URL = "server_synced_url"
        const val KEY_SERVER_SYNCED_TO = "server_synced_to"

        val SYNC_OVERLAP: Duration = Duration.ofMinutes(10)

        /** How old a reading may be before opening a screen refetches it. */
        val AUTO_REFRESH_WINDOW: Duration = Duration.ofSeconds(60)

        /** One per process, because the repositories themselves are many. */
        val inFlight = SharedFetch<Snapshot>()
    }
}

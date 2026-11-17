package io.github.rzukic.usagetop.auth

import android.content.Context
import io.github.rzukic.usagetop.data.ProviderId
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.withTimeoutOrNull

/**
 * Bridge between the activity that opens the browser and the service that owns
 * the loopback socket.
 *
 * Both live in the same process, so a plain singleton carries the handshake.
 * The pending flow is also written to disk, so a service that gets restarted
 * after process death can rebind the same port and keep the in-flight browser
 * redirect valid.
 */
object OAuthSession {

    data class Pending(
        val provider: ProviderId,
        val verifier: String,
        val state: String,
        val port: Int,
        val redirectUri: String,
        val authorizeUrl: String,
        val startedAt: Long,
    )

    sealed interface Result {
        data class Success(val provider: ProviderId) : Result
        data class Failure(val provider: ProviderId, val message: String) : Result
    }

    private var authorizeUrl: CompletableDeferred<String>? = null

    private val _results = MutableSharedFlow<Result>(replay = 1, extraBufferCapacity = 4)
    val results: SharedFlow<Result> = _results

    /** Called by the activity right before it starts the service. */
    fun prepare() {
        authorizeUrl = CompletableDeferred()
    }

    fun publishAuthorizeUrl(url: String) {
        authorizeUrl?.complete(url)
    }

    suspend fun awaitAuthorizeUrl(timeoutMillis: Long): String? {
        val deferred = authorizeUrl ?: CompletableDeferred<String>().also { authorizeUrl = it }
        return withTimeoutOrNull(timeoutMillis) { deferred.await() }
    }

    fun publishResult(result: Result) {
        _results.tryEmit(result)
    }

    fun savePending(context: Context, pending: Pending) {
        prefs(context).edit()
            .putString(KEY_PROVIDER, pending.provider.key)
            .putString(KEY_VERIFIER, pending.verifier)
            .putString(KEY_STATE, pending.state)
            .putInt(KEY_PORT, pending.port)
            .putString(KEY_REDIRECT, pending.redirectUri)
            .putString(KEY_AUTHORIZE, pending.authorizeUrl)
            .putLong(KEY_STARTED, pending.startedAt)
            .apply()
    }

    fun pending(context: Context): Pending? {
        val prefs = prefs(context)
        val provider = prefs.getString(KEY_PROVIDER, null)
            ?.let { key -> ProviderId.values().firstOrNull { it.key == key } }
            ?: return null
        return Pending(
            provider = provider,
            verifier = prefs.getString(KEY_VERIFIER, null) ?: return null,
            state = prefs.getString(KEY_STATE, null) ?: return null,
            port = prefs.getInt(KEY_PORT, 0),
            redirectUri = prefs.getString(KEY_REDIRECT, null) ?: return null,
            authorizeUrl = prefs.getString(KEY_AUTHORIZE, null) ?: return null,
            startedAt = prefs.getLong(KEY_STARTED, 0L),
        )
    }

    fun clearPending(context: Context) {
        prefs(context).edit().clear().apply()
    }

    private fun prefs(context: Context) =
        context.applicationContext.getSharedPreferences("oauth_pending", Context.MODE_PRIVATE)

    private const val KEY_PROVIDER = "provider"
    private const val KEY_VERIFIER = "verifier"
    private const val KEY_STATE = "state"
    private const val KEY_PORT = "port"
    private const val KEY_REDIRECT = "redirect_uri"
    private const val KEY_AUTHORIZE = "authorize_url"
    private const val KEY_STARTED = "started_at"
}

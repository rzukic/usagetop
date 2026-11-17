package io.github.rzukic.usagetop.auth

import io.github.rzukic.usagetop.data.Credentials
import io.github.rzukic.usagetop.data.ProviderId
import java.util.concurrent.ConcurrentHashMap

/**
 * Keeps OAuth access tokens usable. Access tokens live for hours, so without
 * this the widget would go dark shortly after signing in.
 */
object TokenRefresher {

    private const val SKEW_MS = 5 * 60 * 1000L

    /**
     * How long to wait before renewing a rejected token a second time. Without a
     * floor, a provider that keeps answering 401 for some other reason would have
     * a refresh token spent on it at every poll.
     */
    private const val REJECTED_COOLDOWN_MS = 30 * 60 * 1000L

    private val lastForced = ConcurrentHashMap<ProviderId, Long>()

    /** Renews every token that is about to expire, before anything is fetched. */
    suspend fun ensureFresh(credentials: Credentials) {
        OAuthConfig.values().forEach { config ->
            val id = config.providerId
            if (credentials.refreshToken(id) == null) return@forEach

            val expiresAt = credentials.expiresAt(id)
            val stale = credentials.token(id) == null ||
                (expiresAt > 0L && expiresAt - System.currentTimeMillis() < SKEW_MS)

            if (stale) runCatching { OAuthFlow.refresh(config, credentials) }
        }
    }

    /**
     * Renews a token the provider has just refused. The expiry on file is not
     * always known, and a provider can revoke a token early, so "not expired yet"
     * does not mean "still accepted". Returns true when there is a new token
     * worth trying.
     */
    suspend fun renewRejected(id: ProviderId, credentials: Credentials): Boolean {
        val config = OAuthConfig.forProvider(id) ?: return false
        if (credentials.refreshToken(id) == null) return false

        val now = System.currentTimeMillis()
        val last = lastForced[id]
        if (last != null && now - last < REJECTED_COOLDOWN_MS) return false
        lastForced[id] = now

        return runCatching { OAuthFlow.refresh(config, credentials) }.getOrDefault(false)
    }
}

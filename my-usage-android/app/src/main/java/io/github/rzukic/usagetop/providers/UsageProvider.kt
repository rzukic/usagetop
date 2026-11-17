package io.github.rzukic.usagetop.providers

import io.github.rzukic.usagetop.data.Credentials
import io.github.rzukic.usagetop.data.ProviderId
import io.github.rzukic.usagetop.data.ProviderUsage
import io.github.rzukic.usagetop.data.UsageState
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import java.time.Instant
import java.time.OffsetDateTime

interface UsageProvider {
    val id: ProviderId
    suspend fun fetch(credentials: Credentials): ProviderUsage
}

internal fun parseInstant(raw: String?): Instant? {
    if (raw.isNullOrBlank()) return null
    return try {
        OffsetDateTime.parse(raw).toInstant()
    } catch (t: Throwable) {
        try {
            Instant.parse(raw)
        } catch (t2: Throwable) {
            null
        }
    }
}

internal fun networkMessage(t: Throwable): String = when (t) {
    is UnknownHostException -> "No connection"
    is SocketTimeoutException -> "Timed out"
    else -> t.message?.take(60) ?: "Network error"
}

/** A dropped connection is worth retrying, so the last good reading may stand in. */
internal fun networkError(id: ProviderId, t: Throwable): ProviderUsage =
    ProviderUsage(id, UsageState.ERROR, message = networkMessage(t), transient = true)

internal fun httpError(id: ProviderId, code: Int): ProviderUsage {
    val message = when (code) {
        401, 403 -> "Token rejected"
        404 -> "Endpoint moved"
        429 -> "Rate limited"
        else -> "HTTP $code"
    }
    // A rate limit or a server fault is temporary; a rejected token is not.
    return ProviderUsage(
        id,
        UsageState.ERROR,
        message = message,
        transient = code == 429 || code >= 500,
        // 403 is left out on purpose: it is what an edge returns when it dislikes
        // the caller, and no new token changes that.
        rejected = code == 401,
    )
}

package io.github.rzukic.usagetop.auth

import android.util.Base64
import java.security.MessageDigest
import java.security.SecureRandom

/** PKCE helpers for the authorization-code flow. */
object Pkce {

    private val random = SecureRandom()

    /** 43-char base64url secret, kept until the token exchange. */
    fun verifier(): String = randomToken()

    /** S256 challenge: base64url(SHA-256(verifier)). */
    fun challenge(verifier: String): String {
        val digest = MessageDigest.getInstance("SHA-256")
            .digest(verifier.toByteArray(Charsets.US_ASCII))
        return Base64.encodeToString(digest, FLAGS)
    }

    /** CSRF token echoed back on the callback and compared there. */
    fun state(): String = randomToken()

    private fun randomToken(): String {
        val bytes = ByteArray(32)
        random.nextBytes(bytes)
        return Base64.encodeToString(bytes, FLAGS)
    }

    private const val FLAGS = Base64.NO_WRAP or Base64.NO_PADDING or Base64.URL_SAFE
}

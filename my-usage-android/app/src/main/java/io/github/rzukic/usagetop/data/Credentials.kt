package io.github.rzukic.usagetop.data

import android.content.Context
import android.content.SharedPreferences
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey

/**
 * Token storage. Uses EncryptedSharedPreferences when the keystore is usable and
 * silently falls back to a private plain prefs file otherwise, so the app never
 * hard-fails on devices with a broken keystore.
 */
class Credentials(context: Context) {

    private val prefs: SharedPreferences = open(context.applicationContext)

    fun token(id: ProviderId): String? =
        prefs.getString(keyToken(id), null)?.trim()?.takeIf { it.isNotEmpty() }

    fun setToken(id: ProviderId, value: String?) {
        prefs.edit().apply {
            val trimmed = value?.trim()
            if (trimmed.isNullOrEmpty()) remove(keyToken(id)) else putString(keyToken(id), trimmed)
        }.apply()
    }

    fun refreshToken(id: ProviderId): String? =
        prefs.getString(keyRefresh(id), null)?.takeIf { it.isNotEmpty() }

    fun setRefreshToken(id: ProviderId, value: String?) {
        prefs.edit().apply {
            if (value.isNullOrEmpty()) remove(keyRefresh(id)) else putString(keyRefresh(id), value)
        }.apply()
    }

    /** Epoch millis at which the access token expires; 0 when unknown. */
    fun expiresAt(id: ProviderId): Long = prefs.getLong(keyExpires(id), 0L)

    fun setExpiresAt(id: ProviderId, value: Long) {
        prefs.edit().putLong(keyExpires(id), value).apply()
    }

    /** ChatGPT account id, needed as a header on the usage endpoint. */
    fun accountId(id: ProviderId): String? =
        prefs.getString(keyAccount(id), null)?.takeIf { it.isNotEmpty() }

    fun setAccountId(id: ProviderId, value: String?) {
        prefs.edit().apply {
            if (value.isNullOrEmpty()) remove(keyAccount(id)) else putString(keyAccount(id), value)
        }.apply()
    }

    /**
     * Stores a whole session in one write. Setting the fields one at a time would
     * leave a moment where the new access token sits beside the old refresh token,
     * and a process killed inside it loses a refresh token that can never be asked
     * for again.
     */
    fun setSession(id: ProviderId, access: String, refresh: String?, expiresAt: Long, accountId: String?) {
        prefs.edit().apply {
            putString(keyToken(id), access.trim())
            if (refresh.isNullOrEmpty()) remove(keyRefresh(id)) else putString(keyRefresh(id), refresh)
            putLong(keyExpires(id), expiresAt)
            if (!accountId.isNullOrEmpty()) putString(keyAccount(id), accountId)
        }.apply()
    }

    /**
     * Stores a token typed or pasted in by hand. Whatever session was there
     * before is over: leaving its refresh token behind would have the next
     * renewal quietly write the old account's token back over the new one. An
     * empty value clears the provider, which is how the field signs you out.
     */
    fun setManualToken(id: ProviderId, value: String?) {
        val trimmed = value?.trim()
        prefs.edit().apply {
            if (trimmed.isNullOrEmpty()) {
                remove(keyToken(id))
                remove(keyAccount(id))
            } else {
                putString(keyToken(id), trimmed)
            }
            remove(keyRefresh(id))
            remove(keyExpires(id))
        }.apply()
    }

    /** True when the provider was set up through the browser flow. */
    fun isSignedIn(id: ProviderId): Boolean = refreshToken(id) != null

    fun signOut(id: ProviderId) {
        prefs.edit().apply {
            remove(keyToken(id))
            remove(keyRefresh(id))
            remove(keyExpires(id))
            remove(keyAccount(id))
        }.apply()
    }

    /**
     * The address of a usagetop server. While one is set the app reads from it
     * and asks no provider anything itself.
     */
    fun serverUrl(): String? = prefs.getString(KEY_SERVER_URL, null)?.takeIf { it.isNotEmpty() }

    fun serverToken(): String? = prefs.getString(KEY_SERVER_TOKEN, null)?.takeIf { it.isNotEmpty() }

    /** Both in one write, so a half-saved pair is never used. A null address turns the server off. */
    fun setServer(url: String?, token: String?) {
        prefs.edit().apply {
            if (url.isNullOrBlank()) {
                remove(KEY_SERVER_URL)
                remove(KEY_SERVER_TOKEN)
            } else {
                putString(KEY_SERVER_URL, url.trim())
                if (token.isNullOrBlank()) remove(KEY_SERVER_TOKEN) else putString(KEY_SERVER_TOKEN, token.trim())
            }
        }.apply()
    }

    fun kimiChina(): Boolean = prefs.getBoolean(KEY_KIMI_CN, false)

    fun setKimiChina(value: Boolean) {
        prefs.edit().putBoolean(KEY_KIMI_CN, value).apply()
    }

    fun mistralBudget(): Double? = prefs.getString(KEY_MISTRAL_BUDGET, null)?.toDoubleOrNull()

    fun setMistralBudget(value: Double?) {
        prefs.edit().apply {
            if (value == null || value <= 0.0) remove(KEY_MISTRAL_BUDGET) else putString(KEY_MISTRAL_BUDGET, value.toString())
        }.apply()
    }

    fun mistralSpent(): Double? = prefs.getString(KEY_MISTRAL_SPENT, null)?.toDoubleOrNull()

    fun setMistralSpent(value: Double?) {
        prefs.edit().apply {
            if (value == null || value < 0.0) remove(KEY_MISTRAL_SPENT) else putString(KEY_MISTRAL_SPENT, value.toString())
        }.apply()
    }

    private fun keyToken(id: ProviderId) = "token_${id.key}"

    private fun keyRefresh(id: ProviderId) = "refresh_${id.key}"

    private fun keyExpires(id: ProviderId) = "expires_${id.key}"

    private fun keyAccount(id: ProviderId) = "account_${id.key}"

    /**
     * The keystore fails now and then for no lasting reason -- it is busy, or the
     * device has only just unlocked -- so it gets a second try before the app
     * settles for the plain file. The plain file is still private to the app; it
     * is only not encrypted a second time.
     */
    private fun open(context: Context): SharedPreferences =
        runCatching { encrypted(context) }
            .recoverCatching { encrypted(context) }
            .getOrElse { context.getSharedPreferences("ai_usage_fallback", Context.MODE_PRIVATE) }

    private fun encrypted(context: Context): SharedPreferences {
        val masterKey = MasterKey.Builder(context)
            .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
            .build()
        return EncryptedSharedPreferences.create(
            context,
            "ai_usage_secure",
            masterKey,
            EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
            EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM,
        )
    }

    private companion object {
        const val KEY_KIMI_CN = "kimi_china"
        const val KEY_MISTRAL_BUDGET = "mistral_budget"
        const val KEY_MISTRAL_SPENT = "mistral_spent"
        const val KEY_SERVER_URL = "server_url"
        const val KEY_SERVER_TOKEN = "server_token"
    }
}

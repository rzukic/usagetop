package io.github.rzukic.usagetop.data

import io.github.rzukic.usagetop.net.Http
import io.github.rzukic.usagetop.net.HttpResponse
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import org.json.JSONArray
import org.json.JSONObject
import java.io.IOException
import java.net.URLEncoder
import java.time.Instant

/** Everything one call to the server returns: the reading and the history behind it. */
data class ServerState(
    val snapshot: Snapshot,
    val serverTime: Instant,
    val combined: List<UsagePoint>,
    val series: Map<String, List<UsagePoint>>,
) {
    companion object {
        /**
         * Reads a `/api/state` answer. The snapshot inside it is written the way
         * this app's own cache is, so [SnapshotCodec] reads it as it stands. Null
         * when the server has not completed a first poll yet.
         */
        fun parse(json: String): ServerState? {
            val root = JSONObject(json)
            val snapshot = root.optJSONObject("snapshot")
                ?.let { SnapshotCodec.decode(it.toString()) }
                ?: return null

            val series = mutableMapOf<String, List<UsagePoint>>()
            root.optJSONObject("series")?.let { all ->
                all.keys().forEach { key -> series[key] = points(all.optJSONArray(key)) }
            }

            return ServerState(
                snapshot = snapshot,
                serverTime = Instant.ofEpochMilli(root.optLong("serverTime", System.currentTimeMillis())),
                combined = points(root.optJSONArray("combined")),
                series = series,
            )
        }

        private fun points(array: JSONArray?): List<UsagePoint> {
            if (array == null) return emptyList()
            return (0 until array.length()).mapNotNull { index ->
                val pair = array.optJSONArray(index)?.takeIf { it.length() >= 2 } ?: return@mapNotNull null
                val percent = pair.optDouble(1)
                if (percent.isNaN()) return@mapNotNull null
                UsagePoint(Instant.ofEpochMilli(pair.optLong(0)), percent.toFloat())
            }
        }
    }
}

/** A failure talking to the server, worded for the status line. */
class ServerException(message: String) : Exception(message)

/**
 * An account the server follows. It says whether something is stored and
 * whether that renews itself; never the token, which the server does not hand
 * back to anyone.
 */
data class ServerAccount(
    val key: String,
    val provider: String,
    val name: String?,
    val displayName: String,
    val removable: Boolean,
    val configured: Boolean,
    val renews: Boolean,
) {
    companion object {
        fun parse(node: JSONObject) = ServerAccount(
            key = node.optString("key"),
            provider = node.optString("provider"),
            name = node.optString("name").takeIf { it.isNotEmpty() },
            displayName = node.optString("displayName").ifEmpty { node.optString("key") },
            removable = node.optBoolean("removable", false),
            configured = node.optBoolean("configured", false),
            renews = node.optBoolean("renews", false),
        )
    }
}

/**
 * What the user has to do next to finish a sign-in the server started.
 * "code": approve, paste the code the page shows. "redirect": approve, paste
 * the address the browser lands on. "device": type the code into the page.
 */
data class ServerSignInStep(
    val kind: String,
    val url: String,
    val userCode: String?,
    val instructions: String,
) {
    companion object {
        fun parse(node: JSONObject) = ServerSignInStep(
            kind = node.optString("kind").ifEmpty { "code" },
            url = node.optString("url"),
            userCode = node.optString("userCode").takeIf { it.isNotEmpty() },
            instructions = node.optString("instructions"),
        )
    }
}

/**
 * The app's side of the server's API. With a server set, this replaces every
 * provider call the app would otherwise make.
 */
object ServerClient {

    /** What a first call asks for: everything the charts can show. */
    private const val FULL_QUERY = "days=35&points=1500"

    /**
     * "host:8377" or "http://host:8377/" both become "http://host:8377". Null
     * when the text is not an address at all.
     */
    fun normalizeUrl(text: String?): String? {
        var trimmed = text?.trim().orEmpty()
        if (trimmed.isEmpty()) return null
        if (!trimmed.contains("://")) trimmed = "http://$trimmed"
        trimmed = trimmed.trimEnd('/')
        return trimmed.takeIf { it.toHttpUrlOrNull() != null }
    }

    /**
     * The reading and its history. [since] asks only for the readings after that
     * moment; [force] has the server ask every provider first, which it will not
     * do more than twice a minute. Blocking: call it off the main thread.
     */
    fun fetch(url: String, token: String?, since: Instant? = null, force: Boolean = false): ServerState {
        val headers = if (token.isNullOrEmpty()) emptyMap() else mapOf("Authorization" to "Bearer $token")

        if (force) {
            // Best effort: if the forced poll fails, the reading the server
            // already has is still worth showing.
            try {
                Http.postJson("$url/api/refresh", headers, "{}")
            } catch (e: IOException) {
                // Reported by the read below, if it fails too.
            }
        }

        val query = since?.let { "since=${it.toEpochMilli()}&points=1500" } ?: FULL_QUERY
        val response = try {
            Http.get("$url/api/state?$query", headers)
        } catch (e: IOException) {
            throw ServerException("Server unreachable")
        } catch (e: IllegalArgumentException) {
            throw ServerException("Server address is not valid")
        }

        when {
            response.code == 401 -> throw ServerException("Server refused the token")
            response.code !in 200..299 -> throw ServerException("Server answered ${response.code}")
        }

        return try {
            ServerState.parse(response.body)
        } catch (e: Exception) {
            throw ServerException("Server sent something unreadable")
        } ?: throw ServerException("Server has no reading yet")
    }

    /**
     * The accounts the server follows, in its own order. Each says whether it
     * has something stored and whether that renews itself; never the token.
     */
    fun accounts(url: String, token: String?): List<ServerAccount> {
        val response = send { Http.get("$url/api/credentials", headers(token)) }
        return parsed { parseAccounts(response.body) }
    }

    internal fun parseAccounts(json: String): List<ServerAccount> {
        val array = JSONArray(json)
        return (0 until array.length()).mapNotNull { index ->
            array.optJSONObject(index)?.let { ServerAccount.parse(it) }
        }
    }

    /** Another account of a provider: a second Claude subscription, a work ChatGPT. */
    fun addAccount(url: String, token: String?, provider: String, name: String?): ServerAccount {
        val body = JSONObject()
            .put("provider", provider)
            .put("name", name?.trim()?.takeIf { it.isNotEmpty() } ?: JSONObject.NULL)
        val response = send { Http.postJson("$url/api/accounts", headers(token), body.toString()) }
        return parsed { ServerAccount.parse(JSONObject(response.body)) }
    }

    fun renameAccount(url: String, token: String?, key: String, name: String?) {
        val body = JSONObject().put("name", name?.trim()?.takeIf { it.isNotEmpty() } ?: JSONObject.NULL)
        send { Http.patchJson("$url/api/accounts/${escape(key)}", headers(token), body.toString()) }
    }

    /** Only an added account can go; the first of each provider is signed out instead. */
    fun removeAccount(url: String, token: String?, key: String) {
        send { Http.delete("$url/api/accounts/${escape(key)}", headers(token)) }
    }

    fun signOut(url: String, token: String?, key: String) {
        send { Http.delete("$url/api/credentials/${escape(key)}", headers(token)) }
    }

    /**
     * A key for an account the server follows. The settings that travel with a
     * key go along, so a Kimi region or a Mistral budget is not lost by moving
     * the account to the server.
     */
    fun setKey(
        url: String,
        token: String?,
        key: String,
        value: String,
        kimiChina: Boolean? = null,
        mistralBudget: Double? = null,
        mistralSpent: Double? = null,
    ) {
        val body = JSONObject().put("token", value)
        if (kimiChina != null) body.put("kimiChina", kimiChina)
        if (mistralBudget != null) body.put("mistralBudget", mistralBudget)
        if (mistralSpent != null) body.put("mistralSpent", mistralSpent)
        send { Http.putJson("$url/api/credentials/${escape(key)}", headers(token), body.toString()) }
    }

    fun startSignIn(url: String, token: String?, key: String): ServerSignInStep {
        val response = send { Http.postJson("$url/api/signin/${escape(key)}/start", headers(token), "{}") }
        return parsed { ServerSignInStep.parse(JSONObject(response.body)) }
    }

    /** Finishes a "code" or "redirect" sign-in with what the user pasted. */
    fun completeSignIn(url: String, token: String?, key: String, input: String) {
        val body = JSONObject().put("input", input)
        send { Http.postJson("$url/api/signin/${escape(key)}/complete", headers(token), body.toString()) }
    }

    /** "pending", "done", or "failed: reason" for the last sign-in of an account. */
    fun signInStatus(url: String, token: String?, key: String): String {
        val response = send { Http.get("$url/api/signin/${escape(key)}/status", headers(token)) }
        return parsed { JSONObject(response.body).optString("status").ifEmpty { "none" } }
    }

    private fun headers(token: String?): Map<String, String> =
        if (token.isNullOrEmpty()) emptyMap() else mapOf("Authorization" to "Bearer $token")

    private fun escape(key: String): String =
        URLEncoder.encode(key, "UTF-8").replace("+", "%20")

    private fun <T> parsed(parse: () -> T): T = try {
        parse()
    } catch (e: Exception) {
        throw ServerException("Server sent something unreadable")
    }

    private fun send(call: () -> HttpResponse): HttpResponse {
        val response = try {
            call()
        } catch (e: IOException) {
            throw ServerException("Server unreachable")
        } catch (e: IllegalArgumentException) {
            throw ServerException("Server address is not valid")
        }

        if (response.code in 200..299) return response
        if (response.code == 401) throw ServerException("Server refused the token")

        // The server words its refusals ("No account 'x'", "token is required");
        // that is more use than the status code.
        throw ServerException(errorOf(response.body) ?: "Server answered ${response.code}")
    }

    private fun errorOf(body: String): String? = try {
        JSONObject(body).optString("error").takeIf { it.isNotEmpty() }
    } catch (e: Exception) {
        null
    }
}

package io.github.rzukic.usagetop.auth

import android.util.Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import java.io.BufferedReader
import java.io.InputStreamReader
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.URLDecoder

/**
 * HTTP listener on the loopback interface that catches the OAuth redirect.
 *
 * The browser is sent to `http://localhost:<port><path>?code=...&state=...`.
 * Browsers also fire unrelated requests at the same origin (favicon probes,
 * speculative connections), so the listener keeps accepting until a request
 * actually lands on [callbackPath].
 *
 * Both 127.0.0.1 and ::1 are bound when the platform allows it, because
 * `localhost` can resolve to either.
 */
class LoopbackServer(port: Int, private val callbackPath: String) {

    private val sockets: List<ServerSocket>

    val port: Int

    init {
        val v4 = ServerSocket()
        v4.reuseAddress = true
        v4.bind(InetSocketAddress(InetAddress.getByName("127.0.0.1"), port), BACKLOG)
        val bound = v4.localPort
        val boundSockets = mutableListOf(v4)

        runCatching {
            val v6 = ServerSocket()
            v6.reuseAddress = true
            v6.bind(InetSocketAddress(InetAddress.getByName("::1"), bound), BACKLOG)
            boundSockets += v6
        }.onFailure { Log.w(TAG, "IPv6 loopback unavailable: ${it.message}") }

        sockets = boundSockets
        this.port = bound
        Log.i(TAG, "listening on $bound$callbackPath (${boundSockets.size} socket(s))")
    }

    fun redirectUri(): String = "http://localhost:$port$callbackPath"

    /**
     * Suspends until a request hits [callbackPath], then returns its query
     * parameters. Returns null if nothing arrived within [timeoutMillis].
     */
    suspend fun awaitCallback(timeoutMillis: Long): Map<String, String>? =
        withContext(Dispatchers.IO) {
            val results = Channel<Map<String, String>>(Channel.UNLIMITED)
            val acceptors = sockets.map { socket ->
                launch {
                    while (isActive) {
                        val connection = try {
                            socket.accept()
                        } catch (t: Throwable) {
                            break
                        }
                        runCatching { serve(connection, results) }
                            .onFailure { Log.w(TAG, "request failed: ${it.message}") }
                    }
                }
            }

            try {
                withTimeoutOrNull(timeoutMillis) { results.receive() }
            } finally {
                // Closing unblocks the accept() calls the acceptors are parked in.
                close()
                acceptors.forEach(Job::cancel)
                results.close()
            }
        }

    fun close() {
        sockets.forEach { runCatching { it.close() } }
    }

    private fun serve(connection: Socket, results: Channel<Map<String, String>>) {
        connection.use { socket ->
            socket.soTimeout = READ_TIMEOUT_MS
            val reader = BufferedReader(InputStreamReader(socket.getInputStream(), Charsets.UTF_8))
            val requestLine = reader.readLine().orEmpty()
            while (true) {
                val line = reader.readLine() ?: break
                if (line.isEmpty()) break
            }

            val target = requestLine.split(' ').getOrNull(1).orEmpty()
            val path = target.substringBefore('?')
            Log.i(TAG, "request: ${requestLine.take(120)}")

            if (path != callbackPath) {
                respond(socket, 404, "Not Found", NOT_FOUND_HTML)
                return
            }

            val params = parseQuery(target)
            respond(socket, 200, "OK", if (params["code"] != null) SUCCESS_HTML else FAILURE_HTML)
            results.trySend(params)
        }
    }

    private fun respond(socket: Socket, status: Int, reason: String, body: String) {
        val bytes = body.toByteArray(Charsets.UTF_8)
        val header = buildString {
            append("HTTP/1.1 $status $reason\r\n")
            append("Content-Type: text/html; charset=utf-8\r\n")
            append("Content-Length: ${bytes.size}\r\n")
            append("Cache-Control: no-store\r\n")
            append("Connection: close\r\n\r\n")
        }
        socket.getOutputStream().apply {
            write(header.toByteArray(Charsets.US_ASCII))
            write(bytes)
            flush()
        }
    }

    private fun parseQuery(target: String): Map<String, String> {
        val query = target.substringAfter('?', "")
        if (query.isEmpty()) return emptyMap()

        return query.split('&').mapNotNull { pair ->
            val name = pair.substringBefore('=', "")
            if (name.isEmpty()) return@mapNotNull null
            name to URLDecoder.decode(pair.substringAfter('=', ""), "UTF-8")
        }.toMap()
    }

    private companion object {
        const val TAG = "AiUsageOAuth"
        const val BACKLOG = 8
        const val READ_TIMEOUT_MS = 10_000

        const val SUCCESS_HTML = """
            <!doctype html><meta name="viewport" content="width=device-width,initial-scale=1">
            <body style="font-family:system-ui;background:#121216;color:#F2F2F7;
                         text-align:center;padding:64px 24px">
            <h2 style="font-weight:600">Signed in</h2>
            <p style="color:#9A9AA8">You can close this tab and return to usagetop.</p>
            <p style="margin-top:32px"><a href="aiusage://oauth/done"
               style="display:inline-block;padding:14px 28px;border-radius:999px;
                      background:#F2F2F7;color:#121216;text-decoration:none;font-weight:600">
               Return to usagetop</a></p>
            </body>
        """

        const val FAILURE_HTML = """
            <!doctype html><meta name="viewport" content="width=device-width,initial-scale=1">
            <body style="font-family:system-ui;background:#121216;color:#F2F2F7;
                         text-align:center;padding:64px 24px">
            <h2 style="font-weight:600">Sign-in failed</h2>
            <p style="color:#9A9AA8">No authorization code came back. Return to usagetop and try again.</p>
            </body>
        """

        const val NOT_FOUND_HTML = """
            <!doctype html><meta name="viewport" content="width=device-width,initial-scale=1">
            <body style="font-family:system-ui;background:#121216;color:#F2F2F7;
                         text-align:center;padding:64px 24px">
            <h2 style="font-weight:600">Nothing here</h2>
            </body>
        """
    }
}

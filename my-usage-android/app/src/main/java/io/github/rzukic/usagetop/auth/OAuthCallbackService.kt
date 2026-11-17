package io.github.rzukic.usagetop.auth

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.util.Log
import io.github.rzukic.usagetop.R
import io.github.rzukic.usagetop.data.Credentials
import io.github.rzukic.usagetop.data.ProviderId
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/**
 * Owns the loopback socket for a browser sign-in.
 *
 * The listener cannot live in the activity: this device reaps a backgrounded
 * process within seconds, and the browser round trip -- which for Claude goes
 * through an emailed magic link and a second browser -- takes far longer than
 * that. A foreground service keeps the process alive until the redirect lands.
 */
class OAuthCallbackService : Service() {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var server: LoopbackServer? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val provider = intent?.getStringExtra(EXTRA_PROVIDER)
            ?.let { key -> ProviderId.values().firstOrNull { it.key == key } }
        if (provider == null) {
            stopSelf()
            return START_NOT_STICKY
        }

        // Muse approves a code in the browser instead of redirecting to a
        // socket, but the wait is just as long, so it is owned here too.
        if (provider == ProviderId.MUSE) {
            startForegroundCompat(provider)
            awaitDeviceApproval(provider)
            return START_REDELIVER_INTENT
        }

        val config = OAuthConfig.forProvider(provider)
        if (config == null) {
            stopSelf()
            return START_NOT_STICKY
        }

        startForegroundCompat(provider)

        val resumed = OAuthSession.pending(this)
            ?.takeIf { it.provider == provider && System.currentTimeMillis() - it.startedAt < PENDING_TTL_MS }

        if (resumed != null) {
            Log.i(TAG, "resuming ${provider.key} flow on port ${resumed.port}")
            awaitCallback(config, resumed)
            return START_REDELIVER_INTENT
        }

        val verifier = Pkce.verifier()
        val state = Pkce.state()
        val loopback = try {
            LoopbackServer(config.fixedPort ?: 0, config.callbackPath)
        } catch (t: Throwable) {
            Log.e(TAG, "cannot bind loopback port", t)
            fail(provider, "Could not open a local callback port: ${t.message}")
            return START_NOT_STICKY
        }
        server = loopback

        val flow = OAuthSession.Pending(
            provider = provider,
            verifier = verifier,
            state = state,
            port = loopback.port,
            redirectUri = loopback.redirectUri(),
            authorizeUrl = OAuthFlow.authorizeUrl(
                config,
                loopback.redirectUri(),
                Pkce.challenge(verifier),
                state,
            ),
            startedAt = System.currentTimeMillis(),
        )
        OAuthSession.savePending(this, flow)
        OAuthSession.publishAuthorizeUrl(flow.authorizeUrl)
        Log.i(TAG, "started ${provider.key} flow, redirect ${flow.redirectUri}")

        awaitCallback(config, flow)
        return START_REDELIVER_INTENT
    }

    private fun awaitCallback(config: OAuthConfig, flow: OAuthSession.Pending) {
        scope.launch {
            val loopback = server ?: try {
                LoopbackServer(flow.port, config.callbackPath).also { server = it }
            } catch (t: Throwable) {
                Log.e(TAG, "cannot reopen port ${flow.port}", t)
                fail(config.providerId, "Could not reopen the callback port: ${t.message}")
                return@launch
            }

            val params = loopback.awaitCallback(CALLBACK_TIMEOUT_MS)
            if (params == null) {
                fail(config.providerId, "Timed out waiting for the browser")
                return@launch
            }
            if (params["state"] != flow.state) {
                fail(config.providerId, "State mismatch")
                return@launch
            }
            val code = params["code"]
            if (code == null) {
                fail(
                    config.providerId,
                    params["error_description"] ?: params["error"] ?: "No code returned",
                )
                return@launch
            }

            try {
                OAuthFlow.complete(
                    config,
                    Credentials(this@OAuthCallbackService),
                    code,
                    flow.verifier,
                    flow.redirectUri,
                    flow.state,
                )
                OAuthSession.clearPending(this@OAuthCallbackService)
                OAuthSession.publishResult(OAuthSession.Result.Success(config.providerId))
                Log.i(TAG, "${config.providerId.key} signed in")
            } catch (t: Throwable) {
                Log.e(TAG, "token exchange failed", t)
                fail(config.providerId, t.message ?: "Token exchange failed")
                return@launch
            }
            stopSelf()
        }
    }

    /** Starts a device-code sign-in, then polls until the browser approves it. */
    private fun awaitDeviceApproval(provider: ProviderId) {
        scope.launch {
            val started = try {
                DeviceLogin.start()
            } catch (t: Throwable) {
                Log.e(TAG, "cannot start device sign-in", t)
                fail(provider, t.message ?: "Sign-in could not be started")
                return@launch
            }

            OAuthSession.publishAuthorizeUrl(started.verificationUrl)

            try {
                val token = DeviceLogin.awaitToken(started)
                Credentials(this@OAuthCallbackService).setToken(provider, token)
                OAuthSession.publishResult(OAuthSession.Result.Success(provider))
                Log.i(TAG, "${provider.key} signed in")
            } catch (t: Throwable) {
                Log.e(TAG, "device sign-in failed", t)
                fail(provider, t.message ?: "Sign-in failed")
                return@launch
            }
            stopSelf()
        }
    }

    private fun fail(provider: ProviderId, message: String) {
        Log.w(TAG, "${provider.key} sign-in failed: $message")
        OAuthSession.clearPending(this)
        OAuthSession.publishResult(OAuthSession.Result.Failure(provider, message))
        stopSelf()
    }

    override fun onDestroy() {
        server?.close()
        scope.cancel()
        super.onDestroy()
    }

    private fun startForegroundCompat(provider: ProviderId) {
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(
                CHANNEL_ID,
                getString(R.string.oauth_channel),
                NotificationManager.IMPORTANCE_LOW,
            )
        )

        val notification = Notification.Builder(this, CHANNEL_ID)
            .setContentTitle(getString(R.string.oauth_waiting_title))
            .setContentText(getString(R.string.oauth_waiting_text, provider.displayName))
            .setSmallIcon(R.drawable.ic_refresh)
            .setOngoing(true)
            .build()

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }
    }

    companion object {
        private const val TAG = "AiUsageOAuth"
        private const val CHANNEL_ID = "oauth_sign_in"
        private const val NOTIFICATION_ID = 42
        private const val EXTRA_PROVIDER = "provider"
        private const val PENDING_TTL_MS = 10 * 60 * 1000L
        private const val CALLBACK_TIMEOUT_MS = 10 * 60 * 1000L

        fun intent(context: Context, provider: ProviderId): Intent =
            Intent(context, OAuthCallbackService::class.java).putExtra(EXTRA_PROVIDER, provider.key)
    }
}

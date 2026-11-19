package io.github.rzukic.usagetop.update

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.os.Build
import android.util.Log

/**
 * Where Android reports on an update handed to it. Success never arrives: by
 * then this process has been replaced by the new version.
 */
class UpdateReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        when (val status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)) {
            PackageInstaller.STATUS_SUCCESS -> Unit

            // Android wants the user to allow or confirm it. That screen can only
            // be opened while the app is on screen; from the background the
            // download just stays ready until Install is tapped in Settings.
            PackageInstaller.STATUS_PENDING_USER_ACTION -> {
                Updater.installAnswered(context, needsUser = true, failure = null)
                val confirm = if (Build.VERSION.SDK_INT >= 33) {
                    intent.getParcelableExtra(Intent.EXTRA_INTENT, Intent::class.java)
                } else {
                    @Suppress("DEPRECATION") intent.getParcelableExtra(Intent.EXTRA_INTENT)
                }
                if (confirm != null && !Updater.inBackground()) {
                    try {
                        context.startActivity(confirm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                    } catch (problem: Exception) {
                        Log.w("Updater", "Could not ask to confirm the update", problem)
                    }
                }
            }

            PackageInstaller.STATUS_FAILURE_ABORTED ->
                Updater.installAnswered(context, needsUser = false, failure = null)

            else -> {
                val why = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE)
                Log.w("Updater", "Install failed: $status $why")
                Updater.installAnswered(context, needsUser = false, failure = "Android did not install the update")
            }
        }
    }
}

package io.github.rzukic.usagetop.update

import android.content.Context
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.NetworkType
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import java.util.concurrent.TimeUnit

/**
 * The six-hourly look for a newer release. A download it leaves ready goes in
 * at once when Android allows that without asking and the app is not on
 * screen; otherwise it waits for Install in Settings.
 */
class UpdateWorker(
    context: Context,
    params: WorkerParameters,
) : CoroutineWorker(context, params) {

    override suspend fun doWork(): Result {
        Updater.check(applicationContext)
        if (Updater.state.value.phase == Updater.Phase.READY &&
            Updater.installsUnattended(applicationContext) && Updater.inBackground()
        ) {
            Updater.install(applicationContext)
        }
        return Result.success()
    }

    companion object {
        private const val WORK = "usagetop-update"

        fun schedule(context: Context) {
            if (Updater.current == null) return
            val request = PeriodicWorkRequestBuilder<UpdateWorker>(6, TimeUnit.HOURS)
                .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
                .build()
            WorkManager.getInstance(context).enqueueUniquePeriodicWork(WORK, ExistingPeriodicWorkPolicy.KEEP, request)
        }
    }
}

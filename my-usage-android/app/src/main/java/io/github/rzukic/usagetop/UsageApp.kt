package io.github.rzukic.usagetop

import android.app.Application
import io.github.rzukic.usagetop.update.UpdateWorker
import io.github.rzukic.usagetop.update.Updater
import io.github.rzukic.usagetop.work.RefreshWorker

class UsageApp : Application() {

    override fun onCreate() {
        super.onCreate()
        RefreshWorker.schedulePeriodic(this)
        Updater.restore(this)
        UpdateWorker.schedule(this)
    }
}

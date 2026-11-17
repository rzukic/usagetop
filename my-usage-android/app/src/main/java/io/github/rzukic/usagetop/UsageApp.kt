package io.github.rzukic.usagetop

import android.app.Application
import io.github.rzukic.usagetop.work.RefreshWorker

class UsageApp : Application() {

    override fun onCreate() {
        super.onCreate()
        RefreshWorker.schedulePeriodic(this)
    }
}

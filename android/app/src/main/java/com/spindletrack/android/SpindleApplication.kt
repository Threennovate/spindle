package com.spindletrack.android

import android.app.Application
import android.util.Log
import com.spindletrack.android.data.SpindleRepository
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch

class SpindleApplication : Application() {
    val repository by lazy { SpindleRepository(this) }
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val notifications by lazy { TimerNotifications(this) }

    override fun onCreate() {
        super.onCreate()
        scope.launch {
            repository.state.collect { notifications.update(it) }
        }
        scope.launch {
            runCatching { repository.initialize() }
                .onFailure { Log.w("Spindle", "Could not initialize the account", it) }
        }
    }
}

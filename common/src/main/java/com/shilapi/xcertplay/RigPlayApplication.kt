package com.shilapi.xcertplay

import android.app.Activity
import android.app.Application
import android.os.Bundle
import com.shilapi.xcertplay.orchestration.CarPlayController

/**
 * Creates the process-wide SimHub link owner at process start, before any activity or receiver runs,
 * and tells it which rigPlay screen is in the foreground (`status.screen`, #29). Owns the dashboard
 * page (#51), created on the first dashboard SimHub names and kept loaded between opens.
 */
class RigPlayApplication : Application() {
    private var dashboardCreated = false

    /** The process's dashboard page; [DashboardActivity] shows it. Main thread. */
    val dashboard: DashboardWebViewHolder<DashboardWebViewSurface> by lazy {
        dashboardCreated = true
        DashboardWebViewSurface.newHolder(this)
    }

    override fun onCreate() {
        super.onCreate()
        RigSessionCoordinator.init(this)
        registerActivityLifecycleCallbacks(ForegroundTracker())
        // CarPlay's OEM icon ("SimHub") opens the dashboard instead of the launcher (#30).
        CarPlayController.hostUiOpener = { context ->
            RigSessionCoordinator.showDashboard(context)
            true
        }
        // Warm load (#51): the page starts loading when SimHub names a dashboard, not on the first tap.
        RigSessionCoordinator.addObserver(::onSimHubChanged)
        onSimHubChanged()
    }

    override fun onTrimMemory(level: Int) {
        super.onTrimMemory(level)
        if (dashboardCreated) dashboard.onTrimMemory(level)
    }

    private fun onSimHubChanged() {
        val state = RigSessionCoordinator.state
        val paired = RigSessionCoordinator.isPaired
        val content = DashboardContent.resolve(state, paired)
        // No WebView at all until there is a dashboard to load. The idle dashboard (#39) only loads
        // while the idle screen asks for it; the main one is the one kept warm.
        if (content is DashboardContent.Load || dashboardCreated) {
            dashboard.update(content, DashboardContent.resolveIdle(state, paired))
        }
    }

    private class ForegroundTracker : ActivityLifecycleCallbacks {
        private var started = 0

        override fun onActivityStarted(activity: Activity) { started++ }

        override fun onActivityResumed(activity: Activity) {
            RigSessionCoordinator.onForegroundChanged(foregroundOf(activity))
        }

        override fun onActivityStopped(activity: Activity) {
            started = (started - 1).coerceAtLeast(0)
            if (started == 0) RigSessionCoordinator.onForegroundChanged(RigSessionLifecycle.Foreground.NONE)
        }

        override fun onActivityCreated(activity: Activity, savedInstanceState: Bundle?) = Unit
        override fun onActivityPaused(activity: Activity) = Unit
        override fun onActivitySaveInstanceState(activity: Activity, outState: Bundle) = Unit
        override fun onActivityDestroyed(activity: Activity) = Unit
    }

    companion object {
        fun foregroundOf(activity: Activity): RigSessionLifecycle.Foreground = when (activity) {
            is CarPlayHostActivity -> RigSessionLifecycle.Foreground.CARPLAY
            is DashboardActivity ->
                if (activity.idleMode) RigSessionLifecycle.Foreground.IDLE_DASHBOARD else RigSessionLifecycle.Foreground.DASHBOARD
            is OfflineIdleActivity -> RigSessionLifecycle.Foreground.OFFLINE_IDLE
            else -> RigSessionLifecycle.Foreground.HOME
        }
    }
}

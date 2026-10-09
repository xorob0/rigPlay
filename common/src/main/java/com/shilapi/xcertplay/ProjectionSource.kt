package com.shilapi.xcertplay

import android.content.ComponentName
import android.content.Context
import android.content.Intent

/** Which phone platform the home screen projects. */
enum class ProjectionSource {
    CARPLAY, ANDROID_AUTO;

    companion object {
        val DEFAULT = CARPLAY

        /** Stored names that no longer exist fall back to CarPlay. */
        fun fromStored(name: String?): ProjectionSource = entries.firstOrNull { it.name == name } ?: DEFAULT
    }
}

object ProjectionSourceStore {
    private const val PREFS = "xcertplay_projection"
    private const val KEY_SOURCE = "source"

    fun load(context: Context): ProjectionSource =
        ProjectionSource.fromStored(context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString(KEY_SOURCE, null))

    fun save(context: Context, source: ProjectionSource) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putString(KEY_SOURCE, source.name).apply()
    }
}

/**
 * Entry points of the Android Auto receiver (DiAuto, vendored as the `:androidauto` module).
 *
 * The receiver is addressed by class name on purpose: `common` does not depend on the module, so
 * an APK built without it (the automotive flavour) still compiles and reports it as unavailable.
 */
object AndroidAutoReceiver {
    const val HOME_ACTIVITY = "com.andrerinas.openheadunit.main.MainActivity"
    const val SETTINGS_ACTIVITY = "com.andrerinas.openheadunit.main.SettingsActivity"
    const val AUTOMATION_ACTIVITY = "com.andrerinas.openheadunit.main.AutomationActivity"
    const val ACTION_CONNECT = "com.andrerinas.openheadunit.ACTION_CONNECT"

    fun isPackaged(context: Context): Boolean = runCatching {
        context.packageManager.getActivityInfo(ComponentName(context.packageName, HOME_ACTIVITY), 0)
    }.isSuccess

    /** The Android Auto home: connect, wireless setup and status. */
    fun homeIntent(context: Context): Intent = Intent().setClassName(context.packageName, HOME_ACTIVITY)

    fun settingsIntent(context: Context): Intent = Intent().setClassName(context.packageName, SETTINGS_ACTIVITY)

    /** Asks the Android Auto service to look for a phone on USB, through its automation entry point. */
    fun connectUsbIntent(context: Context): Intent =
        Intent(ACTION_CONNECT).setClassName(context.packageName, AUTOMATION_ACTIVITY)
}

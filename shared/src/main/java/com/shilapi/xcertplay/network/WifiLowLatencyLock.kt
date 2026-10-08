package com.shilapi.xcertplay.network

import android.content.Context
import android.net.wifi.WifiManager
import android.os.Build
import android.util.Log
import com.shilapi.xcertplay.simhub.SimHubDiscovery

/**
 * A Wi-Fi lock for the time CarPlay audio streams to the PC (`NetworkAudioSink`).
 *
 * Without one, Android lets the Wi-Fi chip doze between frames and scans for other networks while
 * connected; both hold the tablet's datagrams back and release them in bursts of 100 to 300 ms, which the
 * plugin's jitter buffer hears as the audio cutting out. `WIFI_MODE_FULL_LOW_LATENCY` (Android 10+) turns
 * both off while the app is in the foreground with the screen on, which is how a rig tablet runs;
 * `WIFI_MODE_FULL_HIGH_PERF` is the older, weaker form. Needs `WAKE_LOCK`.
 */
object WifiLowLatencyLock {
    private const val TAG = "rigPlay-WifiLock"

    /** `null` when the device has no Wi-Fi service; the sink then streams without a lock. */
    fun create(context: Context, tag: String = "rigplay-pc-audio"): SimHubDiscovery.NetworkLock? {
        val wifi = (context.applicationContext ?: context).getSystemService(Context.WIFI_SERVICE) as? WifiManager
            ?: return null
        val mode = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            WifiManager.WIFI_MODE_FULL_LOW_LATENCY
        } else {
            @Suppress("DEPRECATION")
            WifiManager.WIFI_MODE_FULL_HIGH_PERF
        }
        val lock = try {
            wifi.createWifiLock(mode, tag).apply { setReferenceCounted(false) }
        } catch (error: RuntimeException) {
            Log.w(TAG, "Wi-Fi lock unavailable", error)
            return null
        }
        return object : SimHubDiscovery.NetworkLock {
            override fun acquire() {
                if (!lock.isHeld) {
                    lock.acquire()
                    Log.i(TAG, "Wi-Fi lock held (mode $mode) while audio streams to the PC")
                }
            }

            override fun release() {
                if (lock.isHeld) {
                    lock.release()
                    Log.i(TAG, "Wi-Fi lock released")
                }
            }
        }
    }
}

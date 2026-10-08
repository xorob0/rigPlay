package com.shilapi.xcertplay

import android.view.Window
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat

/** Tablet-on-a-rig window behaviour (#28): both system bars hidden; a swipe shows them briefly (immersive sticky). */
internal object RigTabletWindow {
    /**
     * [edgeToEdge] false keeps the decor fitting system windows so the soft keyboard still resizes
     * the content (PIN entry, address dialogs); hidden bars take no space either way.
     */
    fun immersive(window: Window, edgeToEdge: Boolean = false) {
        WindowCompat.setDecorFitsSystemWindows(window, !edgeToEdge)
        WindowInsetsControllerCompat(window, window.decorView).apply {
            isAppearanceLightStatusBars = false
            hide(WindowInsetsCompat.Type.systemBars())
            systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }
    }
}

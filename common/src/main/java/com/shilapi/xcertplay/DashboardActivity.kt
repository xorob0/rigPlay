package com.shilapi.xcertplay

import android.content.Context
import android.content.Intent
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Bundle
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import android.webkit.WebChromeClient
import android.widget.Button
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import androidx.activity.ComponentActivity
import androidx.activity.OnBackPressedCallback
import com.shilapi.xcertplay.host.R

/**
 * The SimHub button (#30): the dashboard chosen in SimHub (`state.dashboardUrl`) full screen.
 *
 * The page itself lives in [RigPlayApplication.dashboard] (#51): loaded as soon as SimHub names a
 * dashboard and kept loaded between opens, so this screen only shows it. Opening is instant once
 * the page is loaded; closing takes the WebView out of the layout without destroying it.
 *
 * Returning to CarPlay (back gesture or the small "CarPlay" button) only reorders
 * [CarPlayHostActivity] to the front; the CarPlay session is never touched. While this activity
 * covers the projection, CarPlayHostActivity is merely stopped: its TextureView keeps the
 * SurfaceTexture the decoder renders into (only the hardware layer is dropped), so video shows the
 * next decoded frame on return, the same path "rigPlay home" has always used.
 *
 * In idle mode ([openIdle], #39) it shows the idle dashboard while no phone is connected
 * (`state.idleDashboardUrl`, else the main dashboard; [DashboardContent.resolveIdle]) and reports
 * `status.screen = idle`. The same page shows it: while this screen is started in idle mode it asks
 * the holder for the idle dashboard, and the holder goes back to the main one once it stops, so the
 * SimHub button stays instant. There a tap shows the Home / Settings toolbar ([IdleToolbar]) instead
 * of the CarPlay button, and a dashboard that fails to load hands over to [OfflineIdleActivity].
 */
class DashboardActivity : ComponentActivity(), DashboardWebViewSurface.FullscreenHost {
    private lateinit var holder: DashboardWebViewHolder<DashboardWebViewSurface>
    private var root: FrameLayout? = null
    private var pageContainer: FrameLayout? = null
    private var overlay: LinearLayout? = null
    private var overlayText: TextView? = null
    private var retryButton: Button? = null
    private var returnButton: Button? = null
    private var toolbar: IdleToolbar? = null
    private var shownSurface: DashboardWebViewSurface? = null
    private var fullscreenView: View? = null
    private var fullscreenCallback: WebChromeClient.CustomViewCallback? = null
    private val observer: () -> Unit = { refresh() }

    /** Showing the idle dashboard (#39) rather than the one the SimHub button opens. */
    var idleMode = false
        private set

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON or WindowManager.LayoutParams.FLAG_HARDWARE_ACCELERATED)
        RigTabletWindow.immersive(window, edgeToEdge = true)
        RigSessionCoordinator.init(this)
        idleMode = intent.getBooleanExtra(EXTRA_IDLE, false)
        holder = (application as RigPlayApplication).dashboard
        setContentView(buildContent())
        applyMode()
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                when {
                    fullscreenView != null -> exitFullscreen()
                    idleMode -> IdleToolbar.open(this@DashboardActivity, "home")
                    else -> returnToCarPlay()
                }
            }
        })
        attach()
    }

    /** The other mode was asked for (SimHub button over the idle dashboard, or the policy after it). */
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        val next = intent.getBooleanExtra(EXTRA_IDLE, false)
        if (next == idleMode) return
        idleMode = next
        applyMode()
        // The page switches now; onResume follows, which lets RigPlayApplication report the new screen.
        attach()
        refresh()
    }

    override fun dispatchTouchEvent(event: MotionEvent): Boolean {
        if (idleMode && event.actionMasked == MotionEvent.ACTION_DOWN) {
            // The idle screen that took over the home page after inactivity (#53): a tap goes back there.
            if (RigSessionCoordinator.returnFromIdle(this)) return true
            toolbar?.show()
        }
        return super.dispatchTouchEvent(event)
    }

    private fun applyMode() {
        returnButton?.visibility = if (idleMode) View.GONE else View.VISIBLE
        if (!idleMode) toolbar?.hide()
    }

    override fun onResume() {
        super.onResume()
        RigSessionCoordinator.addObserver(observer)
        attach()
        holder.setVisible(this, true)
        refresh()
    }

    override fun onPause() {
        RigSessionCoordinator.removeObserver(observer)
        holder.setVisible(this, false)
        super.onPause()
    }

    override fun onStop() {
        // Covered (CarPlay, the home page): keep the main dashboard warm for the SimHub button (#51).
        holder.setIdle(this, false)
        super.onStop()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) RigTabletWindow.immersive(window, edgeToEdge = true)
    }

    override fun onDestroy() {
        toolbar?.dispose()
        // The page stays loaded for the next open (#51); only this screen lets go of it.
        pageContainer?.let { container -> shownSurface?.detachFrom(container) }
        shownSurface = null
        holder.detach(this)
        super.onDestroy()
    }

    private fun buildContent(): View {
        val root = FrameLayout(this).apply { setBackgroundColor(Color.BLACK) }
        val pages = FrameLayout(this)
        root.addView(pages, FrameLayout.LayoutParams(-1, -1))
        val message = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            setBackgroundColor(Color.rgb(12, 17, 27))
            setPadding(dp(48), dp(48), dp(48), dp(48))
            visibility = View.GONE
        }
        val messageText = TextView(this).apply {
            textSize = 22f; gravity = Gravity.CENTER; setTextColor(Color.rgb(241, 245, 252))
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            accessibilityLiveRegion = View.ACCESSIBILITY_LIVE_REGION_POLITE
        }
        message.addView(messageText, LinearLayout.LayoutParams(-1, -2))
        val retry = Button(this).apply {
            text = getString(R.string.rig_dashboard_retry); isAllCaps = false; textSize = 18f
            setTextColor(Color.rgb(12, 17, 27))
            background = GradientDrawable().apply { setColor(Color.rgb(166, 200, 255)); cornerRadius = dp(20).toFloat() }
            setOnClickListener { holder.retry() }
        }
        message.addView(retry, LinearLayout.LayoutParams(dp(240), dp(60)).apply { topMargin = dp(24) })
        root.addView(message, FrameLayout.LayoutParams(-1, -1))
        // Small, translucent, out of the way of typical dash layouts' centre.
        val back = Button(this).apply {
            isAllCaps = false; textSize = 15f; setTextColor(Color.WHITE); alpha = 0.75f
            background = GradientDrawable().apply { setColor(0x99000000.toInt()); cornerRadius = dp(18).toFloat() }
            setPadding(dp(16), 0, dp(16), 0)
            setOnClickListener { returnToCarPlay() }
        }
        root.addView(back, FrameLayout.LayoutParams(-2, dp(44), Gravity.TOP or Gravity.END).apply {
            topMargin = dp(12); marginEnd = dp(12)
        })
        this.root = root
        pageContainer = pages; overlay = message; overlayText = messageText; retryButton = retry; returnButton = back
        toolbar = IdleToolbar(this, root)
        return root
    }

    /** Takes the holder's page for this screen's mode (creating it if a dashboard is due) and follows its changes. */
    private fun attach() {
        holder.listener = ::render
        holder.attach(this, idle = idleMode)
        render()
    }

    /** The link state changed: the holder decides whether the page loads. */
    private fun refresh() {
        val state = RigSessionCoordinator.state
        val paired = RigSessionCoordinator.isPaired
        holder.update(DashboardContent.resolve(state, paired), DashboardContent.resolveIdle(state, paired))
        render()
    }

    /** Shows the holder's page or the message for the current content. */
    private fun render() {
        if (isDestroyed || isFinishing) return
        returnButton?.text = getString(
            when {
                CarPlayBackgroundSession.active -> R.string.rig_dashboard_carplay
                RigSessionCoordinator.policyScreen() != null -> R.string.rig_idle_close
                CarPlayBackgroundSession.hasSession() -> R.string.rig_dashboard_carplay
                else -> R.string.rig_rigplay_home
            },
        )
        // Stopped in idle mode, the holder already went back to the main dashboard: nothing to show.
        if (holder.owner !== this || holder.idle != idleMode) return
        showSurface(holder.surface)
        val page = holder.page
        val state = RigSessionCoordinator.state
        val paired = RigSessionCoordinator.isPaired
        // In idle mode anything but a URL makes the policy switch to the rigPlay idle screen; the
        // message shows until then.
        val content = holder.content
            ?: if (idleMode) DashboardContent.resolveIdle(state, paired) else DashboardContent.resolve(state, paired)
        when (content) {
            is DashboardContent.Load ->
                if (page is DashboardWebViewHolder.Page.Failed) {
                    if (idleMode) {
                        // The fallback chain ends at the rigPlay idle screen (#39), which says the dashboard did not load.
                        OfflineIdleActivity.open(this)
                        finish()
                        return
                    }
                    showMessage(getString(R.string.rig_dashboard_unreachable, page.url), retry = true)
                } else {
                    showPage()
                }
            DashboardContent.NoDashboard -> showMessage(getString(R.string.rig_dashboard_no_dashboard), retry = false)
            DashboardContent.ServerOff -> showMessage(getString(R.string.rig_dashboard_server_off), retry = true)
            DashboardContent.Disconnected -> showMessage(getString(R.string.rig_dashboard_disconnected), retry = false)
            DashboardContent.NotPaired -> showMessage(getString(R.string.rig_dashboard_not_paired), retry = false)
        }
    }

    /** Keeps the holder's current surface (a new one after a release) in this screen's layout. */
    private fun showSurface(surface: DashboardWebViewSurface?) {
        val container = pageContainer ?: return
        if (surface !== shownSurface) {
            shownSurface?.detachFrom(container)
            shownSurface = surface
        }
        surface?.attachTo(container, this, this)
    }

    private fun showPage() {
        overlay?.visibility = View.GONE
        shownSurface?.webView?.visibility = View.VISIBLE
    }

    private fun showMessage(message: String, retry: Boolean) {
        shownSurface?.webView?.visibility = View.INVISIBLE
        overlayText?.text = message
        retryButton?.visibility = if (retry) View.VISIBLE else View.GONE
        overlay?.visibility = View.VISIBLE
    }

    /**
     * The page asked for full screen (#50): its view covers the WebView, under the return button and
     * the idle toolbar.
     */
    override fun showFullscreen(view: View, callback: WebChromeClient.CustomViewCallback) {
        val parent = root
        if (parent == null || fullscreenView != null) {
            callback.onCustomViewHidden()
            return
        }
        fullscreenView = view
        fullscreenCallback = callback
        view.setBackgroundColor(Color.BLACK)
        val under = parent.indexOfChild(returnButton).takeIf { it >= 0 } ?: parent.childCount
        parent.addView(view, under, FrameLayout.LayoutParams(-1, -1))
        RigTabletWindow.immersive(window, edgeToEdge = true)
    }

    override fun hideFullscreen() {
        fullscreenView?.let { root?.removeView(it) }
        fullscreenView = null
        fullscreenCallback = null
    }

    private fun exitFullscreen() {
        val callback = fullscreenCallback
        hideFullscreen()
        callback?.onCustomViewHidden()
    }

    /**
     * Back to CarPlay without touching the session; with no phone connected, to the screen the idle
     * policy wants (#39), else as before to CarPlayHostActivity or the rigPlay home.
     */
    private fun returnToCarPlay() {
        if (!CarPlayBackgroundSession.active) {
            when (RigSessionCoordinator.policyScreen()) {
                // Same activity: the new intent switches it to idle mode in place.
                RigSessionLifecycle.PolicyScreen.IDLE_DASHBOARD -> { RigSessionCoordinator.showPolicyScreen(); return }
                null -> Unit
                else -> if (RigSessionCoordinator.showPolicyScreen()) { finish(); return }
            }
        }
        val target = if (CarPlayBackgroundSession.hasSession()) CarPlayHostActivity::class.java else RigPlayActivity::class.java
        startActivity(Intent(this, target).addFlags(Intent.FLAG_ACTIVITY_REORDER_TO_FRONT))
        finish()
    }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()

    companion object {
        private const val EXTRA_IDLE = "com.shilapi.xcertplay.extra.IDLE_DASHBOARD"

        /** Opens the dashboard from anywhere: home button, CarPlay's OEM icon, `command showDashboard`. */
        fun open(context: Context) = start(context, idle = false)

        /** Opens the idle dashboard (#39); the screen policy's choice while no phone is connected. */
        fun openIdle(context: Context) = start(context, idle = true)

        private fun start(context: Context, idle: Boolean) {
            context.startActivity(
                Intent(context, DashboardActivity::class.java)
                    .putExtra(EXTRA_IDLE, idle)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_REORDER_TO_FRONT),
            )
        }
    }
}

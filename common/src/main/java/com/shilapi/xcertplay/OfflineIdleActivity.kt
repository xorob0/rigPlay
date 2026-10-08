package com.shilapi.xcertplay

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import android.widget.Button
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextClock
import android.widget.TextView
import androidx.activity.ComponentActivity
import androidx.activity.OnBackPressedCallback
import com.shilapi.xcertplay.host.R

/**
 * The built-in idle screen (#39): shown while no phone is connected and SimHub is down (the PC's web
 * server is gone, so nothing can come from it), or when the user prefers it to the idle dashboard.
 * A dimmed clock, the rigPlay name, what rigPlay waits for and the last PC's name. Everything is
 * local by design.
 *
 * A tap shows a small Home / Settings toolbar that hides itself again, except when the screen took
 * over the home page after "Go idle after" without a touch (#53): then a tap returns there. With "Turn the screen off"
 * set, the screen goes black and the backlight to its minimum after that many minutes; the window
 * keeps the screen on, so rigPlay can still bring up the idle dashboard or CarPlay when the PC or
 * the phone comes back. A tap wakes it.
 */
class OfflineIdleActivity : ComponentActivity() {
    private val main = Handler(Looper.getMainLooper())
    private var content: LinearLayout? = null
    private var statusView: TextView? = null
    private var detailView: TextView? = null
    private var toolbar: IdleToolbar? = null
    private var dark = false
    private var driftStep = 0
    private val observer: () -> Unit = { render() }
    private val goDark = Runnable { setDark(true) }

    /** Moves the content by a few pixels every minute against burn-in on an always-on panel. */
    private val drift = object : Runnable {
        override fun run() {
            driftStep = (driftStep + 1) % DRIFT.size
            val (x, y) = DRIFT[driftStep]
            content?.translationX = dp(x).toFloat()
            content?.translationY = dp(y).toFloat()
            main.postDelayed(this, DRIFT_INTERVAL_MS)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        RigTabletWindow.immersive(window, edgeToEdge = true)
        RigSessionCoordinator.init(this)
        setContentView(buildContent())
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() = IdleToolbar.open(this@OfflineIdleActivity, "home")
        })
    }

    override fun onResume() {
        super.onResume()
        RigSessionCoordinator.addObserver(observer)
        render()
        setDark(false)
        scheduleDark()
        main.removeCallbacks(drift)
        main.postDelayed(drift, DRIFT_INTERVAL_MS)
    }

    override fun onPause() {
        RigSessionCoordinator.removeObserver(observer)
        main.removeCallbacks(goDark)
        main.removeCallbacks(drift)
        super.onPause()
    }

    override fun onDestroy() {
        main.removeCallbacksAndMessages(null)
        toolbar?.dispose()
        super.onDestroy()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) RigTabletWindow.immersive(window, edgeToEdge = true)
    }

    override fun dispatchTouchEvent(event: MotionEvent): Boolean {
        if (event.actionMasked == MotionEvent.ACTION_DOWN) {
            scheduleDark()
            if (dark) {
                // The first tap only wakes the screen.
                setDark(false)
                return true
            }
            // Took over the home page after inactivity (#53): a tap goes back there.
            if (RigSessionCoordinator.returnFromIdle(this)) return true
            toolbar?.show()
        }
        return super.dispatchTouchEvent(event)
    }

    private fun buildContent(): View {
        val root = FrameLayout(this).apply { setBackgroundColor(Color.BLACK) }
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            setPadding(dp(48), dp(48), dp(48), dp(48))
        }
        column.addView(TextClock(this).apply {
            format12Hour = "h:mm"; format24Hour = "HH:mm"
            textSize = 112f; gravity = Gravity.CENTER; setTextColor(CLOCK)
            typeface = Typeface.create("sans-serif-light", Typeface.NORMAL)
        }, LinearLayout.LayoutParams(-2, -2))
        column.addView(TextClock(this).apply {
            format12Hour = "EEEE d MMMM"; format24Hour = "EEEE d MMMM"
            textSize = 22f; gravity = Gravity.CENTER; setTextColor(DIM)
        }, LinearLayout.LayoutParams(-2, -2))
        val brand = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL
            setPadding(0, dp(40), 0, 0)
        }
        brand.addView(ImageView(this).apply {
            setImageResource(R.drawable.ic_rigplay); alpha = 0.55f
            importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
        }, LinearLayout.LayoutParams(dp(36), dp(36)))
        brand.addView(TextView(this).apply {
            text = getString(R.string.rigplay); textSize = 24f; setTextColor(DIM)
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            setPadding(dp(12), 0, 0, 0)
        })
        column.addView(brand, LinearLayout.LayoutParams(-2, -2))
        val status = TextView(this).apply {
            textSize = 22f; gravity = Gravity.CENTER; setTextColor(DIM)
            setPadding(0, dp(28), 0, 0)
            accessibilityLiveRegion = View.ACCESSIBILITY_LIVE_REGION_POLITE
        }
        column.addView(status, LinearLayout.LayoutParams(-1, -2))
        val detail = TextView(this).apply {
            textSize = 16f; gravity = Gravity.CENTER; setTextColor(DIMMER)
            setPadding(0, dp(8), 0, 0)
        }
        column.addView(detail, LinearLayout.LayoutParams(-1, -2))
        root.addView(column, FrameLayout.LayoutParams(-1, -1))
        content = column; statusView = status; detailView = detail
        toolbar = IdleToolbar(this, root)
        return root
    }

    private fun render() {
        val (status, detail) = describe()
        statusView?.text = status
        detailView?.text = detail
        detailView?.visibility = if (detail.isNullOrEmpty()) View.GONE else View.VISIBLE
    }

    /** What rigPlay waits for, and why no idle dashboard is shown. */
    private fun describe(): Pair<String, String?> {
        val pairing = RigSessionCoordinator.pairing ?: return getString(R.string.rig_idle_not_paired) to null
        val state = RigSessionCoordinator.state
        if (!RigSessionCoordinator.simHubUp) {
            return getString(R.string.rig_idle_waiting_simhub) to getString(R.string.rig_idle_last_pc, pairing.name)
        }
        val connected = getString(R.string.rig_idle_connected, state.hostName ?: pairing.name)
        val why = if (AirPlayPersistence.loadIdleMode(this) != IdleMode.DASHBOARD) {
            null
        } else {
            when (DashboardContent.resolveIdle(state, paired = true)) {
                DashboardContent.ServerOff -> getString(R.string.rig_idle_server_off)
                DashboardContent.NoDashboard -> getString(R.string.rig_idle_no_dashboard)
                is DashboardContent.Load -> getString(R.string.rig_idle_load_failed)
                else -> null
            }
        }
        val waiting = getString(R.string.rig_idle_waiting_phone)
        return connected to (if (why == null) waiting else "$waiting\n$why")
    }

    private fun scheduleDark() {
        main.removeCallbacks(goDark)
        val minutes = AirPlayPersistence.loadIdleScreenOffMinutes(this)
        if (minutes > 0) main.postDelayed(goDark, minutes * 60_000L)
    }

    private fun setDark(value: Boolean) {
        if (dark == value) return
        dark = value
        content?.visibility = if (value) View.INVISIBLE else View.VISIBLE
        if (value) toolbar?.hide()
        window.attributes = window.attributes.apply {
            screenBrightness = if (value) {
                WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_OFF
            } else {
                WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE
            }
        }
    }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()

    companion object {
        private val CLOCK = Color.rgb(138, 147, 163)
        private val DIM = Color.rgb(104, 113, 128)
        private val DIMMER = Color.rgb(78, 86, 99)
        private const val DRIFT_INTERVAL_MS = 60_000L
        private val DRIFT = listOf(0 to 0, 6 to 3, 3 to -6, -6 to 3, -3 to -3)

        fun open(context: Context) {
            context.startActivity(
                Intent(context, OfflineIdleActivity::class.java)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_REORDER_TO_FRONT),
            )
        }
    }
}

/**
 * The idle screens' way back (#39): a tap shows a small Home / Settings toolbar in the top corner,
 * which hides itself after a few seconds. Leaving finishes the idle screen; the policy brings it
 * back on the next change (PC, phone).
 */
internal class IdleToolbar(private val activity: Activity, parent: FrameLayout) {
    private val main = Handler(Looper.getMainLooper())
    private val hideLater = Runnable { hide() }
    private val bar: LinearLayout = LinearLayout(activity).apply {
        orientation = LinearLayout.HORIZONTAL
        background = GradientDrawable().apply { setColor(0xCC000000.toInt()); cornerRadius = dp(22).toFloat() }
        setPadding(dp(6), dp(6), dp(6), dp(6))
        contentDescription = activity.getString(R.string.rig_idle_toolbar)
        visibility = View.GONE
        alpha = 0f
    }

    init {
        bar.addView(button(activity.getString(R.string.rig_idle_home)) { open(activity, "home") },
            LinearLayout.LayoutParams(-2, dp(48)))
        bar.addView(button(activity.getString(R.string.settings)) { open(activity, "settings") },
            LinearLayout.LayoutParams(-2, dp(48)).apply { marginStart = dp(6) })
        parent.addView(bar, FrameLayout.LayoutParams(-2, -2, Gravity.TOP or Gravity.END).apply {
            topMargin = dp(12); marginEnd = dp(12)
        })
    }

    fun show() {
        main.removeCallbacks(hideLater)
        if (bar.visibility != View.VISIBLE) {
            bar.visibility = View.VISIBLE
            bar.animate().cancel()
            bar.animate().alpha(1f).setDuration(FADE_MS).start()
        }
        main.postDelayed(hideLater, VISIBLE_MS)
    }

    fun hide() {
        main.removeCallbacks(hideLater)
        if (bar.visibility != View.VISIBLE) return
        bar.animate().cancel()
        bar.animate().alpha(0f).setDuration(FADE_MS).withEndAction { bar.visibility = View.GONE }.start()
    }

    fun dispose() = main.removeCallbacks(hideLater)

    private fun button(title: String, click: () -> Unit) = Button(activity).apply {
        text = title; isAllCaps = false; textSize = 16f; setTextColor(Color.WHITE)
        background = GradientDrawable().apply { setColor(0x33FFFFFF); cornerRadius = dp(18).toFloat() }
        setPadding(dp(18), 0, dp(18), 0); minHeight = dp(48); minWidth = dp(96)
        stateListAnimator = null
        setOnClickListener { click() }
    }

    private fun dp(value: Int): Int = (value * activity.resources.displayMetrics.density).toInt()

    companion object {
        private const val VISIBLE_MS = 5_000L
        private const val FADE_MS = 150L

        /** Leaves an idle screen for a page of the rigPlay home ("home", "settings"). */
        fun open(activity: Activity, page: String) {
            activity.startActivity(
                Intent(activity, RigPlayActivity::class.java)
                    .putExtra("page", page)
                    .addFlags(Intent.FLAG_ACTIVITY_REORDER_TO_FRONT),
            )
            activity.finish()
        }
    }
}

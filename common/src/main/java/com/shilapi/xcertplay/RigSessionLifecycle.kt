package com.shilapi.xcertplay

import com.shilapi.xcertplay.simhub.NavStatus
import com.shilapi.xcertplay.simhub.NowPlaying
import com.shilapi.xcertplay.simhub.Screen
import com.shilapi.xcertplay.simhub.SimHubCommand
import com.shilapi.xcertplay.simhub.SimHubMessage

/**
 * Couples the phone to the PC (#29): the iPhone is connected exactly while the paired SimHub PC is
 * reachable, and the plugin always knows what the tablet shows (`status`, §6.7).
 *
 * - link up ⇒ start CarPlay the way "Connect phone" does (deferred until a rigPlay screen is in the
 *   foreground, since Android blocks activity starts from the background);
 * - link lost ⇒ stop the session (AirPlay, Bonjour, Wi-Fi Direct group, Bluetooth link go down, so the
 *   iPhone drops CarPlay) and refuse reconnects until the link is back or the user connects by hand;
 * - every change of phone, screen, now-playing or route guidance ⇒ a new `status` (SimHubLink
 *   coalesces to 250 ms).
 *
 * Screen policy (#39), while a PC is paired, see [policyScreen]:
 * - phone connected ⇒ CarPlay;
 * - SimHub up, no phone ⇒ the idle dashboard ([IdleMode.DASHBOARD] and a URL to show), else the
 *   rigPlay idle screen;
 * - SimHub down ⇒ the rigPlay idle screen (the PC's web server is gone with it).
 *
 * The policy only acts when its answer changes (link up/lost, phone connected/gone, a new `state`, a
 * setting), and only replaces screens it owns: CarPlay and the two idle screens. A dashboard opened by
 * hand (SimHub button, `command showDashboard`) is never taken away, and a live CarPlay session is
 * never covered since a connected phone always means CarPlay.
 *
 * Inactivity (#53): the home page and settings (any non-CarPlay, non-dashboard rigPlay screen,
 * [Foreground.HOME]) stay while the user works on them. When the policy wants the rigPlay idle screen
 * ([PolicyScreen.OFFLINE_IDLE]: PC off, or PC on with "rigPlay screen" chosen or no dashboard to show)
 * and nobody touched rigPlay for [IdleInputs.idleAfterMinutes] (counted from the last touch, arriving
 * on HOME or the policy turning to the idle screen, whichever is latest), the idle screen takes over and
 * remembers where the user was; a tap on it returns there ([returnFromIdle]). With 0 minutes it takes
 * over at once when the policy turns to the idle screen while the user is on HOME, and not again until
 * the next such change, so the settings stay reachable. CarPlay, dashboards and the policy's own
 * screens are not timed.
 *
 * Like the phone start, a change while rigPlay is in the background waits until a rigPlay screen is in
 * the foreground (Android blocks activity starts from the background; an idle screen counts); one owed
 * while the phone is being started waits until its session exists, because CarPlayHostActivity has
 * to lay out once to start it.
 *
 * Not thread-safe: call everything on the main thread.
 */
class RigSessionLifecycle(
    private val link: SimHubLinkPort,
    private val phone: PhoneSession,
    private val screens: Screens,
    private val idle: IdleInputs = IdleInputs.NONE,
    private val userActivity: UserActivityMonitor = UserActivityMonitor(),
    private val timer: Timer = Timer.NONE,
    private val log: (String) -> Unit = {},
) {
    /** The CarPlay session as the coordinator sees it. */
    interface PhoneSession {
        /** A session exists (starting, running or stopping). */
        fun hasSession(): Boolean

        /** The iPhone's AirPlay session is up. */
        fun isConnected(): Boolean

        fun phoneName(): String?

        /** Why the phone cannot be started without the user (no iPhone chosen, setup error…), or `null`. */
        fun autoStartBlocker(): String?

        /** Starts CarPlay like the "Connect phone" button. */
        fun start()

        /** Stops the session like the "Disconnect" button. */
        fun stop(completion: () -> Unit)
    }

    interface Screens {
        fun showDashboard()
        fun showCarPlay()

        /** DashboardActivity in idle mode (#39). */
        fun showIdleDashboard()

        /** The built-in rigPlay idle screen, [OfflineIdleActivity] (#39). */
        fun showOfflineIdle()

        /** The rigPlay home page: an idle screen with nothing left to show (the pairing went away). */
        fun showHome()

        /** Back to the home page or settings the idle screen took over from, as the user left it (#53). */
        fun returnHome()
    }

    /** One pending callback on the main thread: the inactivity check (#53). */
    interface Timer {
        /** Replaces any pending callback with [action] in [delayMs]. */
        fun schedule(delayMs: Long, action: () -> Unit)

        fun cancel()

        companion object {
            /** No timer: the idle screen never takes over after inactivity. */
            val NONE: Timer = object : Timer {
                override fun schedule(delayMs: Long, action: () -> Unit) = Unit
                override fun cancel() = Unit
            }
        }
    }

    /** What the screen policy needs besides the link and the phone (#39). */
    interface IdleInputs {
        /** A SimHub PC is paired: the policy applies. Unpaired, the screens are the user's business. */
        fun paired(): Boolean

        fun mode(): IdleMode

        /** The idle dashboard or its fallback, the main dashboard, can be loaded ([DashboardContent.resolveIdle]). */
        fun idleDashboardAvailable(): Boolean

        /** "Go idle after" (#53): minutes without a touch before the idle screen takes over HOME; 0 = immediately. */
        fun idleAfterMinutes(): Int = IdleAfter.DEFAULT

        companion object {
            /** No pairing, hence no policy. */
            val NONE: IdleInputs = object : IdleInputs {
                override fun paired() = false
                override fun mode() = IdleMode.DEFAULT
                override fun idleDashboardAvailable() = false
            }
        }
    }

    /** What rigPlay shows in the foreground. */
    enum class Foreground { NONE, HOME, CARPLAY, DASHBOARD, IDLE_DASHBOARD, OFFLINE_IDLE }

    /** The screen the policy wants (#39). */
    enum class PolicyScreen { CARPLAY, IDLE_DASHBOARD, OFFLINE_IDLE }

    /** True while the paired PC's link is up. */
    var linkUp = false
        private set

    /** False after SimHub dropped the phone: CarPlayHostActivity must not reconnect or start on its own. */
    var phoneConnectionAllowed = true
        private set

    var foreground = Foreground.NONE
        private set

    var nowPlaying: NowPlaying? = null
        private set

    /** CarPlay's next maneuver for `status.nav` (#47); `null` without route guidance. */
    var nav: NavStatus? = null
        private set

    /** `command media` goes here (#32). Without a handler the plugin gets `commandUnavailable`. */
    var mediaCommandHandler: ((SimHubCommand.Media) -> Unit)? = null

    private var pendingStart = false
    private var lastStatus: SimHubMessage.Status? = null

    /** The policy's last answer; screens move only when it changes. */
    private var policyTarget: PolicyScreen? = null

    /** [policyTarget] still has to be shown (waiting for the foreground or for the phone's session). */
    private var screenOwed = false

    /** [startPhone] opened CarPlayHostActivity and its session does not exist yet. */
    private var phoneStarting = false

    /** SimHub went away and the phone is being dropped: it no longer counts as connected. */
    private var phoneStopping = false

    /**
     * The screen the idle screen took over from after inactivity (#53), always [Foreground.HOME]; a tap
     * on the idle screen returns there. `null` when the idle screen came for another reason.
     */
    var idleTakenOverFrom: Foreground? = null
        private set

    /** [UserActivityMonitor.now] when the policy last turned to the rigPlay idle screen (#53). */
    private var idleTargetSince = Long.MIN_VALUE

    fun onLinkUp() {
        linkUp = true
        phoneConnectionAllowed = true
        if (phone.hasSession()) {
            log("SimHub up; phone session already running")
        } else {
            val blocker = phone.autoStartBlocker()
            when {
                blocker != null -> log("SimHub up; not starting the phone: $blocker")
                foreground == Foreground.NONE -> {
                    log("SimHub up; starting the phone when rigPlay is in the foreground")
                    pendingStart = true
                }
                else -> startPhone()
            }
        }
        evaluate()
        publishStatus()
    }

    fun onLinkLost() {
        linkUp = false
        pendingStart = false
        phoneStarting = false
        phoneConnectionAllowed = false
        if (phone.hasSession()) {
            log("SimHub lost; dropping the phone")
            phoneStopping = true
            phone.stop {}
        }
        evaluate()
        publishStatus()
    }

    /** The pairing was removed: no coupling any more, the phone is the user's business. */
    fun onUnpaired() {
        linkUp = false
        pendingStart = false
        phoneStarting = false
        phoneConnectionAllowed = true
        idleTakenOverFrom = null
        evaluate()
    }

    /** A new `state` (dashboard URLs, web server) arrived or an idle setting changed (#39). */
    fun onIdleInputsChanged() = evaluate()

    /**
     * The user pressed Connect phone / Connect with USB. Always allowed; returns true when the user
     * should be warned that SimHub is down (audio then stays on the tablet).
     */
    fun onManualConnect(paired: Boolean): Boolean {
        phoneConnectionAllowed = true
        return paired && !linkUp
    }

    fun onPhoneChanged() {
        if (phoneStarting && (phone.hasSession() || phone.isConnected())) phoneStarting = false
        if (phoneStopping && !phone.hasSession()) phoneStopping = false
        evaluate()
        publishStatus()
    }

    fun onForegroundChanged(next: Foreground) {
        if (foreground == next) return
        // The user left CarPlayHostActivity before it could start the phone (permission prompt, Home).
        if (phoneStarting && foreground == Foreground.CARPLAY && next != Foreground.NONE) phoneStarting = false
        foreground = next
        if (next == Foreground.HOME || next == Foreground.DASHBOARD) {
            // The user is somewhere by hand (or back from the idle screen): arriving counts as activity.
            idleTakenOverFrom = null
            if (next == Foreground.HOME) userActivity.onUserInteraction()
        }
        if (pendingStart && next != Foreground.NONE) {
            pendingStart = false
            if (linkUp && !phone.hasSession() && phone.autoStartBlocker() == null) startPhone()
        }
        settle()
        armInactivity()
        publishStatus()
    }

    /**
     * A tap on an idle screen (#53). When that screen took over after inactivity, goes back to where
     * the user was and returns true: the caller finishes the idle screen. Otherwise false: the tap is
     * the idle screen's own (its toolbar).
     */
    fun returnFromIdle(): Boolean {
        val from = idleTakenOverFrom ?: return false
        if (foreground != Foreground.OFFLINE_IDLE && foreground != Foreground.IDLE_DASHBOARD) return false
        idleTakenOverFrom = null
        userActivity.onUserInteraction()
        log("screen policy: tap on the idle screen; back to $from")
        screens.returnHome()
        return true
    }

    fun updateNowPlaying(value: NowPlaying?) {
        nowPlaying = value
        publishStatus()
    }

    fun updateNav(value: NavStatus?) {
        nav = value
        publishStatus()
    }

    fun onCommand(command: SimHubCommand) {
        when (command) {
            SimHubCommand.ShowDashboard -> screens.showDashboard()
            SimHubCommand.ShowCarPlay ->
                if (phone.hasSession()) screens.showCarPlay() else link.sendCommandUnavailable("no phone connected")
            is SimHubCommand.Media -> {
                val handler = mediaCommandHandler
                if (handler == null) link.sendCommandUnavailable("media commands are not available") else handler(command)
            }
        }
    }

    /**
     * What the tablet should show by itself now (#39); `null` when no PC is paired and no phone is
     * connected: nothing.
     */
    fun policyScreen(): PolicyScreen? = when {
        phone.isConnected() && !phoneStopping -> PolicyScreen.CARPLAY
        !idle.paired() -> null
        linkUp && idle.mode() == IdleMode.DASHBOARD && idle.idleDashboardAvailable() -> PolicyScreen.IDLE_DASHBOARD
        else -> PolicyScreen.OFFLINE_IDLE
    }

    /**
     * Shows the policy's screen now, whatever is in the foreground: the dashboard's close button,
     * rigPlay opening with automatic connection on, the home page's idle screen button. Call it from
     * a foreground activity. Returns false, showing nothing, when the policy has no screen.
     */
    fun showPolicyScreen(): Boolean {
        val target = policyScreen() ?: return false
        policyTarget = target
        screenOwed = false
        show(target)
        return true
    }

    /** The snapshot the plugin should see now. */
    fun currentStatus(): SimHubMessage.Status {
        val connected = phone.isConnected()
        val screen = when (foreground) {
            Foreground.DASHBOARD -> Screen.DASHBOARD
            Foreground.CARPLAY -> if (connected) Screen.CARPLAY else Screen.IDLE
            Foreground.HOME, Foreground.IDLE_DASHBOARD, Foreground.OFFLINE_IDLE -> Screen.IDLE
            Foreground.NONE -> Screen.OFF
        }
        return SimHubMessage.Status(
            phoneConnected = connected,
            phoneName = if (connected) phone.phoneName() else null,
            screen = screen,
            nowPlaying = if (connected) nowPlaying else null,
            nav = if (connected) nav else null,
        )
    }

    /** Sends the snapshot when it changed, or always with [force]. Safe while the link is down (sent after pairing). */
    fun publishStatus(force: Boolean = false) {
        val next = currentStatus()
        if (!force && next == lastStatus) return
        lastStatus = next
        link.send(next)
    }

    private fun startPhone() {
        log("SimHub up; starting the phone")
        phoneStarting = true
        // Once the session exists, the idle screen covers CarPlayHostActivity's "waiting for the iPhone".
        screenOwed = true
        phone.start()
    }

    private fun evaluate() {
        val next = policyScreen()
        if (next == PolicyScreen.CARPLAY) idleTakenOverFrom = null
        if (next != policyTarget) {
            log("screen policy: ${policyTarget ?: "none"} -> ${next ?: "none"}")
            policyTarget = next
            screenOwed = true
            if (next == PolicyScreen.OFFLINE_IDLE) {
                // The PC (or the phone) just went away: HOME gets the full "Go idle after" from now on (#53).
                idleTargetSince = userActivity.now()
                // "Go idle after: immediately": the user on HOME sees the idle screen at once.
                if (idle.idleAfterMinutes() == 0 && inactivityApplies()) {
                    takeOver("going idle immediately")
                    return
                }
            }
        }
        settle()
        armInactivity()
    }

    /** The idle screen may take over after inactivity (#53): the user is on HOME and the policy wants the rigPlay screen. */
    private fun inactivityApplies(): Boolean =
        foreground == Foreground.HOME && !phoneStarting && policyScreen() == PolicyScreen.OFFLINE_IDLE

    /** When the inactivity started: the last touch, or the policy turning to the idle screen if later. */
    private fun quietSince(): Long = maxOf(userActivity.lastInteractionAt, idleTargetSince)

    /** (Re)schedules the inactivity check for [quietSince] plus "Go idle after", or cancels it. */
    private fun armInactivity() {
        val minutes = idle.idleAfterMinutes()
        if (minutes <= 0 || !inactivityApplies()) {
            timer.cancel()
            return
        }
        val due = quietSince() + minutes * MINUTE_MS
        timer.schedule((due - userActivity.now()).coerceAtLeast(0L)) { onInactivityTimer() }
    }

    private fun onInactivityTimer() {
        val minutes = idle.idleAfterMinutes()
        if (minutes <= 0 || !inactivityApplies()) return
        // Touched since the check was scheduled: wait for the new deadline.
        if (userActivity.now() - quietSince() < minutes * MINUTE_MS) return armInactivity()
        takeOver("no touch for $minutes min")
    }

    /** Shows the rigPlay idle screen over HOME and remembers HOME for [returnFromIdle]. */
    private fun takeOver(reason: String) {
        log("screen policy: $reason; the idle screen takes over from $foreground")
        timer.cancel()
        idleTakenOverFrom = foreground
        policyTarget = PolicyScreen.OFFLINE_IDLE
        screenOwed = false
        show(PolicyScreen.OFFLINE_IDLE)
    }

    /** Shows the owed screen once it can be; drops it when the user is on a screen the policy does not own. */
    private fun settle() {
        if (!screenOwed || phoneStarting || foreground == Foreground.NONE) return
        screenOwed = false
        if (foreground !in POLICY_OWNED) return
        val target = policyTarget
        if (target != null) {
            show(target)
        } else if (foreground == Foreground.IDLE_DASHBOARD || foreground == Foreground.OFFLINE_IDLE) {
            log("screen policy: no PC paired; leaving the idle screen")
            screens.showHome()
        }
    }

    private fun show(target: PolicyScreen) {
        when (target) {
            PolicyScreen.CARPLAY -> if (foreground != Foreground.CARPLAY) screens.showCarPlay()
            PolicyScreen.IDLE_DASHBOARD -> if (foreground != Foreground.IDLE_DASHBOARD) screens.showIdleDashboard()
            PolicyScreen.OFFLINE_IDLE -> if (foreground != Foreground.OFFLINE_IDLE) screens.showOfflineIdle()
        }
    }

    private companion object {
        /** Screens the policy may replace on a change; HOME only after inactivity (#53), a hand-opened DASHBOARD never. */
        val POLICY_OWNED = setOf(Foreground.CARPLAY, Foreground.IDLE_DASHBOARD, Foreground.OFFLINE_IDLE)

        const val MINUTE_MS = 60_000L
    }
}

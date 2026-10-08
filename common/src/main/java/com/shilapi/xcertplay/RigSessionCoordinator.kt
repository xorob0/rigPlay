package com.shilapi.xcertplay

import android.content.Context
import android.graphics.BitmapFactory
import android.graphics.drawable.BitmapDrawable
import android.graphics.drawable.Drawable
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.app.Activity
import android.provider.Settings
import android.util.Log
import android.content.Intent
import com.shilapi.xcertplay.glance.CarPlayGlance
import com.shilapi.xcertplay.simhub.DiscoveredHost
import com.shilapi.xcertplay.simhub.NowPlaying
import com.shilapi.xcertplay.simhub.SimHubCommand
import com.shilapi.xcertplay.simhub.SimHubDiscovery
import com.shilapi.xcertplay.simhub.SimHubEndpoints
import com.shilapi.xcertplay.simhub.SimHubLink
import com.shilapi.xcertplay.simhub.SimHubLinkAudioTransport
import com.shilapi.xcertplay.simhub.SimHubLinkMicTransport
import com.shilapi.xcertplay.simhub.SimHubMediaBridge
import com.shilapi.xcertplay.simhub.SimHubMessage
import com.shilapi.xcertplay.simhub.SimHubNav
import com.shilapi.xcertplay.simhub.SimHubProtocol
import com.shilapi.xcertplay.simhub.SimHubState
import com.shilapi.xcertplay.simhub.SimHubStatusSink
import com.shilapi.xcertplay.host.R
import java.io.File

/**
 * Process-wide owner of the SimHub link (#26) and discovery, created by [RigPlayApplication]; runs
 * onboarding (#27) and couples the phone session to the PC through [RigSessionLifecycle] (#29), which
 * also picks CarPlay, the idle dashboard or the rigPlay idle screen while nothing else was asked for (#39).
 *
 * Everything here runs on the main thread: link and discovery callbacks are posted to it, and the
 * UI methods must be called from it. Observers ([addObserver]) are told about any change of
 * [state], [hosts], [pairing], [pairingStep] or the phone session.
 *
 * API for other features (callable from any thread):
 * - [updateNowPlaying]: the iPhone's now-playing changed (#32); sent in the next `status`.
 * - `status.nav` (#47) follows [CarPlayGlance] on its own (see followRouteGuidance).
 * - [sendStatus]: re-send the current `status` snapshot (phone, screen, now playing).
 * - [mediaCommandHandler]: receives `command media` from SimHub wheel buttons (#32), on the main thread;
 *   by default [SimHubEndpoints.mediaBridge].
 * - [simHubLink]: the link itself, e.g. for `state.audioEnabled` (#31).
 *
 * It also owns [SimHubEndpoints]: [SimHubEndpoints.audioTransport] is set while the link exists and
 * cleared when it is stopped, and [SimHubEndpoints.statusSink] forwards now-playing into the single
 * `status` sender, [RigSessionLifecycle.publishStatus].
 */
object RigSessionCoordinator {
    private const val TAG = "rigplay-coordinator"
    private const val NAV_EXPIRY_CHECK_MS = 2_000L

    /** `hello.features` (§7.3): `telemetry` feeds [SimHubEndpoints.telemetry] (#41). */
    private val LINK_FEATURES = setOf(SimHubProtocol.FEATURE_IDLE_DASHBOARD, SimHubProtocol.FEATURE_TELEMETRY, SimHubProtocol.FEATURE_MIC)

    private val main = Handler(Looper.getMainLooper())
    private val observers = LinkedHashSet<() -> Unit>()

    /** Last touch on a rigPlay screen, for "Go idle after" (#53). */
    private val userActivity = UserActivityMonitor { SystemClock.elapsedRealtime() }

    private var appContext: Context? = null
    private lateinit var link: SimHubLink
    private lateinit var linkPort: SimHubLinkPort
    private lateinit var flow: SimHubPairingFlow
    private lateinit var lifecycle: RigSessionLifecycle
    private var discovery: SimHubDiscovery? = null
    private var onboardingVisible = false
    private var iconCache: SimHubIconCache? = null

    /** The stored pairing, or `null` while unpaired. */
    var pairing: SimHubPairing? = null
        private set

    /** Latest link state, as seen on the main thread. */
    var state: SimHubState = SimHubState.STOPPED
        private set

    /** PCs heard from on the network (sorted by name). */
    var hosts: List<DiscoveredHost> = emptyList()
        private set

    /** Why discovery could not start (port busy, no Wi-Fi), or `null`. */
    var discoveryError: String? = null
        private set

    /** "Set up later" on the onboarding page; asked again on the next launch. */
    var onboardingSkipped = false

    val initialized: Boolean get() = appContext != null
    val isPaired: Boolean get() = pairing != null
    val pairingStep: SimHubPairingFlow.Step get() = if (initialized) flow.step else SimHubPairingFlow.Step.ChooseHost

    /** The link, once [init] ran. Its methods are thread-safe. */
    val simHubLink: SimHubLink? get() = if (initialized) link else null

    /**
     * False after SimHub went away and dropped the phone: [CarPlayHostActivity] must neither
     * reconnect nor start a new session until the link is back or the user connects by hand.
     */
    val phoneConnectionAllowed: Boolean get() = !initialized || lifecycle.phoneConnectionAllowed

    /** True while the paired PC is connected. */
    val simHubUp: Boolean get() = initialized && lifecycle.linkUp

    /**
     * Receives `command media` (#32), on the main thread. Defaults to [SimHubEndpoints.mediaBridge], which
     * presses the button on the CarPlay session bound by `CarPlayMediaKeys.attach`. Set to `null`: the
     * plugin gets `commandUnavailable`.
     */
    var mediaCommandHandler: ((SimHubCommand.Media) -> Unit)?
        get() = pendingMediaHandler
        set(value) {
            pendingMediaHandler = value
            if (initialized) lifecycle.mediaCommandHandler = value
        }
    private var pendingMediaHandler: ((SimHubCommand.Media) -> Unit)? = { command -> routeMediaToBridge(command) }

    /** Creates the link and connects to the paired PC, if any. Idempotent. */
    fun init(context: Context) {
        if (appContext != null) return
        val app = context.applicationContext ?: context
        appContext = app
        link = SimHubLink(identity(app), LinkListener, features = LINK_FEATURES)
        linkPort = EpochLinkPort(SimHubLinkPort.of(link))
        flow = SimHubPairingFlow(linkPort) { notifyObservers() }
        lifecycle = RigSessionLifecycle(
            linkPort, RigPhoneSession(app), AppScreens(app), AppIdleInputs(app), userActivity, HandlerTimer(main),
        ) { Log.i(TAG, it) }
        lifecycle.mediaCommandHandler = pendingMediaHandler
        // CarPlay audio to the PC (#31): the link exists from here on.
        attachAudioTransport()
        // One owner of `status` (§6.7): RigSessionLifecycle.publishStatus. The bridge only feeds it now
        // playing; see NowPlayingToLifecycle.
        SimHubEndpoints.statusSink = NowPlayingToLifecycle
        followRouteGuidance()
        CarPlayBackgroundSession.onChanged = { main.post(::onPhoneSessionChanged) }
        // SimHub's own icon (#52), fetched from the PC once per SimHub.
        iconCache = SimHubIconCache(
            dir = File(app.filesDir, SimHubIconCache.DIRECTORY),
            http = SimHubIconCache.UrlSource,
            io = { task -> Thread(task, "rigplay-simhub-icon").apply { isDaemon = true }.start() },
            main = { task -> main.post(task) },
            clock = { android.os.SystemClock.elapsedRealtime() },
            log = { Log.i(TAG, it) },
        ) { notifyObservers() }
        pairing = AirPlayPersistence.loadSimHubPairing(app)
        pairing?.let(::startPaired)
        updateDiscovery()
    }

    fun addObserver(observer: () -> Unit) { observers.add(observer) }
    fun removeObserver(observer: () -> Unit) { observers.remove(observer) }

    // --- session coupling (#29) -----------------------------------------------------------------

    /** The iPhone's now-playing (#32); `null` when unknown. Any thread. */
    fun updateNowPlaying(nowPlaying: NowPlaying?) = onMain { if (initialized) lifecycle.updateNowPlaying(nowPlaying) }

    /** Re-sends the current `status` snapshot to the plugin. Any thread. */
    fun sendStatus() = onMain { if (initialized) lifecycle.publishStatus(force = true) }

    /** Current `status` snapshot, for diagnostics and tests. */
    fun currentStatus(): SimHubMessage.Status? = if (initialized) lifecycle.currentStatus() else null

    /**
     * The user pressed Connect phone or Connect with USB. Returns true when the UI should warn that
     * SimHub is not running (audio would stay on the tablet); the connection goes ahead either way.
     */
    fun onManualConnect(): Boolean = initialized && lifecycle.onManualConnect(paired = isPaired)

    /**
     * SimHub's icon cached for the paired PC (#52), or `null` until it was fetched once. Reads only the
     * stored pairing and the file, so any thread may call it (CarPlay's session start does).
     */
    fun simHubIconFile(context: Context): File? {
        val app = context.applicationContext ?: context
        val hostId = (if (Looper.myLooper() === Looper.getMainLooper()) pairing else null)?.hostId
            ?: AirPlayPersistence.loadSimHubPairing(app)?.hostId
        return SimHubIconCache.file(File(app.filesDir, SimHubIconCache.DIRECTORY), hostId)
    }

    /** [simHubIconFile] as a [sizePx] square drawable, for the SimHub buttons. Main thread. */
    fun simHubIcon(context: Context, sizePx: Int): Drawable? {
        val file = simHubIconFile(context) ?: return null
        val bitmap = BitmapFactory.decodeFile(file.absolutePath) ?: return null
        return BitmapDrawable(context.resources, bitmap).apply { setBounds(0, 0, sizePx, sizePx) }
    }

    /** The SimHub button, CarPlay's OEM icon and `command showDashboard` (#30): the main dashboard. Any thread. */
    fun showDashboard(context: Context) = DashboardActivity.open(context.applicationContext ?: context)

    // --- idle mode (#39) ------------------------------------------------------------------------

    /** What the tablet would show by itself now (see [RigSessionLifecycle.policyScreen]); `null`: no policy. */
    fun policyScreen(): RigSessionLifecycle.PolicyScreen? = if (initialized) lifecycle.policyScreen() else null

    /**
     * Shows CarPlay or the idle screen the policy wants; call it from a foreground activity. False
     * when there is none (no PC paired, no phone).
     */
    fun showPolicyScreen(): Boolean = initialized && lifecycle.showPolicyScreen()

    /** Settings → "When no iPhone is connected" changed. */
    fun onIdleSettingsChanged() {
        if (initialized) lifecycle.onIdleInputsChanged()
        notifyObservers()
    }

    /** A touch or key on a rigPlay screen (`Activity.onUserInteraction`), for "Go idle after" (#53). Main thread. */
    fun onUserInteraction() = userActivity.onUserInteraction()

    /**
     * A tap on an idle screen (#53): when it took over after inactivity, reopens the home page or
     * settings the user was on, finishes [activity] and returns true. False: the tap is the idle screen's own.
     */
    fun returnFromIdle(activity: Activity): Boolean {
        if (!initialized || !lifecycle.returnFromIdle()) return false
        activity.finish()
        return true
    }

    /** Which rigPlay screen is in the foreground; from [RigPlayApplication]'s activity callbacks. */
    fun onForegroundChanged(foreground: RigSessionLifecycle.Foreground) {
        if (!initialized) return
        // The lifecycle derives `status.screen` from this and publishes it itself.
        lifecycle.onForegroundChanged(foreground)
    }

    private fun onPhoneSessionChanged() {
        if (!initialized) return
        lifecycle.onPhoneChanged()
        appContext?.let(RigPlaySessionService::refresh)
        notifyObservers()
    }

    /** "CarPlay: … · SimHub: …" for the foreground service notification. */
    fun notificationText(context: Context): String {
        val phone = when {
            CarPlayBackgroundSession.active -> context.getString(R.string.rig_notification_phone_connected)
            else -> context.getString(R.string.rig_notification_phone_connecting)
        }
        val simhub = when {
            pairing == null -> context.getString(R.string.rig_notification_simhub_unpaired)
            state.paired -> context.getString(R.string.rig_notification_simhub_connected, state.hostName ?: pairing?.name ?: "")
            else -> context.getString(R.string.rig_notification_simhub_waiting)
        }
        return context.getString(R.string.rig_notification_text, phone, simhub)
    }

    private class AppScreens(private val context: Context) : RigSessionLifecycle.Screens {
        override fun showDashboard() = DashboardActivity.open(context)

        override fun showCarPlay() {
            context.startActivity(
                Intent(context, CarPlayHostActivity::class.java)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_REORDER_TO_FRONT),
            )
        }

        override fun showIdleDashboard() = DashboardActivity.openIdle(context)

        override fun showOfflineIdle() = OfflineIdleActivity.open(context)

        override fun showHome() {
            context.startActivity(
                Intent(context, RigPlayActivity::class.java)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_REORDER_TO_FRONT),
            )
        }

        override fun returnHome() {
            context.startActivity(
                Intent(context, RigPlayActivity::class.java)
                    .putExtra(RigPlayActivity.EXTRA_KEEP_PAGE, true)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_REORDER_TO_FRONT),
            )
        }
    }

    /** [RigSessionLifecycle.Timer] on the main thread's handler. */
    private class HandlerTimer(private val handler: Handler) : RigSessionLifecycle.Timer {
        private var pending: Runnable? = null

        override fun schedule(delayMs: Long, action: () -> Unit) {
            cancel()
            val next = Runnable { pending = null; action() }
            pending = next
            handler.postDelayed(next, delayMs)
        }

        override fun cancel() {
            pending?.let(handler::removeCallbacks)
            pending = null
        }
    }

    /** The idle policy's view of the pairing, the settings and the latest `state` (#39). Main thread. */
    private class AppIdleInputs(private val context: Context) : RigSessionLifecycle.IdleInputs {
        override fun paired(): Boolean = pairing != null
        override fun mode(): IdleMode = AirPlayPersistence.loadIdleMode(context)
        override fun idleDashboardAvailable(): Boolean = DashboardContent.idleDashboardAvailable(state, pairing != null)
        override fun idleAfterMinutes(): Int = AirPlayPersistence.loadIdleAfterMinutes(context)
    }

    // --- onboarding (#27) -----------------------------------------------------------------------

    /** The onboarding page is on screen: listen for beacons even when not paired. */
    fun setOnboardingVisible(visible: Boolean) {
        if (onboardingVisible == visible) return
        onboardingVisible = visible
        updateDiscovery()
    }

    fun connect(host: String, port: Int, name: String? = null) {
        if (!initialized) return
        flow.connect(host, port, name)
        notifyObservers()
    }

    fun submitPin(pin: String): Boolean = initialized && flow.submitPin(pin)
    fun requestNewPin() { if (initialized) flow.requestNewPin() }
    fun retryPairing() { if (initialized) flow.retry() }

    /** Leaves the PIN/progress step. A paired PC is reconnected. */
    fun cancelPairing() {
        if (!initialized) return
        flow.cancel()
        pairing?.let(::startPaired)
        notifyObservers()
    }

    /** Settings → SimHub → Reconnect: connect now instead of waiting for the back-off. */
    fun reconnect() {
        val current = pairing ?: return
        startPaired(current)
        linkPort.reconnectNow()
    }

    /** Settings → SimHub → Forget: deletes the token locally (§8) and stops the link. */
    fun forget() {
        val context = appContext ?: return
        Log.i(TAG, "forgetting SimHub ${pairing?.hostId}")
        AirPlayPersistence.clearSimHubPairing(context)
        pairing = null
        linkPort.stop()
        state = SimHubState.STOPPED
        flow.reset()
        lifecycle.onUnpaired()
        RigPlaySessionService.refresh(context)
        updateDiscovery()
        notifyObservers()
    }

    /** Settings → SimHub → Advanced: the discovery port changed. */
    fun restartDiscovery() {
        discovery?.stop()
        discovery = null
        updateDiscovery()
    }

    // --- SimHubEndpoints (#31/#32) --------------------------------------------------------------

    /** `command media` → the CarPlay session; no session takes it → `commandUnavailable` (§6.8). */
    private fun routeMediaToBridge(command: SimHubCommand.Media) {
        if (SimHubEndpoints.mediaBridge.onCommand(command) == SimHubMediaBridge.Result.UNAVAILABLE) {
            linkPort.sendCommandUnavailable("no CarPlay session takes media commands")
        }
    }

    /** Plugs the PC audio path into the link, unless it is already there. Main thread. */
    private fun attachAudioTransport() {
        if (!initialized || SimHubEndpoints.audioTransport != null) return
        // The PC microphone (#34), when the user chose it: MicrophoneUplink asks for it each time the phone listens.
        val app = appContext
        SimHubEndpoints.microphone = SimHubLinkMicTransport(link) {
            app != null && AirPlayPersistence.loadMicrophoneSource(app) == com.shilapi.xcertplay.media.MicrophoneSource.PC
        }
        SimHubEndpoints.audioTransport = try {
            SimHubLinkAudioTransport(link)
        } catch (error: java.io.IOException) {
            // No UDP socket: audio stays on the tablet (SwitchingMediaSink sees no transport).
            Log.w(TAG, "SimHub audio transport unavailable", error)
            null
        }
    }

    /** The link stopped on purpose (Forget, a new PC): audio falls back to the tablet. */
    private fun detachAudioTransport() {
        SimHubEndpoints.microphone = null
        val transport = SimHubEndpoints.audioTransport
        SimHubEndpoints.audioTransport = null
        (transport as? java.io.Closeable)?.let { runCatching { it.close() } }
    }

    /**
     * The [SimHubEndpoints.statusSink] for the rig app. [RigSessionLifecycle.publishStatus] is the only
     * sender of `status` lines: it already knows the phone (CarPlayBackgroundSession) and the screen
     * (foreground activity), so a second full-status sender such as a link-level status merger would
     * race it with conflicting snapshots. The bridge's now-playing is forwarded into the lifecycle;
     * its phone updates are dropped because the lifecycle tracks the phone itself. Any thread.
     */
    private object NowPlayingToLifecycle : SimHubStatusSink {
        override fun updateNowPlaying(nowPlaying: NowPlaying?) = this@RigSessionCoordinator.updateNowPlaying(nowPlaying)
        override fun updatePhone(connected: Boolean, phoneName: String?) = Unit
    }

    // --- route guidance (#47) -------------------------------------------------------------------

    /** Route state expires without a new frame: while a maneuver is shown, re-read it so `nav` clears. */
    private val navExpiryCheck = object : Runnable {
        override fun run() {
            main.removeCallbacks(this)
            if (SimHubNav.of(CarPlayGlance.snapshot()) != null) main.postDelayed(this, NAV_EXPIRY_CHECK_MS)
        }
    }

    private fun followRouteGuidance() {
        CarPlayGlance.addListener { glance ->
            val nav = SimHubNav.of(glance)
            onMain {
                main.removeCallbacks(navExpiryCheck)
                if (nav != null) main.postDelayed(navExpiryCheck, NAV_EXPIRY_CHECK_MS)
                lifecycle.updateNav(nav)
            }
        }
    }

    // --- internals ------------------------------------------------------------------------------

    private fun startPaired(current: SimHubPairing) {
        linkPort.start(SimHubLink.Target(current.host, current.port, current.hostId, current.token))
    }

    private fun identity(context: Context): SimHubLink.Identity {
        val name = runCatching { Settings.Global.getString(context.contentResolver, "device_name") }.getOrNull()
            ?.takeIf { it.isNotBlank() }
            ?: listOf(Build.MANUFACTURER, Build.MODEL).filter { !it.isNullOrBlank() }.joinToString(" ")
                .ifBlank { "rigPlay tablet" }
        val version = runCatching { context.packageManager.getPackageInfo(context.packageName, 0).versionName }
            .getOrNull()?.takeIf { it.isNotBlank() } ?: "0.0.0"
        return SimHubLink.Identity(AirPlayPersistence.loadSimHubTabletId(context), name, version)
    }

    /** Beacons are needed while choosing a PC and while the paired PC is not connected (§9). */
    private fun updateDiscovery() {
        val context = appContext ?: return
        val wanted = onboardingVisible || flow.active || (pairing != null && !state.paired)
        if (!wanted) {
            discovery?.stop()
            discovery = null
            hosts = emptyList()
            return
        }
        if (discovery != null) return
        val next = SimHubDiscovery(
            listener = DiscoveryListener,
            port = AirPlayPersistence.loadSimHubDiscoveryPort(context),
            networkLock = SimHubDiscovery.wifiMulticastLock(context),
        )
        discoveryError = try {
            next.start()
            discovery = next
            null
        } catch (error: Exception) {
            Log.w(TAG, "discovery could not start", error)
            error.message ?: error.javaClass.simpleName
        }
    }

    private fun onLinkState(next: SimHubState) {
        state = next
        flow.onStateChanged(next)
        iconCache?.onState(next)
        // A beacon moved the paired PC to a new address (§9): remember it.
        val stored = pairing
        val target = link.currentTarget
        if (stored != null && target != null && target.hostId == stored.hostId &&
            (target.host != stored.host || target.port != stored.port)
        ) {
            val moved = stored.copy(host = target.host, port = target.port)
            pairing = moved
            appContext?.let { AirPlayPersistence.saveSimHubAddress(it, moved.host, moved.port) }
        }
        updateDiscovery()
        appContext?.let(RigPlaySessionService::refresh)
        // A new `state` may bring or take away the idle dashboard (#39).
        lifecycle.onIdleInputsChanged()
        notifyObservers()
    }

    private fun onPaired(hostId: String, token: String) {
        val context = appContext ?: return
        val stored = pairing
        val next = if (flow.active) {
            flow.onPaired(hostId, token, link.state.hostName)
        } else if (stored != null && stored.hostId == hostId) {
            // Resume: the plugin may hand out a new token; always store what it sent (§6.4).
            stored.copy(token = token, name = link.state.hostName ?: stored.name)
        } else {
            null
        }
        if (next != null) {
            AirPlayPersistence.saveSimHubPairing(context, next)
            pairing = next
            Log.i(TAG, "paired with ${next.name} (${next.hostId})")
        }
        updateDiscovery()
        notifyObservers()
    }

    private fun onTokenRevoked(hostId: String, code: String) {
        if (pairing?.hostId != hostId) return
        Log.i(TAG, "token revoked by $hostId ($code); back to onboarding")
        forget()
    }

    private fun notifyObservers() {
        for (observer in observers.toList()) {
            try {
                observer()
            } catch (error: RuntimeException) {
                Log.w(TAG, "observer failed", error)
            }
        }
    }

    private fun onMain(block: () -> Unit) {
        if (Looper.myLooper() === Looper.getMainLooper()) block() else main.post(block)
    }

    /**
     * Incremented whenever the link is stopped or pointed at another target, so that callbacks the
     * old session queued for the main thread are dropped instead of overwriting the new state.
     */
    @Volatile private var epoch = 0

    /** Runs [block] on the main thread unless the link was stopped or retargeted in between. */
    private fun fromLink(block: () -> Unit) {
        val captured = epoch
        onMain { if (captured == epoch) block() }
    }

    private class EpochLinkPort(private val delegate: SimHubLinkPort) : SimHubLinkPort by delegate {
        override fun start(target: SimHubLink.Target) {
            if (target != delegate.currentTarget || delegate.state.phase == SimHubState.Phase.STOPPED) epoch++
            delegate.start(target)
        }

        override fun stop() {
            // Deliberate stop (Forget, cancel, another PC): no PC audio until the link is up again.
            // The status sink stays: it only feeds the lifecycle, which is link-independent.
            epoch++
            delegate.stop()
            detachAudioTransport()
        }
    }

    private object LinkListener : SimHubLink.Listener {
        override fun onStateChanged(state: SimHubState) = fromLink { onLinkState(state) }
        override fun onPairResult(result: SimHubMessage.PairResult) = fromLink {
            flow.onPairResult(result)
        }
        override fun onPaired(hostId: String, token: String) = fromLink { this@RigSessionCoordinator.onPaired(hostId, token) }
        override fun onAttemptFailed(loss: SimHubLink.LinkLoss) = fromLink {
            flow.onAttemptFailed(loss)
            updateDiscovery()
            notifyObservers()
        }
        override fun onTokenRevoked(hostId: String, code: String) = fromLink {
            this@RigSessionCoordinator.onTokenRevoked(hostId, code)
        }
        override fun onLinkUp(state: SimHubState) = fromLink {
            // A stop() may have cleared the PC audio path; the link is up again.
            attachAudioTransport()
            // The plugin keeps no artwork across sessions (#47).
            SimHubArtworkPublisher.resend()
            // Only the stored PC drives the phone; onPaired ran just before and stored it.
            if (pairing != null) lifecycle.onLinkUp()
            notifyObservers()
        }
        override fun onLinkLost(loss: SimHubLink.LinkLoss) = fromLink {
            Log.i(TAG, "SimHub link lost: ${loss.reason} ${loss.detail ?: ""}")
            lifecycle.onLinkLost()
            notifyObservers()
        }
        // Media commands reach SimHubEndpoints.mediaBridge through mediaCommandHandler (see
        // routeMediaToBridge); showDashboard/showCarPlay stay in RigSessionLifecycle.
        override fun onCommand(command: SimHubCommand) = fromLink { lifecycle.onCommand(command) }
        // Up to 10 Hz: straight into the thread-safe store on the link thread, not via the main thread.
        override fun onTelemetry(telemetry: SimHubMessage.Telemetry) = SimHubEndpoints.telemetry.update(telemetry)
    }

    private object DiscoveryListener : SimHubDiscovery.Listener {
        override fun onHostsChanged(hosts: List<DiscoveredHost>) = onMain {
            this@RigSessionCoordinator.hosts = hosts
            notifyObservers()
        }

        // Thread-safe; lets the link reconnect at once to the paired PC (§9).
        override fun onBeacon(host: DiscoveredHost) = linkPort.onBeacon(host)
    }
}

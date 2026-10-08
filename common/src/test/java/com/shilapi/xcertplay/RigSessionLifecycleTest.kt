package com.shilapi.xcertplay

import com.shilapi.xcertplay.RigSessionLifecycle.Foreground
import com.shilapi.xcertplay.simhub.MediaAction
import com.shilapi.xcertplay.simhub.NavStatus
import com.shilapi.xcertplay.simhub.NowPlaying
import com.shilapi.xcertplay.simhub.Screen
import com.shilapi.xcertplay.simhub.SimHubCommand
import com.shilapi.xcertplay.simhub.SimHubMessage
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class RigSessionLifecycleTest {
    private class FakePhone : RigSessionLifecycle.PhoneSession {
        var session = false
        var connected = false
        var blocker: String? = null
        var starts = 0
        var stops = 0
        override fun hasSession() = session
        override fun isConnected() = connected
        override fun phoneName(): String? = "Tim's iPhone"
        override fun autoStartBlocker() = blocker
        /** False: stop() only begins; [finishStop] ends the session later, like CarPlayBackgroundSession. */
        var stopsAtOnce = true
        override fun start() { starts++; session = true }
        override fun stop(completion: () -> Unit) {
            stops++
            if (stopsAtOnce) finishStop()
            completion()
        }
        fun finishStop() { session = false; connected = false }
    }

    private class FakeScreens : RigSessionLifecycle.Screens {
        var dashboards = 0
        var carPlays = 0
        var idleDashboards = 0
        var offlineIdles = 0
        var homes = 0
        val shown = mutableListOf<String>()
        override fun showDashboard() { dashboards++; shown += "dashboard" }
        override fun showCarPlay() { carPlays++; shown += "carplay" }
        override fun showIdleDashboard() { idleDashboards++; shown += "idleDashboard" }
        override fun showOfflineIdle() { offlineIdles++; shown += "offlineIdle" }
        override fun showHome() { homes++; shown += "home" }
        override fun returnHome() { shown += "returnHome" }
    }

    private class FakeIdle : RigSessionLifecycle.IdleInputs {
        var paired = true
        var mode = IdleMode.DASHBOARD
        var available = true
        var idleAfter = IdleAfter.DEFAULT
        override fun paired() = paired
        override fun mode() = mode
        override fun idleDashboardAvailable() = available
        override fun idleAfterMinutes() = idleAfter
    }

    /** The fake clock (ms) behind [userActivity] and [timer]. */
    private var now = 0L

    /** One pending callback like the Handler-based timer; [advance] runs it when due. */
    private inner class FakeTimer : RigSessionLifecycle.Timer {
        var dueAt: Long? = null
        private var action: (() -> Unit)? = null
        override fun schedule(delayMs: Long, action: () -> Unit) {
            dueAt = now + delayMs
            this.action = action
        }
        override fun cancel() {
            dueAt = null
            action = null
        }
        fun runIfDue(until: Long): Boolean {
            val due = dueAt ?: return false
            if (due > until) return false
            now = maxOf(now, due)
            val pending = action!!
            cancel()
            pending()
            return true
        }
    }

    private fun advance(ms: Long) {
        val end = now + ms
        while (timer.runIfDue(end)) Unit
        now = end
    }

    private fun minutes(value: Int) = value * 60_000L

    private val link = FakeSimHubLinkPort()
    private val phone = FakePhone()
    private val screens = FakeScreens()
    private val lifecycle = RigSessionLifecycle(link, phone, screens)

    // The idle policy (#39) only applies with a paired PC.
    private val idle = FakeIdle()
    private val userActivity = UserActivityMonitor { now }
    private val timer = FakeTimer()
    private val rig = RigSessionLifecycle(link, phone, screens, idle, userActivity, timer)

    private val song = NowPlaying("Teardrop", "Massive Attack", "Mezzanine", "Music", true, 83.4, 330.0, 1L)

    @Test fun linkUpStartsThePhoneWhenRigPlayIsOnScreen() {
        lifecycle.onForegroundChanged(Foreground.HOME)
        lifecycle.onLinkUp()
        assertEquals(1, phone.starts)
        assertTrue(lifecycle.linkUp)
    }

    @Test fun linkUpInBackgroundDefersTheStartUntilAScreenIsShown() {
        lifecycle.onLinkUp()
        assertEquals(0, phone.starts)
        lifecycle.onForegroundChanged(Foreground.HOME)
        assertEquals(1, phone.starts)
        lifecycle.onForegroundChanged(Foreground.NONE)
        lifecycle.onForegroundChanged(Foreground.HOME)
        assertEquals(1, phone.starts)
    }

    @Test fun deferredStartIsCancelledWhenTheLinkDropsFirst() {
        lifecycle.onLinkUp()
        lifecycle.onLinkLost()
        lifecycle.onForegroundChanged(Foreground.HOME)
        assertEquals(0, phone.starts)
    }

    @Test fun linkUpDoesNotRestartARunningSession() {
        lifecycle.onForegroundChanged(Foreground.HOME)
        phone.session = true
        lifecycle.onLinkUp()
        assertEquals(0, phone.starts)
    }

    @Test fun linkUpRespectsBlockers() {
        lifecycle.onForegroundChanged(Foreground.HOME)
        phone.blocker = "no iPhone chosen"
        lifecycle.onLinkUp()
        assertEquals(0, phone.starts)
        assertTrue(lifecycle.phoneConnectionAllowed)
    }

    @Test fun linkLostDropsThePhoneAndBlocksReconnects() {
        lifecycle.onForegroundChanged(Foreground.CARPLAY)
        lifecycle.onLinkUp()
        phone.connected = true
        lifecycle.onLinkLost()
        assertEquals(1, phone.stops)
        assertFalse(phone.session)
        assertFalse(lifecycle.phoneConnectionAllowed)
        assertFalse(lifecycle.linkUp)
    }

    @Test fun linkBackRestartsThePhone() {
        lifecycle.onForegroundChanged(Foreground.HOME)
        lifecycle.onLinkUp()
        lifecycle.onLinkLost()
        lifecycle.onLinkUp()
        assertEquals(2, phone.starts)
        assertTrue(lifecycle.phoneConnectionAllowed)
    }

    @Test fun manualConnectWhileSimHubIsDownIsAllowedWithAWarning() {
        lifecycle.onLinkUp()
        lifecycle.onLinkLost()
        assertTrue(lifecycle.onManualConnect(paired = true))
        assertTrue(lifecycle.phoneConnectionAllowed)
    }

    @Test fun manualConnectNeedsNoWarningWhenUpOrUnpaired() {
        assertFalse(lifecycle.onManualConnect(paired = false))
        lifecycle.onLinkUp()
        assertFalse(lifecycle.onManualConnect(paired = true))
    }

    @Test fun unpairingLiftsTheGuard() {
        lifecycle.onLinkUp()
        lifecycle.onLinkLost()
        lifecycle.onUnpaired()
        assertTrue(lifecycle.phoneConnectionAllowed)
    }

    @Test fun statusFollowsPhoneAndScreen() {
        lifecycle.onForegroundChanged(Foreground.HOME)
        assertEquals(SimHubMessage.Status(false, null, Screen.IDLE, null), link.statuses.last())
        phone.session = true; phone.connected = true
        lifecycle.onForegroundChanged(Foreground.CARPLAY)
        assertEquals(SimHubMessage.Status(true, "Tim's iPhone", Screen.CARPLAY, null), link.statuses.last())
        lifecycle.onForegroundChanged(Foreground.DASHBOARD)
        assertEquals(Screen.DASHBOARD, link.statuses.last().screen)
        lifecycle.onForegroundChanged(Foreground.NONE)
        assertEquals(Screen.OFF, link.statuses.last().screen)
    }

    @Test fun projectionWithoutAPhoneIsIdle() {
        lifecycle.onForegroundChanged(Foreground.CARPLAY)
        assertEquals(Screen.IDLE, link.statuses.last().screen)
    }

    @Test fun unchangedStatusIsNotResent() {
        lifecycle.onForegroundChanged(Foreground.HOME)
        val count = link.statuses.size
        lifecycle.onPhoneChanged()
        lifecycle.publishStatus()
        assertEquals(count, link.statuses.size)
        lifecycle.publishStatus(force = true)
        assertEquals(count + 1, link.statuses.size)
    }

    @Test fun nowPlayingIsSentOnlyWithAPhone() {
        lifecycle.updateNowPlaying(song)
        assertNull(link.statuses.last().nowPlaying)
        phone.connected = true
        lifecycle.onPhoneChanged()
        assertEquals(song, link.statuses.last().nowPlaying)
        lifecycle.updateNowPlaying(song.copy(position = 120.0))
        assertEquals(120.0, link.statuses.last().nowPlaying!!.position, 0.0)
    }

    @Test fun navIsSentOnlyWithAPhoneAndClears() {
        val turn = NavStatus(maneuver = "leftTurn", distanceM = 200, road = "B258", etaEpochS = 1_791_044_100L)
        lifecycle.updateNav(turn)
        assertNull(link.statuses.last().nav)
        phone.connected = true
        lifecycle.onPhoneChanged()
        assertEquals(turn, link.statuses.last().nav)
        lifecycle.updateNav(turn.copy(distanceM = 150))
        assertEquals(150, link.statuses.last().nav!!.distanceM)
        lifecycle.updateNav(null)
        assertNull(link.statuses.last().nav)
    }

    @Test fun showCommandsOpenTheScreens() {
        lifecycle.onCommand(SimHubCommand.ShowDashboard)
        assertEquals(1, screens.dashboards)
        phone.session = true
        lifecycle.onCommand(SimHubCommand.ShowCarPlay)
        assertEquals(1, screens.carPlays)
    }

    @Test fun showCarPlayWithoutAPhoneIsUnavailable() {
        lifecycle.onCommand(SimHubCommand.ShowCarPlay)
        assertEquals(0, screens.carPlays)
        assertEquals(1, link.unavailable.size)
    }

    @Test fun mediaCommandsGoToTheHandlerOrAreUnavailable() {
        lifecycle.onCommand(SimHubCommand.Media(MediaAction.NEXT))
        assertEquals(1, link.unavailable.size)
        val received = mutableListOf<SimHubCommand.Media>()
        lifecycle.mediaCommandHandler = { received += it }
        lifecycle.onCommand(SimHubCommand.Media(MediaAction.PLAY_PAUSE))
        assertEquals(listOf(SimHubCommand.Media(MediaAction.PLAY_PAUSE)), received)
        assertEquals(1, link.unavailable.size)
    }

    // --- screen policy (#39) ---------------------------------------------------------------------

    /** PC comes up while the rigPlay idle screen is shown: the phone starts, then its screen is covered. */
    private fun pcComesUp(from: Foreground = Foreground.OFFLINE_IDLE) {
        rig.onForegroundChanged(from)
        rig.onLinkUp()
        rig.onForegroundChanged(Foreground.CARPLAY) // CarPlayHostActivity resumed to start the phone
        rig.onPhoneChanged() // …and stored its session: waiting for the iPhone
    }

    /** PC up, no phone, the idle dashboard in front. */
    private fun idleOnDashboard() {
        pcComesUp()
        rig.onForegroundChanged(Foreground.IDLE_DASHBOARD)
        screens.shown.clear()
    }

    @Test fun pcOnWithoutPhoneShowsTheIdleDashboardOnceThePhoneWaits() {
        rig.onForegroundChanged(Foreground.OFFLINE_IDLE)
        rig.onLinkUp()
        assertEquals(1, phone.starts)
        // CarPlayHostActivity must lay out once to start the session: nothing covers it yet.
        assertTrue(screens.shown.isEmpty())
        rig.onForegroundChanged(Foreground.CARPLAY)
        assertTrue(screens.shown.isEmpty())
        rig.onPhoneChanged()
        assertEquals(listOf("idleDashboard"), screens.shown)
        rig.onForegroundChanged(Foreground.IDLE_DASHBOARD)
        assertEquals(SimHubMessage.Status(false, null, Screen.IDLE, null), link.statuses.last())
        assertEquals(RigSessionLifecycle.PolicyScreen.IDLE_DASHBOARD, rig.policyScreen())
    }

    @Test fun noDashboardToShowMeansTheRigPlayScreenUntilAStateBringsOne() {
        idle.available = false
        pcComesUp()
        assertEquals(listOf("offlineIdle"), screens.shown)
        rig.onForegroundChanged(Foreground.OFFLINE_IDLE)
        assertEquals(Screen.IDLE, link.statuses.last().screen)
        idle.available = true
        rig.onIdleInputsChanged()
        assertEquals(listOf("offlineIdle", "idleDashboard"), screens.shown)
    }

    @Test fun rigPlayScreenSettingWinsOverTheDashboard() {
        idle.mode = IdleMode.RIGPLAY_SCREEN
        pcComesUp()
        assertEquals(listOf("offlineIdle"), screens.shown)
        rig.onForegroundChanged(Foreground.OFFLINE_IDLE)
        idle.mode = IdleMode.DASHBOARD
        rig.onIdleInputsChanged()
        assertEquals(listOf("offlineIdle", "idleDashboard"), screens.shown)
    }

    @Test fun pcOffShowsTheRigPlayScreenAndPcOnTheIdleDashboardAgain() {
        idleOnDashboard()
        rig.onLinkLost()
        assertEquals(1, phone.stops)
        assertEquals(listOf("offlineIdle"), screens.shown)
        rig.onPhoneChanged() // the waiting session is gone
        rig.onForegroundChanged(Foreground.OFFLINE_IDLE)
        assertEquals(listOf("offlineIdle"), screens.shown)
        pcComesUp()
        assertEquals(2, phone.starts)
        assertEquals(listOf("offlineIdle", "idleDashboard"), screens.shown)
    }

    @Test fun phoneConnectingWhileIdleBringsCarPlayAndLeavingReturnsToIdle() {
        idleOnDashboard()
        phone.connected = true
        rig.onPhoneChanged()
        assertEquals(listOf("carplay"), screens.shown)
        rig.onForegroundChanged(Foreground.CARPLAY)
        assertEquals(SimHubMessage.Status(true, "Tim's iPhone", Screen.CARPLAY, null), link.statuses.last())
        phone.connected = false
        rig.onPhoneChanged()
        assertEquals(listOf("carplay", "idleDashboard"), screens.shown)
    }

    @Test fun phoneConnectingOnTheRigPlayScreenBringsCarPlay() {
        idle.available = false
        pcComesUp()
        rig.onForegroundChanged(Foreground.OFFLINE_IDLE)
        screens.shown.clear()
        phone.connected = true
        rig.onPhoneChanged()
        assertEquals(listOf("carplay"), screens.shown)
    }

    @Test fun liveCarPlayIsNeverCovered() {
        idleOnDashboard()
        phone.connected = true
        rig.onPhoneChanged()
        rig.onForegroundChanged(Foreground.CARPLAY)
        idle.available = false
        rig.onIdleInputsChanged()
        idle.mode = IdleMode.RIGPLAY_SCREEN
        rig.onIdleInputsChanged()
        rig.onLinkUp()
        assertEquals(listOf("carplay"), screens.shown)
    }

    @Test fun pcOffDuringCarPlayLeavesCarPlayWithoutWaitingForTheStop() {
        idleOnDashboard()
        phone.connected = true
        rig.onPhoneChanged()
        rig.onForegroundChanged(Foreground.CARPLAY)
        phone.stopsAtOnce = false
        rig.onLinkLost()
        assertTrue(phone.connected) // still tearing down
        assertEquals(listOf("carplay", "offlineIdle"), screens.shown)
        phone.finishStop()
        rig.onPhoneChanged()
        assertEquals(listOf("carplay", "offlineIdle"), screens.shown)
    }

    /** Policy changes alone never replace HOME; only inactivity does (#53). */
    @Test fun homeIsNotTakenAwayByAChange() {
        rig.onForegroundChanged(Foreground.HOME)
        phone.blocker = "no iPhone chosen"
        rig.onLinkUp()
        idle.available = false
        rig.onIdleInputsChanged()
        rig.onLinkLost()
        assertTrue(screens.shown.isEmpty())
    }

    @Test fun aDashboardOpenedByHandStays() {
        idleOnDashboard()
        rig.onCommand(SimHubCommand.ShowDashboard)
        assertEquals(listOf("dashboard"), screens.shown)
        rig.onForegroundChanged(Foreground.DASHBOARD)
        assertEquals(Screen.DASHBOARD, link.statuses.last().screen)
        rig.onLinkLost()
        assertEquals(listOf("dashboard"), screens.shown)
    }

    @Test fun changesInTheBackgroundWaitForARigPlayScreen() {
        idleOnDashboard()
        rig.onForegroundChanged(Foreground.NONE) // display off
        rig.onLinkLost()
        assertTrue(screens.shown.isEmpty())
        rig.onForegroundChanged(Foreground.IDLE_DASHBOARD) // display back on
        assertEquals(listOf("offlineIdle"), screens.shown)
    }

    @Test fun aChangeOwedInTheBackgroundIsDroppedWhenTheUserOpensHome() {
        idleOnDashboard()
        rig.onForegroundChanged(Foreground.NONE)
        rig.onLinkLost()
        rig.onForegroundChanged(Foreground.HOME)
        assertTrue(screens.shown.isEmpty())
    }

    @Test fun pcUpInTheBackgroundStartsThePhoneAndThenShowsIdle() {
        rig.onLinkUp()
        assertEquals(0, phone.starts)
        rig.onForegroundChanged(Foreground.OFFLINE_IDLE)
        assertEquals(1, phone.starts)
        rig.onForegroundChanged(Foreground.CARPLAY)
        assertTrue(screens.shown.isEmpty())
        rig.onPhoneChanged()
        assertEquals(listOf("idleDashboard"), screens.shown)
    }

    @Test fun leavingCarPlayBeforeThePhoneStartedReleasesThePolicy() {
        idle.available = false
        rig.onForegroundChanged(Foreground.OFFLINE_IDLE)
        rig.onLinkUp()
        rig.onForegroundChanged(Foreground.CARPLAY)
        rig.onForegroundChanged(Foreground.HOME) // e.g. Home from a permission prompt
        assertTrue(screens.shown.isEmpty())
        rig.onForegroundChanged(Foreground.OFFLINE_IDLE)
        idle.available = true
        rig.onIdleInputsChanged()
        assertEquals(listOf("idleDashboard"), screens.shown)
    }

    @Test fun commandsStillWorkWhileIdle() {
        idleOnDashboard()
        rig.onCommand(SimHubCommand.ShowCarPlay) // the session waits for the iPhone
        assertEquals(listOf("carplay"), screens.shown)
        rig.onForegroundChanged(Foreground.CARPLAY)
        rig.onCommand(SimHubCommand.ShowDashboard)
        assertEquals(listOf("carplay", "dashboard"), screens.shown)
        rig.onLinkLost()
        rig.onPhoneChanged()
        rig.onCommand(SimHubCommand.ShowCarPlay)
        assertEquals(1, link.unavailable.size)
    }

    @Test fun withoutAPairedPcThereIsNoPolicy() {
        idle.paired = false
        rig.onForegroundChanged(Foreground.CARPLAY)
        phone.session = true
        phone.connected = true
        rig.onPhoneChanged()
        phone.connected = false
        rig.onPhoneChanged()
        assertTrue(screens.shown.isEmpty())
        assertNull(rig.policyScreen())
        assertFalse(rig.showPolicyScreen())
    }

    @Test fun unpairingOnAnIdleScreenGoesHome() {
        idleOnDashboard()
        idle.paired = false
        rig.onUnpaired()
        assertEquals(listOf("home"), screens.shown)
    }

    @Test fun showPolicyScreenOpensWhatThePolicyWantsFromAnywhere() {
        rig.onForegroundChanged(Foreground.HOME)
        phone.blocker = "no iPhone chosen"
        rig.onLinkUp()
        assertTrue(rig.showPolicyScreen())
        assertEquals(listOf("idleDashboard"), screens.shown)
        rig.onForegroundChanged(Foreground.DASHBOARD)
        idle.available = false
        rig.onIdleInputsChanged() // the user is on the dashboard: no switch
        assertEquals(listOf("idleDashboard"), screens.shown)
        assertTrue(rig.showPolicyScreen())
        assertEquals(listOf("idleDashboard", "offlineIdle"), screens.shown)
    }

    // --- inactivity (#53) ------------------------------------------------------------------------

    /** The user on HOME with the PC off (paired, link down): the policy wants the rigPlay idle screen. */
    private fun onHomeWithThePcOff() {
        rig.onForegroundChanged(Foreground.HOME)
        rig.onIdleInputsChanged()
        assertEquals(RigSessionLifecycle.PolicyScreen.OFFLINE_IDLE, rig.policyScreen())
    }

    /** PC on, no phone (none chosen), the user on HOME. */
    private fun onHomeWithThePcOn() {
        rig.onForegroundChanged(Foreground.HOME)
        phone.blocker = "no iPhone chosen"
        rig.onLinkUp()
        assertEquals(0, phone.starts)
    }

    @Test fun idleScreenTakesOverHomeAfterTheTimeoutWithoutTouches() {
        onHomeWithThePcOff()
        advance(minutes(3) - 1)
        assertTrue(screens.shown.isEmpty())
        advance(1)
        assertEquals(listOf("offlineIdle"), screens.shown)
        assertEquals(Foreground.HOME, rig.idleTakenOverFrom)
        rig.onForegroundChanged(Foreground.OFFLINE_IDLE)
        advance(minutes(30))
        assertEquals(listOf("offlineIdle"), screens.shown)
    }

    @Test fun touchesResetTheTimeout() {
        onHomeWithThePcOff()
        advance(minutes(2))
        userActivity.onUserInteraction()
        advance(minutes(2))
        userActivity.onUserInteraction()
        advance(minutes(3) - 1)
        assertTrue(screens.shown.isEmpty())
        advance(1)
        assertEquals(listOf("offlineIdle"), screens.shown)
    }

    @Test fun thePcGoingAwayStartsTheTimeoutAfresh() {
        onHomeWithThePcOn()
        advance(minutes(10)) // the idle dashboard is the target: HOME is not timed
        assertTrue(screens.shown.isEmpty())
        rig.onLinkLost()
        assertTrue(screens.shown.isEmpty()) // HOME stays as it is when the PC goes away…
        advance(minutes(3) - 1)
        assertTrue(screens.shown.isEmpty())
        advance(1) // …until nobody touched it for 3 minutes
        assertEquals(listOf("offlineIdle"), screens.shown)
    }

    @Test fun pcOnWithTheRigPlayScreenChosenAlsoGoesIdleAfterTheTimeout() {
        idle.mode = IdleMode.RIGPLAY_SCREEN
        onHomeWithThePcOn()
        advance(minutes(3))
        assertEquals(listOf("offlineIdle"), screens.shown)
    }

    @Test fun pcOnWithTheIdleDashboardChosenLeavesHomeAlone() {
        onHomeWithThePcOn()
        advance(minutes(60))
        assertTrue(screens.shown.isEmpty())
        assertNull(timer.dueAt)
    }

    @Test fun immediatelyTakesOverWhenThePcGoesAwayAndOnlyThen() {
        idle.idleAfter = 0
        onHomeWithThePcOn()
        rig.onLinkLost()
        assertEquals(listOf("offlineIdle"), screens.shown)
        assertEquals(Foreground.HOME, rig.idleTakenOverFrom)
        rig.onForegroundChanged(Foreground.OFFLINE_IDLE)
        assertTrue(rig.returnFromIdle())
        rig.onForegroundChanged(Foreground.HOME)
        // Back on HOME (settings) with the PC still off: no timer, the user stays.
        advance(minutes(60))
        assertEquals(listOf("offlineIdle", "returnHome"), screens.shown)
        assertNull(timer.dueAt)
    }

    @Test fun immediatelyDoesNotYankTheUserOffHomeWithoutAChange() {
        idle.idleAfter = 0
        rig.onIdleInputsChanged() // the PC was already off when the user opened HOME
        rig.onForegroundChanged(Foreground.HOME)
        advance(minutes(60))
        assertTrue(screens.shown.isEmpty())
    }

    @Test fun aTapOnTheIdleScreenReturnsToWhereTheUserWas() {
        onHomeWithThePcOff()
        advance(minutes(3))
        rig.onForegroundChanged(Foreground.OFFLINE_IDLE)
        assertTrue(rig.returnFromIdle())
        assertEquals(listOf("offlineIdle", "returnHome"), screens.shown)
        assertNull(rig.idleTakenOverFrom)
        assertFalse(rig.returnFromIdle()) // only once
        rig.onForegroundChanged(Foreground.HOME)
        // Left alone again: the idle screen comes back after the timeout.
        advance(minutes(3))
        assertEquals(listOf("offlineIdle", "returnHome", "offlineIdle"), screens.shown)
    }

    @Test fun aTapOnAnIdleScreenThatCameByItselfIsTheToolbars() {
        idleOnDashboard()
        assertFalse(rig.returnFromIdle())
        rig.onLinkLost()
        rig.onPhoneChanged()
        rig.onForegroundChanged(Foreground.OFFLINE_IDLE)
        assertFalse(rig.returnFromIdle())
        assertEquals(listOf("offlineIdle"), screens.shown)
    }

    @Test fun theWayBackSurvivesThePcComingUpButNotThePhone() {
        onHomeWithThePcOff()
        advance(minutes(3))
        rig.onForegroundChanged(Foreground.OFFLINE_IDLE)
        pcComesUp() // the phone starts behind the idle screen, then the idle dashboard
        assertEquals(listOf("offlineIdle", "idleDashboard"), screens.shown)
        rig.onForegroundChanged(Foreground.IDLE_DASHBOARD)
        assertEquals(Foreground.HOME, rig.idleTakenOverFrom)
        phone.connected = true
        rig.onPhoneChanged()
        assertNull(rig.idleTakenOverFrom)
        rig.onForegroundChanged(Foreground.CARPLAY)
        phone.connected = false
        rig.onPhoneChanged()
        rig.onForegroundChanged(Foreground.IDLE_DASHBOARD)
        assertFalse(rig.returnFromIdle())
    }

    @Test fun aTapOnTheIdleDashboardAlsoReturns() {
        onHomeWithThePcOff()
        advance(minutes(3))
        rig.onForegroundChanged(Foreground.OFFLINE_IDLE)
        pcComesUp()
        rig.onForegroundChanged(Foreground.IDLE_DASHBOARD)
        assertTrue(rig.returnFromIdle())
        assertEquals(listOf("offlineIdle", "idleDashboard", "returnHome"), screens.shown)
    }

    @Test fun noTimeoutWhileAPhoneIsConnected() {
        rig.onForegroundChanged(Foreground.HOME)
        phone.session = true
        phone.connected = true
        rig.onPhoneChanged()
        advance(minutes(60))
        assertTrue(screens.shown.isEmpty())
        assertNull(timer.dueAt)
    }

    @Test fun noTimeoutOnADashboardOrCarPlay() {
        rig.onForegroundChanged(Foreground.DASHBOARD)
        rig.onIdleInputsChanged()
        advance(minutes(60))
        assertTrue(screens.shown.isEmpty())
        rig.onForegroundChanged(Foreground.HOME)
        advance(minutes(2))
        rig.onForegroundChanged(Foreground.DASHBOARD) // opened by hand before the timeout
        advance(minutes(60))
        assertTrue(screens.shown.isEmpty())
        assertNull(timer.dueAt)
    }

    @Test fun noTimeoutInTheBackground() {
        onHomeWithThePcOff()
        rig.onForegroundChanged(Foreground.NONE)
        advance(minutes(60))
        assertTrue(screens.shown.isEmpty())
    }

    @Test fun noTimeoutWithoutAPairedPc() {
        idle.paired = false
        rig.onForegroundChanged(Foreground.HOME)
        rig.onIdleInputsChanged()
        advance(minutes(60))
        assertTrue(screens.shown.isEmpty())
    }

    @Test fun aSettingChangeAppliesLive() {
        idle.idleAfter = 10
        onHomeWithThePcOff()
        advance(minutes(2))
        idle.idleAfter = 1
        rig.onIdleInputsChanged()
        advance(0) // already 2 minutes without a touch
        assertEquals(listOf("offlineIdle"), screens.shown)
    }

    @Test fun aLongerSettingPostponesAndImmediatelyStopsTheTimer() {
        onHomeWithThePcOff()
        advance(minutes(2))
        idle.idleAfter = 5
        rig.onIdleInputsChanged()
        advance(minutes(2))
        assertTrue(screens.shown.isEmpty())
        idle.idleAfter = 0
        rig.onIdleInputsChanged()
        advance(minutes(60))
        assertTrue(screens.shown.isEmpty())
        idle.idleAfter = 3
        rig.onIdleInputsChanged()
        advance(0)
        assertEquals(listOf("offlineIdle"), screens.shown)
    }
}

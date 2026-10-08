package com.shilapi.xcertplay

import com.shilapi.xcertplay.DashboardWebViewHolder.Page
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Test

class DashboardWebViewHolderTest {
    private class FakeSurface : DashboardWebViewHolder.Surface {
        val loads = mutableListOf<String>()
        var clears = 0
        var visible: Boolean? = null
        var destroyed = false
        override fun load(url: String) { loads += url }
        override fun clear() { clears++ }
        override fun setVisible(visible: Boolean) { this.visible = visible }
        override fun destroy() { destroyed = true }
    }

    private class FakeScheduler : DashboardWebViewHolder.Scheduler {
        val tasks = mutableListOf<Pair<Long, () -> Unit>>()
        var cancelled = 0
        override fun schedule(delayMs: Long, task: () -> Unit): DashboardWebViewHolder.Cancellable {
            val entry = delayMs to task
            tasks += entry
            return DashboardWebViewHolder.Cancellable { if (tasks.remove(entry)) cancelled++ }
        }
        fun runAll() = tasks.toList().also { tasks.clear() }.forEach { it.second() }
    }

    private val surfaces = mutableListOf<FakeSurface>()
    private val scheduler = FakeScheduler()
    private val probes = mutableListOf<Triple<String, Int, (Boolean) -> Unit>>()
    private val holder = DashboardWebViewHolder(
        createSurface = { FakeSurface().also { surfaces += it } },
        scheduler = scheduler,
        prober = { host, port, result -> probes += Triple(host, port, result) },
    )

    private val pit = DashboardContent.Load("http://192.168.1.20:8888/Dash#Pit|nocontrols", null)
    private val clock = DashboardContent.Load("http://192.168.1.20:8888/Dash#Clock|nocontrols", null)
    private val natted = DashboardContent.Load("http://172.30.0.2:8888/Dash#Pit|nocontrols", "http://127.0.0.1:8888/Dash#Pit|nocontrols")
    private val surface: FakeSurface get() = surfaces.single()

    @Test fun nothingIsCreatedWithoutADashboard() {
        holder.update(DashboardContent.NoDashboard)
        holder.update(DashboardContent.Disconnected)
        assertTrue(surfaces.isEmpty())
        assertNull(holder.attach(this))
        assertTrue(scheduler.tasks.isEmpty())
    }

    @Test fun firstDashboardIsLoadedInTheBackground() {
        holder.update(pit)
        assertEquals(listOf(pit.url), surface.loads)
        assertEquals(false, surface.visible)
        assertEquals(Page.Loading(pit.url), holder.page)
        assertFalse(holder.attached)
    }

    @Test fun sameUrlIsNotReloaded() {
        holder.update(pit)
        holder.onPageFinished(pit.url)
        holder.update(pit)
        holder.update(pit.copy())
        assertSame(surface, holder.attach(this))
        assertEquals(listOf(pit.url), surface.loads)
        assertEquals(Page.Loaded(pit.url), holder.page)
    }

    @Test fun urlChangeReloadsInTheSameSurface() {
        holder.update(pit)
        holder.onPageFinished(pit.url)
        holder.update(clock)
        assertEquals(listOf(pit.url, clock.url), surface.loads)
        assertEquals(Page.Loading(clock.url), holder.page)
    }

    @Test fun pageSurvivesALinkDropAndIsNotReloadedOnReturn() {
        holder.update(pit)
        holder.onPageFinished(pit.url)
        holder.update(DashboardContent.Disconnected)
        holder.update(pit)
        assertEquals(listOf(pit.url), surface.loads)
        assertEquals(0, surface.clears)
        assertEquals(1, scheduler.cancelled)
    }

    @Test fun serverOffBlanksAndTheNextDashboardLoadsFresh() {
        holder.update(pit)
        holder.onPageFinished(pit.url)
        holder.update(DashboardContent.ServerOff)
        assertEquals(1, surface.clears)
        assertEquals(Page.Idle, holder.page)
        holder.update(pit)
        assertEquals(listOf(pit.url, pit.url), surface.loads)
    }

    @Test fun failedPageIsReloadedOnTheNextStateOrRetry() {
        holder.update(pit)
        holder.onMainFrameError(pit.url, "-6 net::ERR_CONNECTION_REFUSED")
        assertEquals(Page.Failed(pit.url, "-6 net::ERR_CONNECTION_REFUSED"), holder.page)
        holder.onPageFinished(pit.url) // the error page
        assertTrue(holder.page is Page.Failed)
        holder.update(pit)
        assertEquals(2, surface.loads.size)
        holder.retry()
        assertEquals(3, surface.loads.size)
    }

    @Test fun unreachableHostSwitchesToTheConnectedHost() {
        holder.update(natted)
        val (host, port, result) = probes.single()
        assertEquals("172.30.0.2" to 8888, host to port)
        result(false)
        assertEquals(listOf(natted.url, natted.fallbackUrl), surface.loads)
        // The abandoned URL's late finish does not count; the fallback's does.
        holder.onPageFinished(natted.url)
        assertEquals(Page.Loading(natted.fallbackUrl!!), holder.page)
        holder.onPageFinished(natted.fallbackUrl!!)
        assertEquals(Page.Loaded(natted.fallbackUrl!!), holder.page)
        // Same content again: no reload.
        holder.update(natted)
        assertEquals(2, surface.loads.size)
    }

    @Test fun mainFrameErrorSwitchesToTheConnectedHostThenFails() {
        holder.update(natted)
        holder.onMainFrameError(natted.url, "timeout")
        assertEquals(natted.fallbackUrl, surface.loads.last())
        holder.onMainFrameError(natted.fallbackUrl!!, "refused")
        assertEquals(Page.Failed(natted.fallbackUrl!!, "refused"), holder.page)
    }

    @Test fun reachableHostOrALoadedPageKeepsTheUrlAsGiven() {
        holder.update(natted)
        probes.single().third(true)
        assertEquals(listOf(natted.url), surface.loads)
        holder.update(clock)
        holder.update(natted)
        holder.onPageFinished(natted.url)
        probes.last().third(false) // too late: the page answered
        assertEquals(natted.url, surface.loads.last())
    }

    @Test fun attachAndDetachBookkeeping() {
        holder.update(pit)
        val first = Any()
        val second = Any()
        assertSame(surface, holder.attach(first))
        holder.setVisible(first, true)
        assertEquals(true, surface.visible)
        // A second screen takes over; the first one's late calls change nothing.
        holder.attach(second)
        holder.setVisible(first, false)
        holder.detach(first)
        assertSame(second, holder.owner)
        assertEquals(true, surface.visible)
        holder.detach(second)
        assertFalse(holder.attached)
        assertEquals(false, surface.visible)
        assertFalse(surface.destroyed)
        assertNull(holder.listener)
    }

    @Test fun listenerHearsLoads() {
        var calls = 0
        holder.listener = { calls++ }
        holder.update(pit)
        holder.onPageFinished(pit.url)
        assertEquals(2, calls)
    }

    @Test fun linkDownForTenMinutesReleasesThePage() {
        holder.update(pit)
        holder.update(DashboardContent.Disconnected)
        holder.update(DashboardContent.Disconnected)
        assertEquals(DashboardWebViewHolder.RELEASE_AFTER_MS, scheduler.tasks.single().first)
        assertEquals(10 * 60_000L, DashboardWebViewHolder.RELEASE_AFTER_MS)
        scheduler.runAll()
        assertTrue(surface.destroyed)
        assertNull(holder.surface)
        assertEquals(Page.Idle, holder.page)
        // Back: a new surface, loaded at once.
        holder.update(pit)
        assertEquals(2, surfaces.size)
        assertEquals(listOf(pit.url), surfaces[1].loads)
    }

    @Test fun linkBackCancelsTheRelease() {
        holder.update(pit)
        holder.update(DashboardContent.Disconnected)
        holder.update(pit)
        assertTrue(scheduler.tasks.isEmpty())
        assertFalse(surface.destroyed)
    }

    @Test fun releaseWhileShownHandsTheNextSurfaceToTheScreen() {
        holder.update(pit)
        val screen = Any()
        holder.attach(screen)
        holder.setVisible(screen, true)
        holder.update(DashboardContent.Disconnected)
        scheduler.runAll()
        holder.update(pit)
        assertEquals(true, surfaces[1].visible)
        assertSame(surfaces[1], holder.attach(screen))
    }

    @Test fun trimMemoryReleasesOnlyWhenNotShownAndOnlyWhenSevere() {
        holder.update(pit)
        holder.attach(this)
        holder.onTrimMemory(80)
        assertFalse(surface.destroyed)
        holder.detach(this)
        holder.onTrimMemory(10)
        holder.onTrimMemory(20)
        holder.onTrimMemory(40)
        assertFalse(surface.destroyed)
        holder.onTrimMemory(15)
        assertTrue(surface.destroyed)
    }

    @Test fun trimLevels() {
        assertTrue(DashboardWebViewHolder.shouldReleaseOnTrim(15))
        assertTrue(DashboardWebViewHolder.shouldReleaseOnTrim(80))
        assertFalse(DashboardWebViewHolder.shouldReleaseOnTrim(5))
        assertFalse(DashboardWebViewHolder.shouldReleaseOnTrim(60))
    }

    @Test fun attachAfterAReleaseRecreatesThePage() {
        holder.update(pit)
        holder.onTrimMemory(80)
        assertTrue(surface.destroyed)
        val reopened = holder.attach(this)
        assertNotNull(reopened)
        assertEquals(listOf(pit.url), surfaces[1].loads)
    }

    @Test fun unpairingBlanksAndReleasesLater() {
        holder.update(pit)
        holder.update(DashboardContent.NotPaired)
        assertEquals(1, surface.clears)
        assertEquals(1, scheduler.tasks.size)
    }

    // --- the idle dashboard (#39) in the same page ---

    @Test fun mainDashboardIsWarmedNotTheIdleOne() {
        holder.update(pit, clock)
        assertEquals(listOf(pit.url), surface.loads)
        assertFalse(holder.idle)
    }

    @Test fun idleScreenLoadsTheIdleDashboardAndClosingRewarmsTheMainOne() {
        holder.update(pit, clock)
        holder.onPageFinished(pit.url)
        val screen = Any()
        assertSame(surface, holder.attach(screen, idle = true))
        assertTrue(holder.idle)
        assertEquals(clock, holder.content)
        assertEquals(Page.Loading(clock.url), holder.page)
        holder.update(pit, clock)
        assertEquals(listOf(pit.url, clock.url), surface.loads)
        holder.detach(screen)
        assertFalse(holder.idle)
        assertEquals(pit, holder.content)
        assertEquals(listOf(pit.url, clock.url, pit.url), surface.loads)
    }

    @Test fun sameUrlForBothDashboardsSwitchesWithoutALoad() {
        holder.update(pit)
        holder.onPageFinished(pit.url)
        val screen = Any()
        holder.attach(screen, idle = true)
        holder.setIdle(screen, false)
        holder.setIdle(screen, true)
        assertEquals(listOf(pit.url), surface.loads)
        assertEquals(Page.Loaded(pit.url), holder.page)
    }

    @Test fun onlyTheOwnerSwitchesTheDashboard() {
        holder.update(pit, clock)
        val screen = Any()
        holder.attach(screen)
        holder.setIdle(Any(), true)
        holder.detach(Any())
        assertFalse(holder.idle)
        holder.setIdle(screen, true)
        assertEquals(listOf(pit.url, clock.url), surface.loads)
    }

    @Test fun idleDashboardWithoutAMainOneCreatesThePageOnlyForTheIdleScreen() {
        holder.update(DashboardContent.NoDashboard, clock)
        assertTrue(surfaces.isEmpty())
        val screen = Any()
        assertNotNull(holder.attach(screen, idle = true))
        assertEquals(listOf(clock.url), surface.loads)
        holder.detach(screen)
        assertEquals(DashboardContent.NoDashboard, holder.content)
        assertEquals(1, surface.clears)
    }

    @Test fun retryReloadsTheIdleDashboardWhileShown() {
        holder.update(pit, clock)
        val screen = Any()
        holder.attach(screen, idle = true)
        holder.onMainFrameError(clock.url, "-6 refused")
        assertEquals(Page.Failed(clock.url, "-6 refused"), holder.page)
        holder.retry()
        assertEquals(listOf(pit.url, clock.url, clock.url), surface.loads)
    }
}

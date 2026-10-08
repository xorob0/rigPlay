package com.shilapi.xcertplay

import com.shilapi.xcertplay.simhub.SimHubState
import java.net.URI

/** What the SimHub dashboard screen (#30) shows for a link state. */
sealed class DashboardContent {
    /** Load [url]; [fallbackUrl] is the same URL on the host the link is connected to, when different. */
    data class Load(val url: String, val fallbackUrl: String?) : DashboardContent()

    /** `state.dashboardUrl` is `null`: nothing selected on the PC (§6.8). */
    object NoDashboard : DashboardContent() {
        override fun toString() = "NoDashboard"
    }

    /** `state.dashboardServer.reachable == false`: SimHub's web dash server is off (§6.6). */
    object ServerOff : DashboardContent() {
        override fun toString() = "ServerOff"
    }

    /** Paired, but the link is down. */
    object Disconnected : DashboardContent() {
        override fun toString() = "Disconnected"
    }

    object NotPaired : DashboardContent() {
        override fun toString() = "NotPaired"
    }

    companion object {
        fun resolve(state: SimHubState, paired: Boolean): DashboardContent {
            if (!paired) return NotPaired
            if (!state.paired) return Disconnected
            if (!state.dashboardServerReachable) return ServerOff
            // SimHub's own toolbar off (#50): the dashboard fills the screen with no tap.
            val url = SimHubDashPage.withoutChrome(state.dashboardUrl ?: return NoDashboard)
            return Load(url, state.host?.let { DashboardUrls.withHost(url, it) })
        }

        /**
         * The idle dashboard (#39): `state.idleDashboardUrl`, else the main `state.dashboardUrl`. Anything
         * but [Load] means the rigPlay idle screen is shown instead ([OfflineIdleActivity]).
         */
        fun resolveIdle(state: SimHubState, paired: Boolean): DashboardContent {
            val url = state.idleDashboardUrl ?: state.dashboardUrl
            return resolve(state.copy(dashboardUrl = url), paired)
        }

        /** True when [resolveIdle] has a URL to load. */
        fun idleDashboardAvailable(state: SimHubState, paired: Boolean): Boolean = resolveIdle(state, paired) is Load
    }
}

object DashboardUrls {
    /**
     * [url] with its host replaced by [host], keeping scheme, port, path and query; `null` when the
     * host is already [host] or [url] cannot be parsed. The plugin builds dashboard URLs from the
     * PC's local address of the TCP connection (§11), which is wrong behind NAT (a VM, a port
     * forward): the address the tablet connected to is then the one that works.
     */
    fun withHost(url: String, host: String): String? {
        val uri = parse(url) ?: return null
        val current = uri.host ?: return null
        val bare = host.removePrefix("[").removeSuffix("]")
        if (current.removePrefix("[").removeSuffix("]").equals(bare, ignoreCase = true)) return null
        val authorityHost = if (bare.contains(':')) "[$bare]" else bare
        val port = if (uri.port >= 0) ":${uri.port}" else ""
        val userInfo = uri.rawUserInfo?.let { "$it@" } ?: ""
        val rest = buildString {
            append(uri.rawPath ?: "")
            uri.rawQuery?.let { append('?').append(it) }
            if ('#' in url) append('#').append(url.substringAfter('#'))
        }
        return "${uri.scheme}://$userInfo$authorityHost$port$rest"
    }

    /** Host and port to probe for [url] (default 80 for http). */
    fun endpoint(url: String): Pair<String, Int>? {
        val uri = parse(url) ?: return null
        val host = uri.host?.removePrefix("[")?.removeSuffix("]") ?: return null
        val port = if (uri.port >= 0) uri.port else if (uri.scheme.equals("https", true)) 443 else 80
        return host to port
    }

    /**
     * [url] without its fragment, which is kept verbatim by the callers: SimHub's `|` flags (#50)
     * are not legal in a java.net.URI fragment.
     */
    private fun parse(url: String): URI? = runCatching { URI(url.substringBefore('#')) }.getOrNull()
}

package com.shilapi.xcertplay

import com.shilapi.xcertplay.simhub.DiscoveredHost
import com.shilapi.xcertplay.simhub.SimHubLink
import com.shilapi.xcertplay.simhub.SimHubMessage
import com.shilapi.xcertplay.simhub.SimHubState

/**
 * The part of [SimHubLink] the onboarding flow and the session coordinator use. Tests substitute a
 * fake; the app wraps the real link with [of].
 */
interface SimHubLinkPort {
    val state: SimHubState
    val currentTarget: SimHubLink.Target?
    fun start(target: SimHubLink.Target)
    fun stop()
    fun requestPairing()
    fun pair(pin: String): Boolean
    fun reconnectNow()
    fun onBeacon(host: DiscoveredHost)
    fun send(status: SimHubMessage.Status)
    fun sendCommandUnavailable(message: String?): Boolean

    companion object {
        fun of(link: SimHubLink): SimHubLinkPort = object : SimHubLinkPort {
            override val state: SimHubState get() = link.state
            override val currentTarget: SimHubLink.Target? get() = link.currentTarget
            override fun start(target: SimHubLink.Target) = link.start(target)
            override fun stop() = link.stop()
            override fun requestPairing() = link.requestPairing()
            override fun pair(pin: String): Boolean = link.pair(pin)
            override fun reconnectNow() = link.reconnectNow()
            override fun onBeacon(host: DiscoveredHost) = link.onBeacon(host)
            override fun send(status: SimHubMessage.Status) = link.send(status)
            override fun sendCommandUnavailable(message: String?): Boolean = link.sendCommandUnavailable(message)
        }
    }
}

/** Parses what the user types in "Enter address manually": `host`, `host:port`, `[v6]` or `[v6]:port`. */
object SimHubAddress {
    data class Parsed(val host: String, val port: Int)

    fun parse(text: String, defaultPort: Int): Parsed? {
        val input = text.trim()
        if (input.isEmpty() || input.any { it.isWhitespace() || it == '/' }) return null
        if (input.startsWith("[")) {
            val close = input.indexOf(']')
            if (close <= 1) return null
            val host = input.substring(1, close)
            val rest = input.substring(close + 1)
            return when {
                rest.isEmpty() -> Parsed(host, defaultPort)
                rest.startsWith(":") -> port(rest.substring(1))?.let { Parsed(host, it) }
                else -> null
            }
        }
        val colons = input.count { it == ':' }
        return when (colons) {
            0 -> Parsed(input, defaultPort)
            1 -> {
                val host = input.substringBefore(':')
                if (host.isEmpty()) null else port(input.substringAfter(':'))?.let { Parsed(host, it) }
            }
            // A bare IPv6 address without brackets: no port.
            else -> Parsed(input, defaultPort)
        }
    }

    private fun port(text: String): Int? = text.toIntOrNull()?.takeIf { it in 1..65535 }
}

package com.shilapi.xcertplay

import com.shilapi.xcertplay.simhub.DiscoveredHost
import com.shilapi.xcertplay.simhub.SimHubLink
import com.shilapi.xcertplay.simhub.SimHubMessage
import com.shilapi.xcertplay.simhub.SimHubState

/** Records what the code under test asks of the link. */
class FakeSimHubLinkPort : SimHubLinkPort {
    override var state: SimHubState = SimHubState.STOPPED
    override var currentTarget: SimHubLink.Target? = null
    val started = mutableListOf<SimHubLink.Target>()
    var stops = 0
    var pairingRequests = 0
    val pins = mutableListOf<String>()
    var pairAccepted = true
    var reconnects = 0
    val beacons = mutableListOf<DiscoveredHost>()
    val statuses = mutableListOf<SimHubMessage.Status>()
    val unavailable = mutableListOf<String?>()

    override fun start(target: SimHubLink.Target) {
        started += target
        currentTarget = target
        state = SimHubState(phase = SimHubState.Phase.CONNECTING, host = target.host, controlPort = target.port)
    }

    override fun stop() {
        stops++
        state = SimHubState.STOPPED
    }

    override fun requestPairing() { pairingRequests++ }

    override fun pair(pin: String): Boolean {
        pins += pin
        return pairAccepted
    }

    override fun reconnectNow() { reconnects++ }
    override fun onBeacon(host: DiscoveredHost) { beacons += host }
    override fun send(status: SimHubMessage.Status) { statuses += status }
    override fun sendCommandUnavailable(message: String?): Boolean {
        unavailable += message
        return true
    }
}

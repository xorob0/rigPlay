package com.shilapi.xcertplay

import com.shilapi.xcertplay.SimHubPairingFlow.PairingError
import com.shilapi.xcertplay.SimHubPairingFlow.Step
import com.shilapi.xcertplay.simhub.PairFailure
import com.shilapi.xcertplay.simhub.SimHubLink
import com.shilapi.xcertplay.simhub.SimHubMessage.PairResult
import com.shilapi.xcertplay.simhub.SimHubState
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class SimHubPairingFlowTest {
    private val link = FakeSimHubLinkPort()
    private val seen = mutableListOf<Step>()
    private val flow = SimHubPairingFlow(link) { seen += it }

    private fun unpaired(name: String = "RIG-PC") =
        SimHubState(phase = SimHubState.Phase.UNPAIRED, host = "192.168.1.20", controlPort = 23711, hostId = "h1", hostName = name)

    private fun toPinStep() {
        flow.connect("192.168.1.20", 23711, "RIG-PC")
        flow.onStateChanged(unpaired())
        flow.onPairResult(PairResult(ok = false, reason = PairFailure.PIN_REQUIRED, pinExpiresInSec = 120))
    }

    @Test fun connectAsksForPinBeforeStartingTheLink() {
        flow.connect("192.168.1.20", 23711, "RIG-PC")
        assertEquals(1, link.pairingRequests)
        assertEquals(listOf(SimHubLink.Target("192.168.1.20", 23711)), link.started)
        assertEquals(Step.Connecting("192.168.1.20", 23711, "RIG-PC"), flow.step)
        assertTrue(flow.active)
    }

    @Test fun welcomeMovesToWaitingForPinWithTheHostName() {
        flow.connect("192.168.1.20", 23711, null)
        flow.onStateChanged(unpaired("Garage PC"))
        assertEquals(Step.WaitingForPin("192.168.1.20", 23711, "Garage PC"), flow.step)
    }

    @Test fun pinRequiredShowsPinEntryWithExpiry() {
        toPinStep()
        assertEquals(Step.EnterPin("192.168.1.20", 23711, "RIG-PC", expiresInSec = 120), flow.step)
    }

    @Test fun submitPinSendsItAndMarksSubmitting() {
        toPinStep()
        assertTrue(flow.submitPin("482913"))
        assertEquals(listOf("482913"), link.pins)
        assertTrue((flow.step as Step.EnterPin).submitting)
        // No double submit while waiting for the answer.
        assertFalse(flow.submitPin("482913"))
        assertEquals(1, link.pins.size)
    }

    @Test fun malformedPinIsRejectedLocally() {
        toPinStep()
        assertFalse(flow.submitPin("12345"))
        assertFalse(flow.submitPin("12345a"))
        assertTrue(link.pins.isEmpty())
    }

    @Test fun pinIsNotSentWhenTheSessionIsNotUnpaired() {
        toPinStep()
        link.pairAccepted = false
        assertFalse(flow.submitPin("000000"))
        assertFalse((flow.step as Step.EnterPin).submitting)
    }

    @Test fun wrongPinKeepsTheStepWithAttemptsLeft() {
        toPinStep()
        flow.submitPin("111111")
        flow.onPairResult(PairResult(ok = false, reason = PairFailure.WRONG_PIN, attemptsLeft = 2))
        val step = flow.step as Step.EnterPin
        assertEquals(PairingError.WRONG_PIN, step.error)
        assertEquals(2, step.attemptsLeft)
        assertFalse(step.submitting)
        assertFalse(step.needsNewPin)
        assertEquals(120, step.expiresInSec)
        assertTrue(flow.submitPin("222222"))
    }

    @Test fun expiredTooManyAndDeniedNeedANewPin() {
        for ((reason, error) in listOf(
            PairFailure.PIN_EXPIRED to PairingError.PIN_EXPIRED,
            PairFailure.TOO_MANY_ATTEMPTS to PairingError.TOO_MANY_ATTEMPTS,
            PairFailure.DENIED to PairingError.DENIED,
        )) {
            toPinStep()
            flow.onPairResult(PairResult(ok = false, reason = reason))
            val step = flow.step as Step.EnterPin
            assertEquals(error, step.error)
            assertTrue(step.needsNewPin)
            assertFalse(flow.submitPin("123456"))
            val before = link.pairingRequests
            flow.requestNewPin()
            assertEquals(before + 1, link.pairingRequests)
            assertEquals(Step.WaitingForPin("192.168.1.20", 23711, "RIG-PC"), flow.step)
        }
    }

    @Test fun unreachableStopsTheLinkAndOffersRetry() {
        flow.connect("10.0.0.9", 23711, null)
        flow.onAttemptFailed(SimHubLink.LinkLoss(SimHubLink.LossReason.CONNECT_FAILED))
        assertEquals(Step.Failed("10.0.0.9", 23711, null, PairingError.UNREACHABLE), flow.step)
        assertEquals(2, link.stops) // one before connecting, one on failure
        assertFalse(flow.active)
        flow.retry()
        assertEquals(Step.Connecting("10.0.0.9", 23711, null), flow.step)
        assertEquals(2, link.started.size)
    }

    @Test fun handshakeTimeoutIsUnreachable() {
        flow.connect("10.0.0.9", 23711, "PC")
        flow.onAttemptFailed(SimHubLink.LinkLoss(SimHubLink.LossReason.HANDSHAKE_TIMEOUT))
        assertEquals(PairingError.UNREACHABLE, (flow.step as Step.Failed).error)
    }

    @Test fun incompatiblePluginFails() {
        flow.connect("10.0.0.9", 23711, "PC")
        flow.onAttemptFailed(SimHubLink.LinkLoss(SimHubLink.LossReason.INCOMPATIBLE))
        assertEquals(PairingError.INCOMPATIBLE, (flow.step as Step.Failed).error)
    }

    @Test fun droppedSessionDuringPinReconnectsInsteadOfFailing() {
        toPinStep()
        flow.onStateChanged(SimHubState(phase = SimHubState.Phase.WAITING, host = "192.168.1.20", controlPort = 23711))
        assertEquals(Step.Connecting("192.168.1.20", 23711, "RIG-PC"), flow.step)
        // A close by the peer is retried by the link; only connect failures are reported.
        flow.onAttemptFailed(SimHubLink.LinkLoss(SimHubLink.LossReason.CLOSED_BY_PEER))
        assertTrue(flow.step is Step.Connecting)
        flow.onStateChanged(unpaired())
        assertTrue(flow.step is Step.WaitingForPin)
    }

    @Test fun pairedBuildsThePairingFromTheLinkTarget() {
        toPinStep()
        flow.submitPin("482913")
        link.currentTarget = SimHubLink.Target("192.168.1.20", 23711, "h1", "tok")
        val pairing = flow.onPaired("h1", "tok", "RIG-PC")
        assertEquals(SimHubPairing("h1", "192.168.1.20", 23711, "RIG-PC", "tok"), pairing)
        assertEquals(Step.Paired(pairing!!), flow.step)
        assertFalse(flow.active)
    }

    @Test fun pairedIsIgnoredWithoutAnAttempt() {
        assertNull(flow.onPaired("h1", "tok", "RIG-PC"))
        assertEquals(Step.ChooseHost, flow.step)
    }

    @Test fun cancelStopsTheLinkAndReturnsToTheList() {
        toPinStep()
        val stops = link.stops
        flow.cancel()
        assertEquals(stops + 1, link.stops)
        assertEquals(Step.ChooseHost, flow.step)
    }

    @Test fun observersSeeEachDistinctStepOnce() {
        toPinStep()
        flow.onStateChanged(unpaired()) // no change while entering a PIN
        assertEquals(3, seen.size)
    }

    @Test fun pinSecretNeverAppearsInToString() {
        val text = com.shilapi.xcertplay.simhub.SimHubMessage.PairRequest.pin("482913").toString()
        assertFalse(text.contains("482913"))
        assertFalse(SimHubPairing("h", "a", 1, "n", "secret-token-value").toString().contains("secret-token-value"))
    }
}

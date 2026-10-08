package com.shilapi.xcertplay

import com.shilapi.xcertplay.simhub.PairFailure
import com.shilapi.xcertplay.simhub.SimHubLink
import com.shilapi.xcertplay.simhub.SimHubMessage
import com.shilapi.xcertplay.simhub.SimHubState

/**
 * First-run pairing with a SimHub PC (#27, `docs/protocol.md` §8): connect, ask the PC to show a PIN,
 * let the user type it, and report the outcome. The PIN is shown on the PC and typed on the tablet.
 *
 * Not thread-safe: the owner calls every method on one thread (the main thread in the app). Link
 * callbacks must be forwarded to [onStateChanged], [onPairResult], [onAttemptFailed] and [onPaired].
 */
class SimHubPairingFlow(
    private val link: SimHubLinkPort,
    private val onChanged: (Step) -> Unit = {},
) {
    sealed class Step {
        /** Choose a discovered PC or enter an address. */
        object ChooseHost : Step() {
            override fun toString() = "ChooseHost"
        }

        /** Opening TCP / waiting for `welcome`, or reconnecting after the session dropped. */
        data class Connecting(val host: String, val port: Int, val name: String?) : Step()

        /** Connected; `pairRequest` Start sent, waiting for the PC to show a PIN. */
        data class WaitingForPin(val host: String, val port: Int, val name: String) : Step()

        /**
         * The PC shows a PIN. [error] explains the last answer; [needsNewPin] means the current PIN
         * is gone (expired, too many attempts, refused) and the user must ask for a new one.
         */
        data class EnterPin(
            val host: String,
            val port: Int,
            val name: String,
            val expiresInSec: Int? = null,
            val error: PairingError? = null,
            val attemptsLeft: Int? = null,
            val submitting: Boolean = false,
            val needsNewPin: Boolean = false,
        ) : Step()

        /** The attempt stopped; the link is stopped too. Retry with [retry]. */
        data class Failed(val host: String, val port: Int, val name: String?, val error: PairingError) : Step()

        /** `pairResult ok`: the caller stored the pairing. */
        data class Paired(val pairing: SimHubPairing) : Step()
    }

    enum class PairingError { WRONG_PIN, PIN_EXPIRED, TOO_MANY_ATTEMPTS, DENIED, UNREACHABLE, INCOMPATIBLE }

    var step: Step = Step.ChooseHost
        private set

    /** True while a pairing attempt owns the link. */
    val active: Boolean get() = step !is Step.ChooseHost && step !is Step.Paired && step !is Step.Failed

    /** Connects to [host]:[port] and asks the PC for a PIN as soon as it answers. */
    fun connect(host: String, port: Int, name: String? = null) {
        link.stop()
        link.requestPairing()
        link.start(SimHubLink.Target(host = host, port = port))
        move(Step.Connecting(host, port, name))
    }

    /** Sends the 6-digit PIN. Returns false (and changes nothing) when it is malformed or there is no PIN step. */
    fun submitPin(pin: String): Boolean {
        val current = step as? Step.EnterPin ?: return false
        if (current.submitting || current.needsNewPin) return false
        if (pin.length != 6 || !pin.all { it in '0'..'9' }) return false
        if (!link.pair(pin)) return false
        move(current.copy(submitting = true, error = null))
        return true
    }

    /** After `pinExpired`, `tooManyAttempts` or `denied`: ask the PC for a new PIN (§6.4). */
    fun requestNewPin() {
        val current = step as? Step.EnterPin ?: return
        link.requestPairing()
        move(Step.WaitingForPin(current.host, current.port, current.name))
    }

    /** From [Step.Failed]: try the same PC again. */
    fun retry() {
        val current = step as? Step.Failed ?: return
        connect(current.host, current.port, current.name)
    }

    /** Back to the PC list; stops the link unless already paired. */
    fun cancel() {
        if (step is Step.Paired) return
        if (active || step is Step.Failed) link.stop()
        move(Step.ChooseHost)
    }

    /** Forget / token revoked: start over. Does not touch the link. */
    fun reset() = move(Step.ChooseHost)

    fun onStateChanged(state: SimHubState) {
        val current = step
        when (current) {
            is Step.Connecting -> if (state.phase == SimHubState.Phase.UNPAIRED) {
                move(Step.WaitingForPin(current.host, current.port, state.hostName ?: current.name ?: current.host))
            }
            is Step.WaitingForPin, is Step.EnterPin -> if (state.phase == SimHubState.Phase.WAITING ||
                state.phase == SimHubState.Phase.CONNECTING
            ) {
                // The session dropped (the PC closed it, Wi-Fi blipped). The link reconnects and asks
                // for a fresh PIN, since the request stays pending until pairing succeeds.
                val (host, port, name) = when (current) {
                    is Step.WaitingForPin -> Triple(current.host, current.port, current.name)
                    is Step.EnterPin -> Triple(current.host, current.port, current.name)
                    else -> return
                }
                move(Step.Connecting(host, port, name))
            }
            else -> Unit
        }
    }

    fun onPairResult(result: SimHubMessage.PairResult) {
        if (result.ok) return // onPaired follows with the token.
        val (host, port, name) = when (val current = step) {
            is Step.WaitingForPin -> Triple(current.host, current.port, current.name)
            is Step.EnterPin -> Triple(current.host, current.port, current.name)
            is Step.Connecting -> Triple(current.host, current.port, current.name ?: current.host)
            else -> return
        }
        val previous = step as? Step.EnterPin
        move(
            when (result.reason) {
                PairFailure.PIN_REQUIRED -> Step.EnterPin(host, port, name, expiresInSec = result.pinExpiresInSec)
                PairFailure.WRONG_PIN -> Step.EnterPin(
                    host, port, name,
                    expiresInSec = previous?.expiresInSec,
                    error = PairingError.WRONG_PIN,
                    attemptsLeft = result.attemptsLeft,
                )
                PairFailure.PIN_EXPIRED -> Step.EnterPin(host, port, name, error = PairingError.PIN_EXPIRED, needsNewPin = true)
                PairFailure.TOO_MANY_ATTEMPTS ->
                    Step.EnterPin(host, port, name, error = PairingError.TOO_MANY_ATTEMPTS, needsNewPin = true)
                PairFailure.DENIED -> Step.EnterPin(host, port, name, error = PairingError.DENIED, needsNewPin = true)
                // No token is sent during onboarding; treat it like a refusal if a PC says so anyway.
                PairFailure.TOKEN_INVALID, null ->
                    Step.EnterPin(host, port, name, error = PairingError.DENIED, needsNewPin = true)
            },
        )
    }

    fun onAttemptFailed(loss: SimHubLink.LinkLoss) {
        val current = step as? Step.Connecting ?: return
        val error = when (loss.reason) {
            SimHubLink.LossReason.CONNECT_FAILED,
            SimHubLink.LossReason.HANDSHAKE_TIMEOUT -> PairingError.UNREACHABLE
            SimHubLink.LossReason.INCOMPATIBLE -> PairingError.INCOMPATIBLE
            else -> return // The link retries on its own.
        }
        link.stop()
        move(Step.Failed(current.host, current.port, current.name, error))
    }

    /**
     * `pairResult ok`. Builds the pairing to store from the link's current target (which carries the
     * address actually used) and [hostName] from `welcome`.
     */
    fun onPaired(hostId: String, token: String, hostName: String?): SimHubPairing? {
        val target = link.currentTarget
        val (host, port, fallbackName) = when (val current = step) {
            is Step.WaitingForPin -> Triple(current.host, current.port, current.name)
            is Step.EnterPin -> Triple(current.host, current.port, current.name)
            is Step.Connecting -> Triple(current.host, current.port, current.name)
            else -> return null
        }
        val pairing = SimHubPairing(
            hostId = hostId,
            host = target?.host ?: host,
            port = target?.port ?: port,
            name = hostName ?: fallbackName ?: host,
            token = token,
        )
        move(Step.Paired(pairing))
        return pairing
    }

    private fun move(next: Step) {
        if (next == step) return
        step = next
        onChanged(next)
    }
}

package com.shilapi.xcertplay.simhub

/**
 * Messages of the rigPlay ↔ SimHub protocol, version 1 (`docs/protocol.md` §4.3 and §6).
 *
 * Optional members that the spec gives a default (`minProtocol`, `features` in `hello`, `fatal`)
 * are kept nullable so that a decoded message re-encodes to the same members; use the
 * `effective...` accessors for the value with the default applied. Absent and `null` mean the same
 * thing on the wire (§3), so the encoder omits optional members that are `null` and writes an explicit
 * `null` only for required members typed "or `null`".
 */
sealed class SimHubMessage {
    /** The wire `type` member. */
    abstract val type: String

    /** §4.3: UDP payload broadcast by the plugin. */
    data class Beacon(
        val name: String,
        val hostId: String,
        val version: String,
        val simhubVersion: String? = null,
        val controlPort: Int,
        val audioPort: Int,
        val protocol: Int,
        val minProtocol: Int? = null,
    ) : SimHubMessage() {
        override val type get() = SimHubProtocol.TYPE_BEACON
        val effectiveMinProtocol: Int get() = minProtocol ?: 1
    }

    /** §6.1: first line the tablet sends. */
    data class Hello(
        val tabletId: String,
        val name: String,
        val appVersion: String,
        val protocol: Int,
        val minProtocol: Int? = null,
        val features: List<String>? = null,
    ) : SimHubMessage() {
        override val type get() = SimHubProtocol.TYPE_HELLO
        val effectiveMinProtocol: Int get() = minProtocol ?: 1
        val effectiveFeatures: List<String> get() = features ?: emptyList()
    }

    /** §6.2: plugin's answer to a compatible hello. */
    data class Welcome(
        val hostId: String,
        val name: String,
        val version: String,
        val simhubVersion: String? = null,
        val protocol: Int,
        val features: List<String>,
    ) : SimHubMessage() {
        override val type get() = SimHubProtocol.TYPE_WELCOME
    }

    /** §6.3: Start (neither member), Submit PIN ([pin]) or Resume ([token]). Never both. */
    data class PairRequest(val token: String? = null, val pin: String? = null) : SimHubMessage() {
        override val type get() = SimHubProtocol.TYPE_PAIR_REQUEST

        override fun toString(): String =
            "PairRequest(token=${token?.let(SimHubProtocol::redactToken)}, pin=${pin?.let { "******" }})"

        companion object {
            val START = PairRequest()
            fun pin(pin: String) = PairRequest(pin = pin)
            fun resume(token: String) = PairRequest(token = token)
        }
    }

    /** §6.4. [token] is set exactly when [ok]; [reason] exactly when not. */
    data class PairResult(
        val ok: Boolean,
        val token: String? = null,
        val reason: PairFailure? = null,
        val pinExpiresInSec: Int? = null,
        val attemptsLeft: Int? = null,
    ) : SimHubMessage() {
        override val type get() = SimHubProtocol.TYPE_PAIR_RESULT

        /** Never print the token (§15); show its first 4 characters at most. */
        override fun toString(): String =
            "PairResult(ok=$ok, token=${token?.let(SimHubProtocol::redactToken)}, reason=$reason, " +
                "pinExpiresInSec=$pinExpiresInSec, attemptsLeft=$attemptsLeft)"
    }

    /** §6.5. [seq] is 0..4294967295 and only for logs. */
    data class Heartbeat(val seq: Long? = null) : SimHubMessage() {
        override val type get() = SimHubProtocol.TYPE_HEARTBEAT
    }

    /** §6.6: complete snapshot from the plugin. */
    data class State(
        val dashboardUrl: String?,
        val idleDashboardUrl: String? = null,
        val dashboardServer: DashboardServer? = null,
        val audio: AudioSettings,
        /** `state.mic` (§6.6): only for sessions with feature `mic`; `null` (omitted) otherwise. */
        val mic: MicSettings? = null,
    ) : SimHubMessage() {
        override val type get() = SimHubProtocol.TYPE_STATE
    }

    /** §6.7: complete snapshot from the tablet. [nav] is optional: `null` (omitted) without route guidance (#47). */
    data class Status(
        val phoneConnected: Boolean,
        val phoneName: String? = null,
        val screen: Screen,
        val nowPlaying: NowPlaying?,
        val nav: NavStatus? = null,
    ) : SimHubMessage() {
        override val type get() = SimHubProtocol.TYPE_STATUS

        companion object {
            /** What the tablet reports when no phone is connected. */
            val IDLE = Status(phoneConnected = false, screen = Screen.IDLE, nowPlaying = null)
        }
    }

    /** §6.8. */
    data class Command(val command: SimHubCommand) : SimHubMessage() {
        override val type get() = SimHubProtocol.TYPE_COMMAND
    }

    /** §6.9: every data member optional; invalid members decode to `null`. */
    data class Telemetry(
        val speedMps: Double? = null,
        val gear: Gear? = null,
        val heading: Double? = null,
        val lat: Double? = null,
        val lon: Double? = null,
        val alt: Double? = null,
        val night: Boolean? = null,
        val fuelPercent: Double? = null,
        val rangeKm: Double? = null,
        val rpm: Double? = null,
        val trackName: String? = null,
        val sessionType: String? = null,
        val gameRunning: Boolean? = null,
    ) : SimHubMessage() {
        override val type get() = SimHubProtocol.TYPE_TELEMETRY
    }

    /** §6.10. [code] is one of [SimHubProtocol.ERROR_CODES] or a newer code handled by [fatal]. */
    data class Error(
        val code: String,
        val message: String? = null,
        val fatal: Boolean? = null,
        val refType: String? = null,
        val minProtocol: Int? = null,
        val maxProtocol: Int? = null,
    ) : SimHubMessage() {
        override val type get() = SimHubProtocol.TYPE_ERROR
        val isFatal: Boolean get() = fatal == true
    }

    /** §6.11. */
    data class AudioStart(
        val stream: AudioStream,
        val format: AudioFormat = AudioFormat.PCM_S16LE,
        val sampleRate: Int,
        val channels: Int,
    ) : SimHubMessage() {
        override val type get() = SimHubProtocol.TYPE_AUDIO_START
    }

    /** §6.12. [stream] is never [AudioStream.MIC]. */
    data class AudioStop(val stream: AudioStream) : SimHubMessage() {
        override val type get() = SimHubProtocol.TYPE_AUDIO_STOP
    }

    /**
     * §6.13, tablet → plugin with feature `mic`: send the PC microphone, mono [sampleRate] Hz s16, as datagrams with
     * `streamType` 4 to [port] on this tablet. [streamType] is always [AudioStream.MIC]'s code.
     */
    data class MicStart(
        val sampleRate: Int,
        val port: Int = SimHubProtocol.MIC_PORT,
        val channels: Int = 1,
        val format: AudioFormat = AudioFormat.PCM_S16LE,
        val streamType: Int = AudioStream.MIC.code,
    ) : SimHubMessage() {
        override val type get() = SimHubProtocol.TYPE_MIC_START
    }

    /** §6.13: the phone closed its microphone. */
    data class MicStop(val streamType: Int = AudioStream.MIC.code) : SimHubMessage() {
        override val type get() = SimHubProtocol.TYPE_MIC_STOP
    }

    /**
     * Tablet → plugin (#47): the now-playing artwork, a small JPEG (≤ 256 px) in [base64], sent when it
     * changes, at most once per 2 s; the whole line stays within [SimHubProtocol.MAX_LINE_BYTES].
     */
    data class Artwork(val mime: String, val base64: String) : SimHubMessage() {
        override val type get() = SimHubProtocol.TYPE_ARTWORK

        override fun toString(): String = "Artwork(mime=$mime, base64=${base64.length} chars)"
    }
}

/** §6.8: what the plugin asks the tablet to do. */
sealed class SimHubCommand {
    data class Media(val action: MediaAction) : SimHubCommand()
    object ShowDashboard : SimHubCommand() {
        override fun toString() = "ShowDashboard"
    }
    object ShowCarPlay : SimHubCommand() {
        override fun toString() = "ShowCarPlay"
    }
}

/**
 * `status.nav` (#47): CarPlay's next maneuver. [maneuver] is the lowerCamel name of Apple's
 * RouteGuidanceManeuverType (`com.shilapi.xcertplay.guidance.RouteManeuverNames`); [distanceM] is the
 * distance to it, [road] the road it leads onto, [etaEpochS] the arrival time (Unix seconds).
 */
data class NavStatus(
    val maneuver: String,
    val distanceM: Int? = null,
    val road: String? = null,
    val etaEpochS: Long? = null,
)

/** `state.dashboardServer` (§6.6). */
data class DashboardServer(val reachable: Boolean, val port: Int)

/** `state.audio` (§6.6). */
data class AudioSettings(val enabled: Boolean, val port: Int, val formats: List<String>)

/** `state.mic` (§6.6): [enabled] when the plugin answers `micStart` with the PC microphone. */
data class MicSettings(val enabled: Boolean)

/** `status.nowPlaying` (§6.7). Text members are required but may be `null`. */
data class NowPlaying(
    val title: String?,
    val artist: String?,
    val album: String?,
    val app: String?,
    val playing: Boolean,
    val position: Double,
    val duration: Double?,
    val updatedAt: Long,
)

/** Enum values carry their exact wire spelling; receivers reject unknown values (§7.2). */
interface WireEnum {
    val wire: String
}

internal inline fun <reified E> wireValueOf(value: String): E? where E : Enum<E>, E : WireEnum =
    enumValues<E>().firstOrNull { it.wire == value }

enum class PairFailure(override val wire: String) : WireEnum {
    PIN_REQUIRED("pinRequired"),
    WRONG_PIN("wrongPin"),
    PIN_EXPIRED("pinExpired"),
    TOO_MANY_ATTEMPTS("tooManyAttempts"),
    DENIED("denied"),
    TOKEN_INVALID("tokenInvalid"),
}

enum class Screen(override val wire: String) : WireEnum {
    CARPLAY("carplay"),
    DASHBOARD("dashboard"),
    IDLE("idle"),
    OFF("off"),
}

enum class MediaAction(override val wire: String) : WireEnum {
    PLAY_PAUSE("playPause"),
    NEXT("next"),
    PREVIOUS("previous"),
    SIRI("siri"),
}

enum class Gear(override val wire: String) : WireEnum {
    P("P"),
    R("R"),
    N("N"),
    D("D"),
}

/**
 * Which way an audio datagram flows (§10.2): `streamType` 1–3 tablet → plugin, 4 (`mic`) plugin → tablet only.
 * Decoding a datagram always names the direction it is received in.
 */
enum class AudioDirection {
    /** CarPlay audio the tablet sends to the plugin. */
    TABLET_TO_PC,

    /** The PC microphone the plugin sends to the tablet (§10.5). */
    PC_TO_TABLET,
}

/**
 * Audio stream names (§6.11) and their datagram `streamType` codes (§10.2). [MIC] is a datagram stream only: it
 * flows plugin → tablet and `audioStart`/`audioStop` cannot name it (the microphone has `micStart`/`micStop`).
 */
enum class AudioStream(override val wire: String, val code: Int, val direction: AudioDirection = AudioDirection.TABLET_TO_PC) : WireEnum {
    MEDIA("media", 1),
    ALT("alt", 2),
    TELEPHONY("telephony", 3),
    MIC("mic", 4, AudioDirection.PC_TO_TABLET),
    ;

    companion object {
        fun fromCode(code: Int): AudioStream? = values().firstOrNull { it.code == code }
    }
}

/**
 * Audio formats (§6.11) and their datagram `format` codes (§10.2): PCM, the default, and Opus (§10.4), sent
 * only when the plugin listed it first in `state.audio.formats`.
 */
enum class AudioFormat(override val wire: String, val code: Int) : WireEnum {
    PCM_S16LE("pcm_s16le", 1),
    OPUS("opus", 2),
    ;

    companion object {
        fun fromCode(code: Int): AudioFormat? = values().firstOrNull { it.code == code }

        /** The sample rates an Opus stream may use (§10.4). */
        val OPUS_SAMPLE_RATES = listOf(8_000, 12_000, 16_000, 24_000, 48_000)

        fun fromWire(wire: String): AudioFormat? = values().firstOrNull { it.wire == wire }
    }
}

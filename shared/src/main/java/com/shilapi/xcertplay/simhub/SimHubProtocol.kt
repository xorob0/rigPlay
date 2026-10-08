package com.shilapi.xcertplay.simhub

import java.net.URI
import org.json.JSONArray
import org.json.JSONException
import org.json.JSONObject
import org.json.JSONTokener

/** Outcome of decoding one control line or beacon payload. */
sealed class SimHubParseResult {
    /** A known message whose members all satisfy the spec. */
    data class Ok(val message: SimHubMessage) : SimHubParseResult()

    /** A JSON object with a string `type` this version does not know; ignored without reply (§14.2). */
    data class Unknown(val type: String) : SimHubParseResult()

    /**
     * Not JSON, not an object, no string `type` ([refType] `null`), or a known type with invalid
     * content ([refType] is that type, so a `badMessage` reply can carry it).
     */
    data class Malformed(val refType: String?, val reason: String) : SimHubParseResult()
}

/**
 * Constants and the JSON codec of the rigPlay ↔ SimHub protocol, version 1. `docs/protocol.md` is the
 * contract; section numbers in comments refer to it. Uses Android's `org.json`, so JVM tests run
 * under Robolectric.
 */
object SimHubProtocol {
    // §2 ports.
    const val DISCOVERY_PORT = 23710
    const val CONTROL_PORT = 23711
    const val AUDIO_PORT = 23712
    const val MIC_PORT = 23713

    // §7 versions this implementation speaks.
    const val PROTOCOL_VERSION = 1
    const val MIN_PROTOCOL_VERSION = 1

    // §7.3 features.
    const val FEATURE_TELEMETRY = "telemetry"
    const val FEATURE_IDLE_DASHBOARD = "idleDashboard"
    const val FEATURE_MIC = "mic"

    // §4, §9 timing.
    const val BEACON_INTERVAL_MS = 1_000L
    const val DISCOVERY_EXPIRY_MS = 5_000L
    const val HEARTBEAT_INTERVAL_MS = 1_000L
    const val LINK_LOSS_TIMEOUT_MS = 5_000L
    const val RECONNECT_INITIAL_DELAY_MS = 1_000L
    const val RECONNECT_MAX_DELAY_MS = 10_000L
    const val RECONNECT_JITTER = 0.2
    const val STATUS_MIN_INTERVAL_MS = 250L

    // §4.1, §5.1 sizes.
    const val MAX_BEACON_BYTES = 1024
    const val MAX_LINE_BYTES = 65_536

    const val FORMAT_PCM_S16LE = "pcm_s16le"
    const val FORMAT_OPUS = "opus"

    // §5.3 message types.
    const val TYPE_BEACON = "beacon"
    const val TYPE_HELLO = "hello"
    const val TYPE_WELCOME = "welcome"
    const val TYPE_PAIR_REQUEST = "pairRequest"
    const val TYPE_PAIR_RESULT = "pairResult"
    const val TYPE_HEARTBEAT = "heartbeat"
    const val TYPE_STATE = "state"
    const val TYPE_STATUS = "status"
    const val TYPE_COMMAND = "command"
    const val TYPE_TELEMETRY = "telemetry"
    const val TYPE_ERROR = "error"
    const val TYPE_AUDIO_START = "audioStart"
    const val TYPE_AUDIO_STOP = "audioStop"
    const val TYPE_ARTWORK = "artwork"
    const val TYPE_MIC_START = "micStart"
    const val TYPE_MIC_STOP = "micStop"

    const val MIME_JPEG = "image/jpeg"

    // §14.1 error codes.
    const val ERROR_UNSUPPORTED_PROTOCOL = "unsupportedProtocol"
    const val ERROR_HELLO_REQUIRED = "helloRequired"
    const val ERROR_NOT_PAIRED = "notPaired"
    const val ERROR_UNEXPECTED_MESSAGE = "unexpectedMessage"
    const val ERROR_BAD_MESSAGE = "badMessage"
    const val ERROR_LINE_TOO_LONG = "lineTooLong"
    const val ERROR_REPLACED = "replaced"
    const val ERROR_FORGOTTEN = "forgotten"
    const val ERROR_SHUTDOWN = "shutdown"
    const val ERROR_COMMAND_UNAVAILABLE = "commandUnavailable"
    const val ERROR_INTERNAL = "internal"

    val ERROR_CODES: Set<String> = setOf(
        ERROR_UNSUPPORTED_PROTOCOL, ERROR_HELLO_REQUIRED, ERROR_NOT_PAIRED, ERROR_UNEXPECTED_MESSAGE,
        ERROR_BAD_MESSAGE, ERROR_LINE_TOO_LONG, ERROR_REPLACED, ERROR_FORGOTTEN, ERROR_SHUTDOWN,
        ERROR_COMMAND_UNAVAILABLE, ERROR_INTERNAL,
    )

    private const val COMMAND_MEDIA = "media"
    private const val COMMAND_SHOW_DASHBOARD = "showDashboard"
    private const val COMMAND_SHOW_CARPLAY = "showCarPlay"
    private const val U32_MAX = 4_294_967_295L
    private val PIN_PATTERN = Regex("^[0-9]{6}$")

    /** Logs may show the first 4 characters of a token, never the rest (§15). */
    fun redactToken(token: String): String = token.take(4) + "…"

    /** True when `[min, max]` overlaps the versions this implementation speaks (§4.2, §7.1). */
    fun isCompatible(minProtocol: Int, maxProtocol: Int): Boolean =
        minOf(maxProtocol, PROTOCOL_VERSION) >= maxOf(minProtocol, MIN_PROTOCOL_VERSION)

    /** Decodes, returning only a valid known message. */
    fun parseOrNull(line: String): SimHubMessage? = (parse(line) as? SimHubParseResult.Ok)?.message

    /**
     * Decodes one line (one `\r` before the end is stripped, §5.1) or one beacon payload.
     * Validates every member the spec requires; never throws.
     */
    fun parse(line: String): SimHubParseResult {
        val text = line.removeSuffix("\n").removeSuffix("\r")
        val root = try {
            val tokener = JSONTokener(text)
            val value = tokener.nextValue()
            if (tokener.nextClean() != '\u0000') {
                return SimHubParseResult.Malformed(null, "trailing characters after the JSON value")
            }
            value
        } catch (error: JSONException) {
            return SimHubParseResult.Malformed(null, "not JSON: ${error.message}")
        }
        if (root !is JSONObject) return SimHubParseResult.Malformed(null, "not a JSON object")
        val type = root.opt("type") as? String
            ?: return SimHubParseResult.Malformed(null, "no string type")
        val fields = Fields(root)
        return try {
            val message = when (type) {
                TYPE_BEACON -> decodeBeacon(fields)
                TYPE_HELLO -> decodeHello(fields)
                TYPE_WELCOME -> decodeWelcome(fields)
                TYPE_PAIR_REQUEST -> decodePairRequest(fields)
                TYPE_PAIR_RESULT -> decodePairResult(fields)
                TYPE_HEARTBEAT -> SimHubMessage.Heartbeat(fields.optInt("seq", 0L..U32_MAX))
                TYPE_STATE -> decodeState(fields)
                TYPE_STATUS -> decodeStatus(fields)
                TYPE_COMMAND -> decodeCommand(fields)
                TYPE_TELEMETRY -> decodeTelemetry(root)
                TYPE_ERROR -> decodeError(fields)
                TYPE_AUDIO_START -> decodeAudioStart(fields)
                TYPE_AUDIO_STOP -> SimHubMessage.AudioStop(fields.reqStream())
                TYPE_MIC_START -> decodeMicStart(fields)
                TYPE_MIC_STOP -> SimHubMessage.MicStop(fields.reqMicStreamType())
                TYPE_ARTWORK -> SimHubMessage.Artwork(mime = fields.reqString("mime", 1..255), base64 = fields.reqString("base64", 1..MAX_LINE_BYTES))
                else -> return SimHubParseResult.Unknown(type)
            }
            SimHubParseResult.Ok(message)
        } catch (error: BadMember) {
            SimHubParseResult.Malformed(type, error.message ?: "invalid member")
        }
    }

    /** Encodes [message] as one JSON line without the terminating `\n`. */
    fun encode(message: SimHubMessage): String = toJson(message).toString()

    /** [encode] plus the `\n` terminator, as UTF-8 bytes ready for the socket. */
    fun encodeLine(message: SimHubMessage): ByteArray = (encode(message) + "\n").toByteArray(Charsets.UTF_8)

    fun toJson(message: SimHubMessage): JSONObject {
        val json = JSONObject().put("type", message.type)
        when (message) {
            is SimHubMessage.Beacon -> json
                .put("name", message.name)
                .put("hostId", message.hostId)
                .put("version", message.version)
                .putOpt("simhubVersion", message.simhubVersion)
                .put("controlPort", message.controlPort)
                .put("audioPort", message.audioPort)
                .put("protocol", message.protocol)
                .putOpt("minProtocol", message.minProtocol)
            is SimHubMessage.Hello -> json
                .put("tabletId", message.tabletId)
                .put("name", message.name)
                .put("appVersion", message.appVersion)
                .put("protocol", message.protocol)
                .putOpt("minProtocol", message.minProtocol)
                .putOpt("features", message.features?.let(::JSONArray))
            is SimHubMessage.Welcome -> json
                .put("hostId", message.hostId)
                .put("name", message.name)
                .put("version", message.version)
                .putOpt("simhubVersion", message.simhubVersion)
                .put("protocol", message.protocol)
                .put("features", JSONArray(message.features))
            is SimHubMessage.PairRequest -> json
                .putOpt("token", message.token)
                .putOpt("pin", message.pin)
            is SimHubMessage.PairResult -> json
                .put("ok", message.ok)
                .putOpt("token", message.token)
                .putOpt("reason", message.reason?.wire)
                .putOpt("pinExpiresInSec", message.pinExpiresInSec)
                .putOpt("attemptsLeft", message.attemptsLeft)
            is SimHubMessage.Heartbeat -> json.putOpt("seq", message.seq)
            is SimHubMessage.State -> json
                .put("dashboardUrl", message.dashboardUrl ?: JSONObject.NULL)
                .putOpt("idleDashboardUrl", message.idleDashboardUrl)
                .putOpt("dashboardServer", message.dashboardServer?.let {
                    JSONObject().put("reachable", it.reachable).put("port", it.port)
                })
                .put("audio", JSONObject()
                    .put("enabled", message.audio.enabled)
                    .put("port", message.audio.port)
                    .put("formats", JSONArray(message.audio.formats)))
                .putOpt("mic", message.mic?.let { JSONObject().put("enabled", it.enabled) })
            is SimHubMessage.Status -> json
                .put("phoneConnected", message.phoneConnected)
                .putOpt("phoneName", message.phoneName)
                .put("screen", message.screen.wire)
                .put("nowPlaying", message.nowPlaying?.let(::nowPlayingJson) ?: JSONObject.NULL)
                .putOpt("nav", message.nav?.let(::navJson))
            is SimHubMessage.Command -> when (val command = message.command) {
                is SimHubCommand.Media -> json.put("command", COMMAND_MEDIA).put("action", command.action.wire)
                SimHubCommand.ShowDashboard -> json.put("command", COMMAND_SHOW_DASHBOARD)
                SimHubCommand.ShowCarPlay -> json.put("command", COMMAND_SHOW_CARPLAY)
            }
            is SimHubMessage.Telemetry -> json
                .putFinite("speedMps", message.speedMps)
                .putOpt("gear", message.gear?.wire)
                .putFinite("heading", message.heading)
                .putFinite("lat", message.lat)
                .putFinite("lon", message.lon)
                .putFinite("alt", message.alt)
                .putOpt("night", message.night)
                .putFinite("fuelPercent", message.fuelPercent)
                .putFinite("rangeKm", message.rangeKm)
                .putFinite("rpm", message.rpm)
                .putOpt("trackName", message.trackName)
                .putOpt("sessionType", message.sessionType)
                .putOpt("gameRunning", message.gameRunning)
            is SimHubMessage.Error -> json
                .put("code", message.code)
                .putOpt("message", message.message)
                .putOpt("fatal", message.fatal)
                .putOpt("refType", message.refType)
                .putOpt("minProtocol", message.minProtocol)
                .putOpt("maxProtocol", message.maxProtocol)
            is SimHubMessage.AudioStart -> json
                .put("stream", message.stream.wire)
                .put("format", message.format.wire)
                .put("sampleRate", message.sampleRate)
                .put("channels", message.channels)
            is SimHubMessage.AudioStop -> json.put("stream", message.stream.wire)
            is SimHubMessage.MicStart -> json
                .put("streamType", message.streamType)
                .put("format", message.format.wire)
                .put("sampleRate", message.sampleRate)
                .put("channels", message.channels)
                .put("port", message.port)
            is SimHubMessage.MicStop -> json.put("streamType", message.streamType)
            is SimHubMessage.Artwork -> json.put("mime", message.mime).put("base64", message.base64)
        }
        return json
    }

    private fun navJson(nav: NavStatus): JSONObject = JSONObject()
        .put("maneuver", nav.maneuver)
        .putOpt("distanceM", nav.distanceM)
        .putOpt("road", nav.road)
        .putOpt("etaEpochS", nav.etaEpochS)

    private fun nowPlayingJson(nowPlaying: NowPlaying): JSONObject {
        // §3: a decimal that is not finite and has no null option means the message is not sent.
        require(nowPlaying.position.isFinite()) { "nowPlaying.position must be finite" }
        require(nowPlaying.duration?.isFinite() != false) { "nowPlaying.duration must be finite or null" }
        return JSONObject()
            .put("title", nowPlaying.title ?: JSONObject.NULL)
            .put("artist", nowPlaying.artist ?: JSONObject.NULL)
            .put("album", nowPlaying.album ?: JSONObject.NULL)
            .put("app", nowPlaying.app ?: JSONObject.NULL)
            .put("playing", nowPlaying.playing)
            .put("position", nowPlaying.position)
            .put("duration", nowPlaying.duration ?: JSONObject.NULL)
            .put("updatedAt", nowPlaying.updatedAt)
    }

    private fun JSONObject.putFinite(name: String, value: Double?): JSONObject =
        if (value != null && value.isFinite()) put(name, value) else this

    // --- decoding -----------------------------------------------------------------------------

    private fun decodeBeacon(f: Fields): SimHubMessage.Beacon {
        val protocol = f.reqInt("protocol", 1L..Int.MAX_VALUE).toInt()
        val minProtocol = f.optInt("minProtocol", 1L..Int.MAX_VALUE)?.toInt()
        if (minProtocol != null && minProtocol > protocol) throw BadMember("minProtocol above protocol")
        return SimHubMessage.Beacon(
            name = f.reqString("name"),
            hostId = f.reqString("hostId", 1..128),
            version = f.reqString("version"),
            simhubVersion = f.optString("simhubVersion"),
            controlPort = f.reqPort("controlPort"),
            audioPort = f.reqPort("audioPort"),
            protocol = protocol,
            minProtocol = minProtocol,
        )
    }

    private fun decodeHello(f: Fields): SimHubMessage.Hello {
        val protocol = f.reqInt("protocol", 1L..Int.MAX_VALUE).toInt()
        val minProtocol = f.optInt("minProtocol", 1L..Int.MAX_VALUE)?.toInt()
        if (minProtocol != null && minProtocol > protocol) throw BadMember("minProtocol above protocol")
        return SimHubMessage.Hello(
            tabletId = f.reqString("tabletId", 1..64),
            name = f.reqString("name"),
            appVersion = f.reqString("appVersion"),
            protocol = protocol,
            minProtocol = minProtocol,
            features = f.optStringList("features"),
        )
    }

    private fun decodeWelcome(f: Fields) = SimHubMessage.Welcome(
        hostId = f.reqString("hostId", 1..128),
        name = f.reqString("name"),
        version = f.reqString("version"),
        simhubVersion = f.optString("simhubVersion"),
        protocol = f.reqInt("protocol", 1L..Int.MAX_VALUE).toInt(),
        features = f.reqStringList("features"),
    )

    private fun decodePairRequest(f: Fields): SimHubMessage.PairRequest {
        val token = f.optString("token", 1..128)
        val pin = f.optString("pin")
        if (pin != null && !PIN_PATTERN.matches(pin)) throw BadMember("pin must be exactly 6 ASCII digits")
        if (token != null && pin != null) throw BadMember("token and pin together")
        return SimHubMessage.PairRequest(token = token, pin = pin)
    }

    private fun decodePairResult(f: Fields): SimHubMessage.PairResult {
        val ok = f.reqBoolean("ok")
        val token = f.optString("token", 1..128)
        val reason = f.optEnum<PairFailure>("reason")
        if (ok && token == null) throw BadMember("ok without token")
        if (ok && reason != null) throw BadMember("reason with ok")
        if (!ok && reason == null) throw BadMember("missing reason")
        if (!ok && token != null) throw BadMember("token without ok")
        return SimHubMessage.PairResult(
            ok = ok,
            token = token,
            reason = reason,
            pinExpiresInSec = f.optInt("pinExpiresInSec", 1L..Int.MAX_VALUE)?.toInt(),
            attemptsLeft = f.optInt("attemptsLeft", 0L..Int.MAX_VALUE)?.toInt(),
        )
    }

    private fun decodeState(f: Fields): SimHubMessage.State {
        val server = f.optObject("dashboardServer")?.let {
            DashboardServer(reachable = it.reqBoolean("reachable"), port = it.reqPort("port"))
        }
        val audio = f.reqObject("audio").let {
            AudioSettings(enabled = it.reqBoolean("enabled"), port = it.reqPort("port"), formats = it.reqStringList("formats"))
        }
        return SimHubMessage.State(
            dashboardUrl = f.reqNullableString("dashboardUrl")?.also { requireHttpUrl("dashboardUrl", it) },
            idleDashboardUrl = f.optString("idleDashboardUrl")?.also { requireHttpUrl("idleDashboardUrl", it) },
            dashboardServer = server,
            audio = audio,
            mic = f.optObject("mic")?.let { MicSettings(enabled = it.reqBoolean("enabled")) },
        )
    }

    private fun requireHttpUrl(name: String, value: String) {
        val uri = try {
            URI(value)
        } catch (_: Exception) {
            throw BadMember("$name is not a URL")
        }
        val scheme = uri.scheme?.lowercase()
        if ((scheme != "http" && scheme != "https") || uri.host.isNullOrEmpty()) {
            throw BadMember("$name is not an absolute http URL")
        }
    }

    private fun decodeStatus(f: Fields): SimHubMessage.Status {
        val nowPlaying = f.reqNullableObject("nowPlaying")?.let {
            NowPlaying(
                title = it.reqNullableString("title"),
                artist = it.reqNullableString("artist"),
                album = it.reqNullableString("album"),
                app = it.reqNullableString("app"),
                playing = it.reqBoolean("playing"),
                position = it.reqDecimal("position").also { p -> if (p < 0) throw BadMember("position below 0") },
                duration = it.reqNullableDecimal("duration")?.also { d -> if (d <= 0) throw BadMember("duration not above 0") },
                updatedAt = it.reqInt("updatedAt", Long.MIN_VALUE..Long.MAX_VALUE),
            )
        }
        val nav = decodeNav(f.optJsonObject("nav"))
        return SimHubMessage.Status(
            phoneConnected = f.reqBoolean("phoneConnected"),
            phoneName = f.optString("phoneName"),
            screen = f.reqEnum("screen"),
            nowPlaying = nowPlaying,
            nav = nav,
        )
    }

    /**
     * §6.7.1: `status.nav` is validated leniently like telemetry (mirrors the C# `DecodeNav`): a `nav`
     * that is not an object means no route guidance; a member with the wrong type or out of range is
     * treated as absent; a missing or invalid `maneuver` drops the whole `nav`; a bad `nav` never
     * makes the status invalid — the rest of the status is used either way.
     */
    private fun decodeNav(nav: JSONObject?): NavStatus? {
        if (nav == null) return null
        val maneuver = (nav.opt("maneuver") as? String)?.takeIf { it.length in 1..64 } ?: return null
        val distanceM = (nav.opt("distanceM") as? Number)?.toDouble()?.let { value ->
            if (!value.isFinite() || value < 0.0 || value > Int.MAX_VALUE.toDouble()) {
                null
            } else {
                // Whole metres on the wire; a decimal is accepted and rounded away from zero (C# behavior).
                Math.round(value).toInt()
            }
        }
        val road = (nav.opt("road") as? String)?.takeUnless { it.isBlank() }
        val etaEpochS = when (val value = nav.opt("etaEpochS")) {
            is Int -> value.toLong().takeIf { it >= 0 }
            is Long -> value.takeIf { it >= 0 }
            else -> null
        }
        return NavStatus(maneuver = maneuver, distanceM = distanceM, road = road, etaEpochS = etaEpochS)
    }

    private fun decodeCommand(f: Fields): SimHubMessage.Command {
        val command = when (val name = f.reqString("command")) {
            COMMAND_MEDIA -> SimHubCommand.Media(f.reqEnum("action"))
            // "action: Ignored for other commands" (§6.8), whatever its value.
            COMMAND_SHOW_DASHBOARD -> SimHubCommand.ShowDashboard
            COMMAND_SHOW_CARPLAY -> SimHubCommand.ShowCarPlay
            else -> throw BadMember("unknown command $name")
        }
        return SimHubMessage.Command(command)
    }

    /** §6.9: per-member validation; a bad member becomes `null`, the rest is used. */
    private fun decodeTelemetry(json: JSONObject): SimHubMessage.Telemetry {
        fun decimal(name: String, range: ClosedFloatingPointRange<Double>? = null, below: Double? = null): Double? {
            val value = (json.opt(name) as? Number)?.toDouble() ?: return null
            if (!value.isFinite()) return null
            if (range != null && value !in range) return null
            if (below != null && value >= below) return null
            return value
        }
        fun text(name: String) = json.opt(name) as? String
        fun flag(name: String) = json.opt(name) as? Boolean
        return SimHubMessage.Telemetry(
            speedMps = decimal("speedMps", 0.0..Double.MAX_VALUE),
            gear = text("gear")?.let { wireValueOf<Gear>(it) },
            heading = decimal("heading", 0.0..Double.MAX_VALUE, below = 360.0),
            lat = decimal("lat", -90.0..90.0),
            lon = decimal("lon", -180.0..180.0),
            alt = decimal("alt"),
            night = flag("night"),
            fuelPercent = decimal("fuelPercent", 0.0..100.0),
            rangeKm = decimal("rangeKm", 0.0..Double.MAX_VALUE),
            rpm = decimal("rpm", 0.0..Double.MAX_VALUE),
            trackName = text("trackName"),
            sessionType = text("sessionType"),
            gameRunning = flag("gameRunning"),
        )
    }

    private fun decodeError(f: Fields): SimHubMessage.Error {
        val code = f.reqString("code")
        val unsupported = code == ERROR_UNSUPPORTED_PROTOCOL
        val range = 1L..Int.MAX_VALUE
        return SimHubMessage.Error(
            code = code,
            message = f.optString("message"),
            fatal = f.optBoolean("fatal"),
            refType = f.optString("refType"),
            minProtocol = (if (unsupported) f.reqInt("minProtocol", range) else f.optInt("minProtocol", range))?.toInt(),
            maxProtocol = (if (unsupported) f.reqInt("maxProtocol", range) else f.optInt("maxProtocol", range))?.toInt(),
        )
    }

    private fun decodeAudioStart(f: Fields): SimHubMessage.AudioStart {
        val sampleRate = f.reqInt("sampleRate", 8_000L..48_000L).toInt()
        if (sampleRate % 100 != 0) throw BadMember("sampleRate must be a multiple of 100")
        val format: AudioFormat = f.reqEnum("format")
        if (format == AudioFormat.OPUS && sampleRate !in AudioFormat.OPUS_SAMPLE_RATES) {
            throw BadMember("sampleRate for opus must be 8000, 12000, 16000, 24000 or 48000")
        }
        return SimHubMessage.AudioStart(
            stream = f.reqStream(),
            format = format,
            sampleRate = sampleRate,
            channels = f.reqInt("channels", 1L..2L).toInt(),
        )
    }

    /** §6.13: a mono `pcm_s16le` stream with `streamType` 4 at a protocol sample rate, to a valid port. */
    private fun decodeMicStart(f: Fields): SimHubMessage.MicStart {
        val sampleRate = f.reqInt("sampleRate", 8_000L..48_000L).toInt()
        if (sampleRate % 100 != 0) throw BadMember("sampleRate must be a multiple of 100")
        val format: AudioFormat = f.reqEnum("format")
        if (format != AudioFormat.PCM_S16LE) throw BadMember("the microphone is pcm_s16le only")
        return SimHubMessage.MicStart(
            streamType = f.reqMicStreamType(),
            format = format,
            sampleRate = sampleRate,
            channels = f.reqInt("channels", 1L..1L).toInt(),
            port = f.reqPort("port"),
        )
    }

    private class BadMember(message: String) : Exception(message)

    /** Strict accessors: wrong JSON types are errors, `org.json`'s coercions are not used. */
    private class Fields(private val json: JSONObject) {
        private fun raw(name: String): Any? = json.opt(name)?.takeUnless { it === JSONObject.NULL }

        fun reqString(name: String, length: IntRange? = null): String =
            optString(name, length) ?: throw BadMember("missing $name")

        fun optString(name: String, length: IntRange? = null): String? {
            val value = raw(name) ?: return null
            if (value !is String) throw BadMember("$name must be a string")
            if (length != null && value.length !in length) throw BadMember("$name length outside $length")
            return value
        }

        fun reqNullableString(name: String): String? {
            if (!json.has(name)) throw BadMember("missing $name")
            return optString(name)
        }

        fun reqInt(name: String, range: LongRange): Long = optInt(name, range) ?: throw BadMember("missing $name")

        fun optInt(name: String, range: LongRange): Long? {
            val value = raw(name) ?: return null
            // §3: integers have no fraction or exponent; org.json then yields Int or Long.
            val long = when (value) {
                is Int -> value.toLong()
                is Long -> value
                else -> throw BadMember("$name must be an integer")
            }
            if (long !in range) throw BadMember("$name outside $range")
            return long
        }

        fun reqPort(name: String): Int = reqInt(name, 1L..65_535L).toInt()

        /** `stream` of audioStart / audioStop: media, alt or telephony; `mic` is a datagram stream only (§6.13). */
        fun reqStream(): AudioStream {
            val stream = reqEnum<AudioStream>("stream")
            if (stream == AudioStream.MIC) throw BadMember("unknown stream value mic")
            return stream
        }

        /** `streamType` of micStart / micStop: always 4. */
        fun reqMicStreamType(): Int = reqInt("streamType", AudioStream.MIC.code.toLong()..AudioStream.MIC.code.toLong()).toInt()

        fun reqDecimal(name: String): Double = optDecimal(name) ?: throw BadMember("missing $name")

        fun reqNullableDecimal(name: String): Double? {
            if (!json.has(name)) throw BadMember("missing $name")
            return optDecimal(name)
        }

        private fun optDecimal(name: String): Double? {
            val value = raw(name) ?: return null
            if (value !is Number) throw BadMember("$name must be a number")
            return value.toDouble().also { if (!it.isFinite()) throw BadMember("$name must be finite") }
        }

        fun reqBoolean(name: String): Boolean = optBoolean(name) ?: throw BadMember("missing $name")

        fun optBoolean(name: String): Boolean? {
            val value = raw(name) ?: return null
            return value as? Boolean ?: throw BadMember("$name must be a boolean")
        }

        inline fun <reified E> reqEnum(name: String): E where E : Enum<E>, E : WireEnum =
            optEnum<E>(name) ?: throw BadMember("missing $name")

        inline fun <reified E> optEnum(name: String): E? where E : Enum<E>, E : WireEnum {
            val value = optString(name) ?: return null
            return wireValueOf<E>(value) ?: throw BadMember("unknown $name value $value")
        }

        fun reqStringList(name: String): List<String> = optStringList(name) ?: throw BadMember("missing $name")

        fun optStringList(name: String): List<String>? {
            val value = raw(name) ?: return null
            if (value !is JSONArray) throw BadMember("$name must be an array")
            return List(value.length()) { index ->
                value.opt(index) as? String ?: throw BadMember("$name[$index] must be a string")
            }
        }

        fun reqObject(name: String): Fields = optObject(name) ?: throw BadMember("missing $name")

        fun reqNullableObject(name: String): Fields? {
            if (!json.has(name)) throw BadMember("missing $name")
            return optObject(name)
        }

        fun optObject(name: String): Fields? {
            val value = raw(name) ?: return null
            if (value !is JSONObject) throw BadMember("$name must be an object")
            return Fields(value)
        }

        /** Raw nested object for lenient decoding: a member that is absent or not an object yields `null`, never an error. */
        fun optJsonObject(name: String): JSONObject? = raw(name) as? JSONObject
    }
}

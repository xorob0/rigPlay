package com.shilapi.xcertplay.simhub

import java.io.File
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * Conformance test required by `docs/protocol.md` §17 and `protocol/fixtures/README.md`: reads every
 * file in `protocol/fixtures/` (enumerated, not listed) and checks the Kotlin codec against it.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [29], manifest = Config.NONE)
class ProtocolFixturesTest {
    private val fixtures: File = locateFixtures()

    private val validFiles: List<File>
        get() = fixtures.listFiles { file -> file.isFile && file.name.endsWith(".json") && file.name != AUDIO_HEADER }!!
            .sortedBy { it.name }

    private val invalidFiles: List<File>
        get() = File(fixtures, "invalid").listFiles { file -> file.isFile && file.name.endsWith(".json") }!!
            .sortedBy { it.name }

    @Test fun everyValidFixtureDecodesToItsTypeAndRoundTrips() {
        val files = validFiles
        assertTrue("expected the fixture set, found ${files.size}", files.size >= 35)
        val seenTypes = mutableSetOf<String>()
        for (file in files) {
            val text = file.readText(Charsets.UTF_8)
            val expectedType = file.name.substringBefore('.')
            assertEquals("${file.name}: type member", expectedType, JSONObject(text).getString("type"))
            val decoded = SimHubProtocol.parse(text)
            if (decoded !is SimHubParseResult.Ok) fail("${file.name}: expected a valid message, got $decoded")
            val message = (decoded as SimHubParseResult.Ok).message
            assertEquals("${file.name}: decoded type", expectedType, message.type)
            assertEquals("${file.name}: class", EXPECTED_CLASSES.getValue(expectedType), message::class.java)

            val line = SimHubProtocol.encode(message)
            assertFalse("${file.name}: encoded line contains a newline", line.contains('\n') || line.contains('\r'))
            assertEquals("${file.name}: decode(encode(x)) == x", SimHubParseResult.Ok(message), SimHubProtocol.parse(line))
            assertJsonEquivalent(file.name, JSONObject(text), JSONObject(line))
            seenTypes += expectedType
        }
        assertEquals("every protocol-1 type has a fixture", EXPECTED_CLASSES.keys, seenTypes)
    }

    @Test fun wireLinesAreAcceptedWithCarriageReturnAndNewline() {
        for (file in validFiles) {
            val oneLine = JSONObject(file.readText(Charsets.UTF_8)).toString()
            val expected = SimHubProtocol.parse(oneLine)
            assertEquals(file.name, expected, SimHubProtocol.parse("$oneLine\r\n"))
            assertEquals(file.name, expected, SimHubProtocol.parse("$oneLine\n"))
        }
    }

    @Test fun everyInvalidFixtureIsRejected() {
        val files = invalidFiles
        assertTrue("expected the invalid set, found ${files.size}", files.size >= 28)
        for (file in files) {
            val result = SimHubProtocol.parse(file.readText(Charsets.UTF_8))
            assertFalse("${file.name}: must not decode, got $result", result is SimHubParseResult.Ok)
            assertNull("${file.name}: parseOrNull", SimHubProtocol.parseOrNull(file.readText(Charsets.UTF_8)))
            val name = file.name.removeSuffix(".json")
            when (name) {
                "unknown-type" -> assertEquals(SimHubParseResult.Unknown("selfDestruct"), result)
                "truncated", "not-an-object", "missing-type", "type-not-string" ->
                    assertNull("$name: no refType", (result as SimHubParseResult.Malformed).refType)
                else -> {
                    // `<type>-<what is wrong>.json`: the decoder knows the type and reports it for badMessage.
                    val refType = name.substringBefore('-')
                    assertEquals("$name: refType", refType, (result as SimHubParseResult.Malformed).refType)
                }
            }
        }
    }

    @Test fun audioHeaderVectorsEncodeAndDecodeByteForByte() {
        val vectors = JSONObject(File(fixtures, AUDIO_HEADER).readText(Charsets.UTF_8))
        assertEquals(AudioHeader.SIZE, vectors.getInt("headerSize"))
        val valid = vectors.getJSONArray("valid")
        assertTrue(valid.length() >= 6)
        for (index in 0 until valid.length()) {
            val vector = valid.getJSONObject(index)
            val name = vector.getString("name")
            val direction = vector.direction()
            val fields = vector.getJSONObject("header")
            val header = AudioHeader(
                seq = fields.getInt("seq"),
                stream = AudioStream.fromCode(fields.getInt("streamType"))!!,
                start = fields.getInt("flags") and AudioHeader.FLAG_START != 0,
                timestamp = fields.getLong("timestamp"),
                sampleRateHz = fields.getInt("sampleRateHz"),
                channels = fields.getInt("channels"),
                format = AudioFormat.fromCode(fields.getInt("format"))!!,
            )
            assertEquals(name, fields.getString("stream"), header.stream.wire)
            val samplesJson = vector.getJSONArray("samples")
            val samples = ShortArray(samplesJson.length()) { samplesJson.getInt(it).toShort() }

            assertEquals("$name header", vector.getString("headerHex"), SimHubAudioCodec.encodeHeader(header).hex())
            val datagram = SimHubAudioCodec.encodeSamples(header, samples)
            assertEquals("$name datagram", vector.getString("datagramHex"), datagram.hex())
            assertEquals("$name payload", vector.getString("payloadHex"), datagram.copyOfRange(AudioHeader.SIZE, datagram.size).hex())

            val decoded = SimHubAudioCodec.decode(vector.getString("datagramHex").unhex(), direction)
                ?: fail("$name: decode rejected a valid datagram") as Nothing
            assertEquals("$name direction", direction, decoded.header.stream.direction)
            // streamType fixes the direction (§10.2): read the other way, the same bytes are dropped.
            val other = if (direction == AudioDirection.PC_TO_TABLET) AudioDirection.TABLET_TO_PC else AudioDirection.PC_TO_TABLET
            assertNull("$name read the other way", SimHubAudioCodec.decode(vector.getString("datagramHex").unhex(), other))
            assertEquals("$name decoded header", header, decoded.header)
            assertEquals("$name sampleRate field", fields.getInt("sampleRateField"), decoded.header.sampleRateHz / 100)
            assertEquals("$name frames", vector.getInt("frames"), decoded.frames)
            assertArrayEquals("$name samples", samples, decoded.samples())
        }
        val opus = vectors.getJSONArray("opus")
        assertTrue(opus.length() >= 3)
        for (index in 0 until opus.length()) {
            val vector = opus.getJSONObject(index)
            val name = vector.getString("name")
            val fields = vector.getJSONObject("header")
            val header = AudioHeader(
                seq = fields.getInt("seq"),
                stream = AudioStream.fromCode(fields.getInt("streamType"))!!,
                start = fields.getInt("flags") and AudioHeader.FLAG_START != 0,
                timestamp = fields.getLong("timestamp"),
                sampleRateHz = fields.getInt("sampleRateHz"),
                channels = fields.getInt("channels"),
                format = AudioFormat.fromCode(fields.getInt("format"))!!,
            )
            assertEquals(name, AudioFormat.OPUS, header.format)
            val payload = vector.getString("payloadHex").unhex()
            assertEquals("$name header", vector.getString("headerHex"), SimHubAudioCodec.encodeHeader(header).hex())
            assertEquals("$name datagram", vector.getString("datagramHex"), SimHubAudioCodec.encode(header, payload).hex())
            // The frames come from the TOC byte alone: no decoder on either side.
            assertEquals("$name frames", vector.getInt("frames"), OpusPacket.frames(payload, sampleRate = header.sampleRateHz))

            val decoded = SimHubAudioCodec.decode(vector.getString("datagramHex").unhex(), AudioDirection.TABLET_TO_PC)
                ?: fail("$name: decode rejected a valid opus datagram") as Nothing
            assertEquals("$name decoded header", header, decoded.header)
            assertEquals("$name frames", vector.getInt("frames"), decoded.frames)
            assertArrayEquals("$name payload", payload, decoded.payload)
        }
        val invalid = vectors.getJSONArray("invalid")
        assertTrue(invalid.length() >= 12)
        for (index in 0 until invalid.length()) {
            val vector = invalid.getJSONObject(index)
            assertNull(
                "${vector.getString("name")}: ${vector.getString("reason")}",
                SimHubAudioCodec.decode(vector.getString("datagramHex").unhex(), vector.direction()),
            )
        }
    }

    @Test fun reservedHeaderFlagBitsAreIgnoredOnDecode() {
        val datagram = "000001ff0000000001e002010100ffff".unhex()
        val decoded = SimHubAudioCodec.decode(datagram, AudioDirection.TABLET_TO_PC)!!
        assertTrue(decoded.header.start)
        assertEquals("000001010000000001e00201", SimHubAudioCodec.encodeHeader(decoded.header).hex())
    }

    @Test fun strictTypingAndTrailingGarbage() {
        assertMalformed("""{"type":"hello","tabletId":"t","name":"n","appVersion":"1","protocol":1.0}""", "hello")
        assertMalformed("""{"type":"heartbeat","seq":-1}""", "heartbeat")
        assertMalformed("""{"type":"heartbeat","seq":4294967296}""", "heartbeat")
        assertMalformed("""{"type":"state","dashboardUrl":"ftp://x/y","audio":{"enabled":true,"port":1,"formats":[]}}""", "state")
        assertMalformed("""{"type":"pairResult","ok":false,"reason":"denied","token":"abc"}""", "pairResult")
        assertMalformed("""{"type":"error","code":"unsupportedProtocol","fatal":true}""", "error")
        assertMalformed("""{"type":"command","command":"rewind"}""", "command")
        assertMalformed("""{"type":"welcome","hostId":"h","name":"n","version":"1","protocol":1}""", "welcome")
        val trailing = SimHubProtocol.parse("""{"type":"heartbeat"} {"type":"heartbeat"}""")
        assertTrue(trailing is SimHubParseResult.Malformed)
        assertEquals(
            SimHubParseResult.Ok(SimHubMessage.Heartbeat(4294967295L)),
            SimHubProtocol.parse("""{"type":"heartbeat","seq":4294967295,"future":{"x":1}}"""),
        )
    }

    @Test fun telemetryDegradesBadMembersToNull() {
        val result = SimHubProtocol.parse(
            """{"type":"telemetry","speedMps":-3,"gear":"S","heading":360,"lat":"50","lon":6.9,"night":1,"rpm":7000,"trackName":5}""",
        )
        assertEquals(
            SimHubParseResult.Ok(SimHubMessage.Telemetry(lon = 6.9, rpm = 7000.0)),
            result,
        )
    }

    @Test fun commandActionIsIgnoredForNonMediaCommands() {
        assertEquals(
            SimHubParseResult.Ok(SimHubMessage.Command(SimHubCommand.ShowCarPlay)),
            SimHubProtocol.parse("""{"type":"command","command":"showCarPlay","action":"shuffle"}"""),
        )
    }

    @Test fun nonFiniteTelemetryIsOmittedAndTokensAreRedacted() {
        val line = SimHubProtocol.encode(SimHubMessage.Telemetry(speedMps = Double.NaN, rpm = Double.POSITIVE_INFINITY, gameRunning = true))
        assertEquals("""{"type":"telemetry","gameRunning":true}""", line)
        val result = SimHubMessage.PairResult(ok = true, token = "q3Z2b0x9V1mN8pR4sT6uW7yA5cE1gH3jK2lM0nO9pQ8")
        assertFalse(result.toString().contains("b0x9"))
        assertFalse(SimHubMessage.PairRequest.resume(result.token!!).toString().contains("b0x9"))
        assertFalse(SimHubMessage.PairRequest.pin("048291").toString().contains("048291"))
    }

    @Test fun statusNavIsOptionalAndArtworkStaysWithinOneLine() {
        val status = SimHubMessage.Status(phoneConnected = true, screen = Screen.CARPLAY, nowPlaying = null)
        assertFalse("no nav member without route guidance", SimHubProtocol.encode(status).contains("nav"))
        val nav = status.copy(nav = NavStatus(maneuver = "leftTurn"))
        assertEquals(
            """{"type":"status","phoneConnected":true,"screen":"carplay","nowPlaying":null,"nav":{"maneuver":"leftTurn"}}""",
            SimHubProtocol.encode(nav),
        )
        assertEquals(SimHubParseResult.Ok(nav), SimHubProtocol.parse(SimHubProtocol.encode(nav)))
        // §6.7.1: a bad `nav` never makes the status invalid; the rest of the status is used.
        // A `nav` without a valid `maneuver` is dropped (nav absent), the status stays valid.
        assertEquals(
            SimHubParseResult.Ok(status),
            SimHubProtocol.parse("""{"type":"status","phoneConnected":true,"screen":"carplay","nowPlaying":null,"nav":{"distanceM":5}}"""),
        )
        assertEquals(
            SimHubParseResult.Ok(status),
            SimHubProtocol.parse("""{"type":"status","phoneConnected":true,"screen":"carplay","nowPlaying":null,"nav":{"distanceM":350,"road":"B258"}}"""),
        )
        // A wrong-type member is dropped; the rest of a valid `nav` is kept.
        assertEquals(
            SimHubParseResult.Ok(status.copy(nav = NavStatus(maneuver = "leftTurn"))),
            SimHubProtocol.parse("""{"type":"status","phoneConnected":true,"screen":"carplay","nowPlaying":null,"nav":{"maneuver":"leftTurn","distanceM":"x"}}"""),
        )
        // A `nav` that is not an object means no route guidance; the status stays valid.
        assertEquals(
            SimHubParseResult.Ok(status),
            SimHubProtocol.parse("""{"type":"status","phoneConnected":true,"screen":"carplay","nowPlaying":null,"nav":5}"""),
        )
        assertMalformed("""{"type":"artwork","mime":"image/jpeg"}""", "artwork")
        assertFalse(SimHubMessage.Artwork("image/jpeg", "QUJD".repeat(1_000)).toString().contains("QUJD"))
    }

    @Test fun micMessagesAndStateMic() {
        assertEquals(
            """{"type":"micStart","streamType":4,"format":"pcm_s16le","sampleRate":24000,"channels":1,"port":23713}""",
            SimHubProtocol.encode(SimHubMessage.MicStart(sampleRate = 24_000)),
        )
        assertEquals("""{"type":"micStop","streamType":4}""", SimHubProtocol.encode(SimHubMessage.MicStop()))
        assertMalformed("""{"type":"micStop","streamType":3}""", "micStop")
        assertMalformed("""{"type":"micStart","streamType":4,"format":"opus","sampleRate":16000,"channels":1,"port":23713}""", "micStart")
        assertMalformed("""{"type":"micStart","streamType":4,"format":"pcm_s16le","sampleRate":16000,"channels":1,"port":0}""", "micStart")
        // `mic` is a datagram stream only: audioStart and audioStop cannot name it.
        assertMalformed("""{"type":"audioStart","stream":"mic","format":"pcm_s16le","sampleRate":16000,"channels":1}""", "audioStart")
        assertMalformed("""{"type":"audioStop","stream":"mic"}""", "audioStop")
        val state = SimHubProtocol.parseOrNull(
            """{"type":"state","dashboardUrl":null,"audio":{"enabled":true,"port":23712,"formats":["pcm_s16le"]}}""",
        ) as SimHubMessage.State
        assertNull("no state.mic without the feature", state.mic)
        assertFalse(SimHubProtocol.encode(state).contains("mic"))
    }

    @Test fun versionCompatibility() {
        assertTrue(SimHubProtocol.isCompatible(1, 1))
        assertTrue(SimHubProtocol.isCompatible(1, 3))
        assertFalse(SimHubProtocol.isCompatible(2, 3))
    }

    private fun assertMalformed(line: String, refType: String) {
        val result = SimHubProtocol.parse(line)
        assertTrue("$line -> $result", result is SimHubParseResult.Malformed)
        assertEquals(line, refType, (result as SimHubParseResult.Malformed).refType)
    }

    /** README rule: members compared by value (6120 == 6120.0), order ignored; absent == null (§3). */
    private fun assertJsonEquivalent(path: String, expected: Any?, actual: Any?) {
        val e = expected.takeUnless { it === JSONObject.NULL }
        val a = actual.takeUnless { it === JSONObject.NULL }
        when {
            e is JSONObject && a is JSONObject -> {
                val names = (e.keys().asSequence() + a.keys().asSequence()).toSet()
                for (name in names) assertJsonEquivalent("$path.$name", e.opt(name), a.opt(name))
            }
            e is JSONArray && a is JSONArray -> {
                assertEquals("$path length", e.length(), a.length())
                for (index in 0 until e.length()) assertJsonEquivalent("$path[$index]", e.opt(index), a.opt(index))
            }
            e is Number && a is Number -> assertEquals(path, e.toDouble(), a.toDouble(), 0.0)
            else -> assertEquals(path, e, a)
        }
    }

    /** The fixtures README: a vector flows tablet → plugin unless its `direction` says `pcToTablet`. */
    private fun JSONObject.direction(): AudioDirection =
        if (optString("direction") == "pcToTablet") AudioDirection.PC_TO_TABLET else AudioDirection.TABLET_TO_PC

    private fun ByteArray.hex(): String = joinToString("") { "%02x".format(it.toInt() and 0xFF) }

    private fun String.unhex(): ByteArray = ByteArray(length / 2) { substring(it * 2, it * 2 + 2).toInt(16).toByte() }

    companion object {
        private const val AUDIO_HEADER = "audio-header.json"

        private val EXPECTED_CLASSES: Map<String, Class<out SimHubMessage>> = mapOf(
            "beacon" to SimHubMessage.Beacon::class.java,
            "hello" to SimHubMessage.Hello::class.java,
            "welcome" to SimHubMessage.Welcome::class.java,
            "pairRequest" to SimHubMessage.PairRequest::class.java,
            "pairResult" to SimHubMessage.PairResult::class.java,
            "heartbeat" to SimHubMessage.Heartbeat::class.java,
            "state" to SimHubMessage.State::class.java,
            "status" to SimHubMessage.Status::class.java,
            "command" to SimHubMessage.Command::class.java,
            "telemetry" to SimHubMessage.Telemetry::class.java,
            "error" to SimHubMessage.Error::class.java,
            "audioStart" to SimHubMessage.AudioStart::class.java,
            "audioStop" to SimHubMessage.AudioStop::class.java,
            "artwork" to SimHubMessage.Artwork::class.java,
            "micStart" to SimHubMessage.MicStart::class.java,
            "micStop" to SimHubMessage.MicStop::class.java,
        )

        /** Gradle runs unit tests in the module directory; walk up to the repository root. */
        private fun locateFixtures(): File {
            var dir: File? = File("").absoluteFile
            while (dir != null) {
                val candidate = File(dir, "protocol/fixtures")
                if (File(candidate, "README.md").isFile) return candidate
                dir = dir.parentFile
            }
            error("protocol/fixtures not found above ${File("").absolutePath}")
        }
    }
}

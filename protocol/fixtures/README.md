# Protocol fixtures

Golden samples for [`docs/protocol.md`](../../docs/protocol.md). The spec is the authority; these files
are its examples in a form tests can read.

Both test suites MUST read every file in this directory:

- the Kotlin test in `:shared` (`shared/src/test/java/com/shilapi/xcertplay/simhub/ProtocolFixturesTest.kt`, #26);
- the xunit test in `plugin/RigPlay.Tests` (`ProtocolFixturesTests.cs`, #20).

A test enumerates the files rather than listing them, so a new fixture is picked up without editing
either test. A change to a message in the spec changes its fixture in the same commit.

## Valid message samples (`*.json` in this directory)

- File name: `<type>.json` for the main sample, `<type>.<variant>.json` for others. The part before
  the first `.` equals the top-level `"type"` member. Tests assert this.
- Each file is one JSON object, pretty-printed for reading. On the wire the same object is one line.
- A test MUST decode each file into its message type without error, encode it again, decode that, and
  get an equal value. Members are compared by value, not by text: `6120` and `6120.0` are equal, and
  member order does not matter.
- `beacon.json` is a UDP payload, not a control-channel line; it follows the same rules.

| Type | Files |
|---|---|
| `beacon` | `beacon.json` |
| `hello` | `hello.json` |
| `welcome` | `welcome.json` |
| `pairRequest` | `pairRequest.json` (Start), `pairRequest.pin.json`, `pairRequest.token.json` |
| `pairResult` | `pairResult.json` (ok), `pairResult.pinRequired.json`, `pairResult.wrongPin.json`, `pairResult.denied.json`, `pairResult.tokenInvalid.json` |
| `heartbeat` | `heartbeat.json` |
| `state` | `state.json`, `state.minimal.json` (required members only), `state.serverDown.json`, `state.opus.json` (Opus preferred), `state.mic.json` (PC microphone available) |
| `status` | `status.json`, `status.idle.json` (no phone), `status.liveStream.json` (null duration), `status.nav.json` (route guidance) |
| `command` | `command.json` (media next), `command.playPause.json`, `command.previous.json`, `command.siri.json`, `command.showDashboard.json`, `command.showCarPlay.json` |
| `telemetry` | `telemetry.json` (every field), `telemetry.partial.json` (absent and null fields) |
| `error` | `error.json` (unsupportedProtocol), `error.notPaired.json`, `error.shutdown.json` |
| `audioStart` | `audioStart.json` (media 48 kHz stereo), `audioStart.telephony.json` (16 kHz mono), `audioStart.opus.json` (Opus) |
| `audioStop` | `audioStop.json` |
| `artwork` | `artwork.json` (a tiny JPEG) |
| `micStart` | `micStart.json` (16 kHz mono to port 23713) |
| `micStop` | `micStop.json` |

## Audio header vectors (`audio-header.json`)

Not a control message. Its `"type"` is `"audio-header"` only so that the file-name rule above holds
for every file; that value never appears on the wire. Message-decoding tests skip it; the audio codec
tests read it.

- `valid[]`: each entry has the header fields, the PCM samples, and `headerHex`, `payloadHex` and
  `datagramHex` (lower-case hex, no separators). A test MUST encode the fields and samples to exactly
  `datagramHex`, and decode `datagramHex` back to the same fields and samples.
- `opus[]`: Opus datagrams (format 2, §10.4). Each entry has the header fields, `payloadHex` (one Opus
  packet as an encoder produced it), `frames` (what the packet decodes to at the header's sample rate,
  from its TOC byte) and `headerHex` / `datagramHex`. A test MUST encode the header and payload to
  exactly `datagramHex`, decode it back to the same fields and payload, and derive `frames` from the
  packet without an Opus decoder.
- `invalid[]`: each entry has a `datagramHex` and a `reason`. A test MUST reject each one.
- `direction` (optional on every entry): `tabletToPc` (the default) for datagrams a plugin receives, which
  carry `streamType` 1–3; `pcToTablet` for datagrams a tablet receives on its microphone port, which carry
  `streamType` 4 only (spec §10.4). A test decodes each vector in its direction: `streamType-mic-from-tablet`
  and `media-to-tablet` are the same kind of header read the wrong way.
- Header fields are big-endian; the PCM payload is little-endian. `media-44k1-midstream` exists to
  catch a codec that gets the header byte order wrong.

## Invalid samples (`invalid/`)

A decoder MUST NOT return a known, valid message for any of these. What it returns instead (an error
value, an "unknown" message, an exception) is up to each implementation; at runtime the session logs
the line and keeps going, as in spec §14.2. `truncated.json` is deliberately not valid JSON.

| File | Rejected because | Spec |
|---|---|---|
| `truncated.json` | Not valid JSON. | §14.2 |
| `not-an-object.json` | Top level is an array. | §3 |
| `missing-type.json` | No `type` member. | §3 |
| `type-not-string.json` | `type` is a number. | §3 |
| `unknown-type.json` | `type` is not a message of protocol 1. Ignored without reply at runtime. | §14.2 |
| `hello-missing-tabletId.json` | Required `tabletId` missing. | §6.1 |
| `hello-protocol-zero.json` | `protocol` must be ≥ 1. | §7.1 |
| `hello-protocol-string.json` | `protocol` must be an integer, not a string. | §7.1 |
| `hello-minProtocol-above-protocol.json` | `minProtocol` greater than `protocol`. | §7.1 |
| `beacon-missing-controlPort.json` | Required `controlPort` missing. | §4.3 |
| `pairRequest-token-and-pin.json` | `token` and `pin` together. | §6.3 |
| `pairRequest-pin-five-digits.json` | `pin` must be exactly 6 digits. | §6.3 |
| `pairResult-ok-without-token.json` | `ok: true` requires `token`. | §6.4 |
| `pairResult-unknown-reason.json` | `reason` value `busy` is not defined. | §6.4 |
| `state-missing-audio.json` | Required `audio` missing. | §6.6 |
| `status-unknown-screen.json` | `screen` value `projector` is not defined. | §6.7 |
| `status-nowPlaying-missing-playing.json` | Required `nowPlaying.playing` missing. | §6.7 |
| `command-media-without-action.json` | `command: media` requires `action`. | §6.8 |
| `command-unknown-action.json` | `action` value `shuffle` is not defined. | §6.8 |
| `audioStart-unknown-stream.json` | `stream` value `navigation` is not defined. | §6.11 |
| `audioStart-sampleRate-22050.json` | `sampleRate` must be a multiple of 100. | §6.11 |
| `error-missing-code.json` | Required `code` missing. | §6.10 |
| `artwork-mime-without-base64.json` | Required `base64` is `null`. | §6.14 |
| `micStart-streamType-media.json` | `streamType` must be 4 (`mic`). | §6.13 |
| `micStart-stereo.json` | `channels` must be 1: the microphone is mono. | §6.13 |
| `micStart-format-opus.json` | `format` must be `pcm_s16le`: the microphone is PCM only; Opus flows tablet → plugin. | §6.13, §10.5 |
| `micStart-missing-port.json` | Required `port` missing. | §6.13 |
| `micStart-sampleRate-11025.json` | `sampleRate` must be a multiple of 100. | §6.13 |
| `micStop-missing-streamType.json` | Required `streamType` missing. | §6.13 |
| `state-mic-enabled-string.json` | `mic.enabled` must be a boolean. | §6.6 |

There is no invalid `telemetry` sample: telemetry is validated field by field and a bad field becomes
`null` rather than rejecting the message (§6.9). The plugin is also lenient with `status.nav` (§6.7.1), so
there is no invalid `nav` sample.

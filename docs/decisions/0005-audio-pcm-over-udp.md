# ADR 0005: Phone audio goes to the PC as PCM over UDP

**Date:** 2026-10-03
**Status:** Accepted

## Context

The iPhone sends its audio (media, Siri, calls) to the tablet over AirPlay, and the app already
decodes it to PCM. On a rig the user listens through the PC's speakers or headset, not the tablet's
speaker. Options considered:

- **Play on the tablet** and let the user cable it into the PC. Tablet speakers are poor and an extra
  cable defeats the point.
- **Bluetooth from the phone to the PC.** Conflicts with CarPlay, which owns the phone's audio route.
- **Stream over TCP**, on the control connection or a second one. A lost segment on Wi-Fi stalls
  everything behind it; for live audio, a short gap is better than a growing delay.
- **RTP.** Fits, but its header carries nothing we need beyond what a fixed 12-byte header carries, and
  using it properly brings RTCP and payload-type negotiation.
- **Encode to Opus first.** Cuts bandwidth from about 1.5 Mbit/s to under 0.2 Mbit/s, but adds CPU on
  the tablet, a decoder in the plugin, and latency. On a home LAN the bandwidth is not the problem.

## Decision

The tablet sends uncompressed s16le PCM to the plugin over UDP. Each datagram has a 12-byte header:
sequence number, stream type, flags, a sample-clock timestamp, sample rate in units of 100 Hz, channel
count and format. Streams are announced and ended with `audioStart`/`audioStop` on the control channel,
which also tells the plugin which source address to trust. The plugin runs a jitter buffer per stream,
fills gaps with silence, mixes the streams and plays them through WASAPI on the device the user picks.
Details are in [`docs/protocol.md`](../protocol.md), section 10.

The header has a `format` field, and value 2 is reserved for Opus, so compression can be added
without a new protocol version once the plugin can decode it. [ADR 0006](0006-opus-optional.md) adds
it as an option the plugin turns on; PCM stays the default.

## Consequences

Audio is simple to produce and to consume, and a lost datagram costs a few milliseconds of sound. At
48 kHz stereo the stream is about 1.6 Mbit/s and 200 datagrams per second, which a home Wi-Fi network
handles but a congested one may not; Opus is the follow-up for that case. Latency is the jitter buffer
(60–120 ms) plus the network, acceptable for music and calls but not for lip-sync, which nothing here
needs.

The microphone stays on the tablet in protocol 1. Using the PC microphone reuses the same header in the
other direction (`micStart`/`micStop`, stream type 4), reserved but not specified yet. Audio is not
encrypted or authenticated beyond the source address check.

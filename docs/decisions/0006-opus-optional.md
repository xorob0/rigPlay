# ADR 0006: Opus is an option the plugin turns on, not the default

**Date:** 2026-10-03
**Status:** Accepted (extends [0005](0005-audio-pcm-over-udp.md))

## Context

[ADR 0005](0005-audio-pcm-over-udp.md) sends phone audio to the PC as uncompressed PCM and reserves
header format 2 for Opus "once the plugin can decode it". The question came up again after the first
rig test: is PCM the best way, or should the stream be compressed?

What PCM costs on this link: about 1.5 Mbit/s and 200 datagrams per second at 48 kHz stereo. On a
home LAN that is not the problem; the first rig test showed audio cutting out at 0 % loss because
Wi-Fi power save and queueing stalled the stream, not because of its size. What compression would
buy: 50 datagrams per second of about 240 bytes, which survive a weak or shared 2.4 GHz link where
200 datagrams of 972 bytes do not, and Opus's own loss concealment if it is ever used. What it costs:
an encoder on the tablet (Android has one, software, from API 29), a decoder in the plugin (SimHub
ships none, so one more DLL), 20 ms of packet delay plus the encoder's look-ahead, and a resample
for sources at 44.1 kHz, which Opus does not take.

Candidates for the compressed format, if any: Opus is royalty free, made for low delay, decodes
anything from 8 to 48 kHz and is on every Android device. AAC-LC works on 1024-sample frames (21 ms
at 48 kHz) plus encoder and decoder priming, and its managed decoders are scarce. SBC is a Bluetooth
codec with poor quality per bit and no tooling on either side.

Where to decide: the plugin already tells the tablet what it accepts (`state.audio.formats`, most
preferred first), so the plugin's settings page is where every other audio choice lives (device,
volume, port). A tablet-side switch would be a second place to look, and a tablet that cannot encode
can fall back on its own.

## Decision

Opus is added as an optional format, off by default on the plugin. PCM stays the default and the
protocol's baseline: a plugin always lists `pcm_s16le`, and lists `opus` before it only while its
**Opus compression** setting is on. The tablet sends the first format in the list it knows; a tablet
without an Opus encoder sends PCM and counts the fallback. No protocol version or feature string is
needed: a plugin that does not list `opus` is never sent it. Details are in
[`docs/protocol.md`](../protocol.md) §10.4.

The plugin decodes with Concentus, the managed port of libopus, shipped as `Concentus.dll` next to
`RigPlay.dll`. A plugin installed without it still loads; Opus is then unavailable and the page says
so. The tablet encodes with Android's `MediaCodec` Opus encoder in 20 ms frames, at 96 kbit/s for
stereo and 48 kbit/s for mono, resampling 44.1 kHz sources to 48 kHz.

The receiver decodes each packet as it arrives and places the PCM in the same jitter buffer as a PCM
stream; a lost packet is silence. Opus's packet loss concealment and in-band FEC are not used.

## Consequences

Nothing changes for a user who does not touch the setting. A user on a weak Wi-Fi link has a switch
to try, and the rigPlay page shows which format each stream arrived in and how many packets did not
decode. The install is two DLLs instead of one, and the release zip, the install guide and the
licence notices say so.

Opus costs the tablet a software encoder (small at complexity 5) and the PC a managed decoder, both
negligible beside video decoding. A 44.1 kHz source loses a little at the top of the band to the
linear resampler before encoding; a better resampler is a follow-up if anyone hears it. Packet loss
concealment is the other follow-up: it needs decoding in play-out order rather than on receipt.

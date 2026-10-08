// SPDX-License-Identifier: GPL-3.0-only
// OpusTests.cs: the optional Opus format (docs/protocol.md §10.4) end to end on the plugin side: packets encoded
// with Concentus go through AudioReceiver as opus datagrams and come out of the jitter buffer as PCM; the
// setting gates audioStart and state.audio.formats; a packet the decoder cannot decode is counted, not played.
using System;
using System.Collections.Generic;
using System.Net;
using Concentus;
using Concentus.Enums;
using RigPlayPlugin.Audio;
using Xunit;

namespace RigPlayPlugin.Tests.Audio
{
    public class OpusTests
    {
        private sealed class FakeSink : IAudioSink
        {
            public readonly List<AudioStream> Active = new List<AudioStream>();
            public void StreamActivated(AudioStream stream) { Active.Add(stream); }
            public void StreamDeactivated(AudioStream stream) { Active.Remove(stream); }
        }

        private readonly FakeSink sink = new FakeSink();

        private AudioReceiver NewReceiver(bool opus)
        {
            return new AudioReceiver(0, sink, 20, 200, 180, () => 1000) { OpusEnabled = opus };
        }

        /// <summary>Sends <paramref name="packets"/> 20 ms Opus packets of a 440 Hz tone (or silence) at the rate, as the tablet would.</summary>
        private static List<byte[]> ToneDatagrams(int rate, int channels, int packets, bool tone = true, AudioStreamType stream = AudioStreamType.Media)
        {
            var encoder = OpusCodecFactory.CreateEncoder(rate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
            encoder.Bitrate = channels == 2 ? 96000 : 48000;
            var frame = rate / 50;
            var pcm = new short[frame * channels];
            var packet = new byte[1275];
            var datagrams = new List<byte[]>();
            for (var k = 0; k < packets; k++)
            {
                for (var i = 0; i < frame; i++)
                {
                    var sample = tone ? (short)(8000 * Math.Sin(2 * Math.PI * 440 * (k * frame + i) / rate)) : (short)0;
                    for (var c = 0; c < channels; c++) pcm[i * channels + c] = sample;
                }
                var n = encoder.Encode(pcm, frame, packet, packet.Length);
                var header = AudioHeader.Create((ushort)k, stream, k == 0, (uint)(k * frame), rate, channels, AudioFormat.Opus);
                var datagram = new byte[AudioHeader.Size + n];
                header.Write(datagram, 0);
                Array.Copy(packet, 0, datagram, AudioHeader.Size, n);
                datagrams.Add(datagram);
            }
            return datagrams;
        }

        [Fact]
        public void TheDecoderIsAvailableInTheTests()
        {
            Assert.True(OpusSupport.Available, OpusSupport.UnavailableReason);
            Assert.Equal("", OpusSupport.UnavailableReason);
        }

        [Fact]
        public void FormatsListOpusFirstOnlyWhenEnabled()
        {
            Assert.Equal(new List<string> { "pcm_s16le" }, NewReceiver(false).SupportedFormats());
            Assert.Equal(new List<string> { "opus", "pcm_s16le" }, NewReceiver(true).SupportedFormats());
        }

        [Fact]
        public void AudioStartInOpusNeedsTheSettingAndAnOpusRate()
        {
            var off = NewReceiver(false);
            Assert.False(off.OnAudioStart("media", "opus", 48000, 2));
            Assert.True(off.OnAudioStart("media", "pcm_s16le", 48000, 2));

            var on = NewReceiver(true);
            Assert.False(on.OnAudioStart("media", "opus", 44100, 2)); // not an Opus rate
            Assert.False(on.OnAudioStart("media", "flac", 48000, 2));
            Assert.True(on.OnAudioStart("media", "opus", 48000, 2));
            var stream = on.GetStream(AudioStreamType.Media);
            Assert.Equal(AudioFormat.Opus, stream.Format);
            Assert.NotNull(stream.Decoder);
            Assert.True(on.OnAudioStart("alt", "opus", 16000, 1));
            Assert.True(on.OnAudioStart("telephony", "pcm_s16le", 16000, 1));
            Assert.Null(on.GetStream(AudioStreamType.Telephony).Decoder);
        }

        [Fact]
        public void OpusDatagramsAreDecodedIntoTheJitterBufferAsPcm()
        {
            var r = NewReceiver(true);
            Assert.True(r.OnAudioStart("media", "opus", 48000, 2));
            var datagrams = ToneDatagrams(48000, 2, 10);
            foreach (var d in datagrams)
            {
                Assert.True(d.Length < 400, "a 20 ms packet at 96 kbit/s is about 240 bytes, not " + d.Length);
                r.ProcessDatagram(d, d.Length, IPAddress.Loopback);
            }
            var stream = r.GetStream(AudioStreamType.Media);
            Assert.True(stream.Active);
            var c = stream.Buffer.Counters;
            Assert.Equal(10, c.Received);
            Assert.Equal(0, c.Lost);
            Assert.Equal(960 * 10, c.BufferedFrames);
            Assert.True(c.Playing);

            // The tone comes out: not silence, and no sample clipped (the encoder was fed 8000 peak).
            var pcm = new byte[960 * 10 * 4];
            r.ProcessDatagram(datagrams[0], 0, IPAddress.Loopback); // a 0-byte datagram is invalid and changes nothing
            stream.Buffer.Read(pcm, 0, pcm.Length);
            long energy = 0;
            short peak = 0;
            for (var i = 48000 / 50 * 4; i < pcm.Length; i += 2) // skip the first packet: the encoder fades in
            {
                var s = (short)(pcm[i] | (pcm[i + 1] << 8));
                energy += Math.Abs(s);
                if (Math.Abs(s) > peak) peak = (short)Math.Abs(s);
            }
            Assert.True(energy / (pcm.Length / 2) > 2000, "average magnitude " + energy / (pcm.Length / 2));
            Assert.InRange(peak, 6000, 10000);

            r.Tick(1500);
            var stats = r.Stats.Find(AudioStreamType.Media);
            Assert.Equal(AudioFormat.Opus, stats.Format);
            Assert.Equal("48 kHz stereo opus", stats.FormatText);
            Assert.Contains("undecodable 0", stats.ToDisplayString());
            Assert.Equal(1, r.Stats.Invalid);
        }

        [Fact]
        public void MonoSixteenKiloHertzOpusFollowsItsOwnClock()
        {
            var r = NewReceiver(true);
            Assert.True(r.OnAudioStart("alt", "opus", 16000, 1));
            foreach (var d in ToneDatagrams(16000, 1, 5, tone: false, stream: AudioStreamType.Alt)) r.ProcessDatagram(d, d.Length, IPAddress.Loopback);
            var c = r.GetStream(AudioStreamType.Alt).Buffer.Counters;
            Assert.Equal(5, c.Received);
            Assert.Equal(320 * 5, c.BufferedFrames);
        }

        [Fact]
        public void APacketTheDecoderRejectsIsCountedAsInvalid()
        {
            var r = NewReceiver(true);
            Assert.True(r.OnAudioStart("media", "opus", 48000, 2));
            var good = ToneDatagrams(48000, 2, 2);
            r.ProcessDatagram(good[0], good[0].Length, IPAddress.Loopback);
            // TOC code 1 means two frames of equal size, which an odd payload length cannot be: a valid header, an
            // invalid packet. The TOC alone passes the header check, so the decoder is what refuses it.
            var bad = AudioHeader.Create(1, AudioStreamType.Media, false, 960, 48000, 2, AudioFormat.Opus).ToBytes();
            Array.Resize(ref bad, AudioHeader.Size + 4);
            bad[AudioHeader.Size] = 0xF9;
            bad[AudioHeader.Size + 1] = 1;
            bad[AudioHeader.Size + 2] = 2;
            bad[AudioHeader.Size + 3] = 3;
            r.ProcessDatagram(bad, bad.Length, IPAddress.Loopback);
            r.ProcessDatagram(good[1], good[1].Length, IPAddress.Loopback);
            r.Tick(1500);
            var stats = r.Stats.Find(AudioStreamType.Media);
            Assert.Equal(1, stats.DecodeFailures);
            Assert.Equal(1, r.Stats.Invalid);
            Assert.Equal(2, r.GetStream(AudioStreamType.Media).Buffer.Counters.Received);
            Assert.Contains("undecodable 1", stats.ToDisplayString());
        }

        [Fact]
        public void DatagramsInTheOtherFormatAreRejectedAsAMismatch()
        {
            var r = NewReceiver(true);
            Assert.True(r.OnAudioStart("media", "opus", 48000, 2));
            var pcm = AudioHeader.Create(0, AudioStreamType.Media, true, 0, 48000, 2).Encode(new short[480]);
            r.ProcessDatagram(pcm, pcm.Length, IPAddress.Loopback);
            Assert.Equal(0, r.GetStream(AudioStreamType.Media).Buffer.Counters.Received);
            r.Tick(1500);
            Assert.Equal(1, r.Stats.Rejected);
        }

        [Fact]
        public void TheStartFlagFallbackDoesNotStartOpusWhileTheSettingIsOff()
        {
            var r = NewReceiver(false);
            r.AutoStartOnFirstFlag = true;
            var d = ToneDatagrams(48000, 2, 1)[0];
            r.ProcessDatagram(d, d.Length, IPAddress.Loopback);
            Assert.False(r.IsStarted(AudioStreamType.Media));

            var on = NewReceiver(true);
            on.AutoStartOnFirstFlag = true;
            on.ProcessDatagram(d, d.Length, IPAddress.Loopback);
            Assert.True(on.IsStarted(AudioStreamType.Media));
            Assert.Equal(AudioFormat.Opus, on.GetStream(AudioStreamType.Media).Format);
        }

        [Fact]
        public void ARestartInTheOtherFormatReplacesTheDecoder()
        {
            var r = NewReceiver(true);
            Assert.True(r.OnAudioStart("media", "opus", 48000, 2));
            Assert.NotNull(r.GetStream(AudioStreamType.Media).Decoder);
            Assert.True(r.OnAudioStart("media", "pcm_s16le", 48000, 2));
            Assert.Null(r.GetStream(AudioStreamType.Media).Decoder);
            r.OnAudioStop("media");
            Assert.False(r.IsStarted(AudioStreamType.Media));
        }
    }
}

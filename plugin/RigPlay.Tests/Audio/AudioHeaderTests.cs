// SPDX-License-Identifier: GPL-3.0-only
// AudioHeaderTests.cs: the audio datagram codec against every vector in protocol/fixtures/audio-header.json
// (docs/protocol.md §10.2, §10.4): decode, re-encode byte for byte, place Opus packets by their TOC, and reject
// every invalid datagram.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RigPlayPlugin.Audio;
using Xunit;

namespace RigPlayPlugin.Tests.Audio
{
    public class AudioHeaderTests
    {
        private static readonly Lazy<JObject> Fixture = new Lazy<JObject>(() => JObject.Parse(File.ReadAllText(FixturePath("audio-header.json"))));

        /// <summary>protocol/fixtures/&lt;name&gt;, found by walking up from the test binaries to the repository root.</summary>
        internal static string FixturePath(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "protocol", "fixtures", name);
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new FileNotFoundException("protocol/fixtures/" + name + " not found above " + AppContext.BaseDirectory);
        }

        private static byte[] Hex(string hex)
        {
            return Convert.FromHexString(hex);
        }

        private static string ToHex(byte[] bytes)
        {
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        public static IEnumerable<object[]> ValidNames()
        {
            return Fixture.Value["valid"].Select(v => new object[] { (string)v["name"] });
        }

        public static IEnumerable<object[]> InvalidNames()
        {
            return Fixture.Value["invalid"].Select(v => new object[] { (string)v["name"] });
        }

        public static IEnumerable<object[]> OpusNames()
        {
            return Fixture.Value["opus"].Select(v => new object[] { (string)v["name"] });
        }

        private static JToken Vector(string section, string name)
        {
            return Fixture.Value[section].Single(v => (string)v["name"] == name);
        }

        /// <summary>The vector's direction (fixtures README): tablet to plugin unless it says pcToTablet.</summary>
        private static AudioDirection DirectionOf(JToken vector)
        {
            return AudioHeader.ParseDirection((string)vector["direction"]);
        }

        [Fact]
        public void FixtureDescribesTheSameLayout()
        {
            Assert.Equal(AudioHeader.Size, (int)Fixture.Value["headerSize"]);
            Assert.Equal("big-endian", (string)Fixture.Value["byteOrder"]["header"]);
            Assert.Equal("little-endian", (string)Fixture.Value["byteOrder"]["payload"]);
            Assert.Equal(6, Fixture.Value["valid"].Count());
            Assert.Equal(4, Fixture.Value["opus"].Count());
            Assert.Equal(16, Fixture.Value["invalid"].Count());
        }

        [Theory]
        [MemberData(nameof(ValidNames))]
        public void DecodesValidVector(string name)
        {
            var v = Vector("valid", name);
            var h = v["header"];
            var datagram = Hex((string)v["datagramHex"]);

            AudioHeader header;
            Assert.Equal(AudioHeaderError.Ok, AudioHeader.TryParse(datagram, DirectionOf(v), out header));
            Assert.Equal((int)h["seq"], header.Seq);
            Assert.Equal((int)h["streamType"], (int)header.StreamType);
            Assert.Equal((string)h["stream"], AudioHeader.StreamName(header.StreamType));
            Assert.Equal((int)h["flags"], header.Flags);
            Assert.Equal(((int)h["flags"] & 1) == 1, header.IsStart);
            Assert.Equal((uint)h["timestamp"], header.Timestamp);
            Assert.Equal((int)h["sampleRateField"], header.SampleRateField);
            Assert.Equal((int)h["sampleRateHz"], header.SampleRate);
            Assert.Equal((int)h["channels"], header.Channels);
            Assert.Equal((int)h["format"], (int)header.Format);

            var payloadLength = datagram.Length - AudioHeader.Size;
            Assert.Equal((int)v["frames"], payloadLength / header.BlockAlign);
            Assert.Equal((int)v["frames"], header.PayloadFrames(datagram, AudioHeader.Size, payloadLength));
            var samples = AudioHeader.DecodeSamples(datagram, AudioHeader.Size, payloadLength);
            Assert.Equal(v["samples"].Select(s => (short)(int)s).ToArray(), samples);
        }

        [Theory]
        [MemberData(nameof(OpusNames))]
        public void OpusVectorEncodesDecodesAndPlacesByItsToc(string name)
        {
            var v = Vector("opus", name);
            var h = v["header"];
            var datagram = Hex((string)v["datagramHex"]);
            var payload = Hex((string)v["payloadHex"]);

            AudioHeader header;
            Assert.Equal(AudioHeaderError.Ok, AudioHeader.TryParse(datagram, out header));
            Assert.Equal(AudioFormat.Opus, header.Format);
            Assert.Equal((int)h["seq"], header.Seq);
            Assert.Equal((string)h["stream"], AudioHeader.StreamName(header.StreamType));
            Assert.Equal((uint)h["timestamp"], header.Timestamp);
            Assert.Equal((int)h["sampleRateHz"], header.SampleRate);
            Assert.Equal((int)h["channels"], header.Channels);
            Assert.Equal(((int)h["flags"] & 1) == 1, header.IsStart);
            // The frames come from the TOC byte alone: no decoder is involved.
            Assert.Equal((int)v["frames"], header.PayloadFrames(datagram, AudioHeader.Size, datagram.Length - AudioHeader.Size));
            Assert.Equal((int)v["frames"], OpusToc.Frames(payload, 0, payload.Length, header.SampleRate));

            var created = AudioHeader.Create((ushort)(int)h["seq"], (AudioStreamType)(int)h["streamType"], ((int)h["flags"] & 1) == 1,
                (uint)h["timestamp"], (int)h["sampleRateHz"], (int)h["channels"], AudioFormat.Opus);
            Assert.Equal((string)v["headerHex"], ToHex(created.ToBytes()));
            Assert.Equal(datagram, created.ToBytes().Concat(payload).ToArray());
        }

        [Theory]
        [InlineData(0xF8, 1, 48000, 960)]   // CELT FB 20 ms, code 0
        [InlineData(0xF8, 1, 16000, 320)]
        [InlineData(0xE0, 1, 48000, 120)]   // CELT FB 2.5 ms
        [InlineData(0x18, 1, 48000, 2880)]  // SILK NB 60 ms
        [InlineData(0x79, 1, 48000, 1920)]  // code 1: two 20 ms frames
        [InlineData(0x7A, 1, 48000, 1920)]  // code 2
        [InlineData(0x7B, 3, 48000, 2880)]  // code 3 with a count byte of 3
        public void OpusTocGivesTheFramesOfAPacket(int toc, int countByte, int rate, int frames)
        {
            var packet = toc == 0x7B ? new[] { (byte)toc, (byte)countByte } : new[] { (byte)toc };
            Assert.Equal(frames, OpusToc.Frames(packet, 0, packet.Length, rate));
        }

        [Fact]
        public void OpusTocRejectsMalformedPackets()
        {
            Assert.Equal(0, OpusToc.Frames(new byte[0], 0, 0, 48000));
            Assert.Equal(0, OpusToc.Frames(new byte[] { 0x7B }, 0, 1, 48000));        // code 3 without its count
            Assert.Equal(0, OpusToc.Frames(new byte[] { 0x7B, 0x00 }, 0, 2, 48000));  // count 0
            Assert.Equal(0, OpusToc.Frames(new byte[] { 0x1B, 0x03 }, 0, 2, 48000));  // 3 x 60 ms > 120 ms
        }

        [Theory]
        [MemberData(nameof(ValidNames))]
        public void EncodesValidVectorByteForByte(string name)
        {
            var v = Vector("valid", name);
            var h = v["header"];
            var header = AudioHeader.Create(
                (ushort)(int)h["seq"],
                (AudioStreamType)(int)h["streamType"],
                ((int)h["flags"] & 1) == 1,
                (uint)h["timestamp"],
                (int)h["sampleRateHz"],
                (int)h["channels"],
                (AudioFormat)(int)h["format"]);

            Assert.Equal((string)v["headerHex"], ToHex(header.ToBytes()));
            var samples = v["samples"].Select(s => (short)(int)s).ToArray();
            Assert.Equal((string)v["datagramHex"], ToHex(header.Encode(samples)));
            Assert.Equal((string)v["payloadHex"], ToHex(header.Encode(samples).Skip(AudioHeader.Size).ToArray()));
        }

        [Theory]
        [MemberData(nameof(ValidNames))]
        public void DecodeThenEncodeIsIdentity(string name)
        {
            var v = Vector("valid", name);
            var datagram = Hex((string)v["datagramHex"]);
            AudioHeader header;
            Assert.Equal(AudioHeaderError.Ok, AudioHeader.TryParse(datagram, DirectionOf(v), out header));
            var samples = AudioHeader.DecodeSamples(datagram, AudioHeader.Size, datagram.Length - AudioHeader.Size);
            Assert.Equal(datagram, header.Encode(samples));
        }

        [Theory]
        [MemberData(nameof(InvalidNames))]
        public void RejectsInvalidVector(string name)
        {
            var v = Vector("invalid", name);
            var datagram = Hex((string)v["datagramHex"]);
            AudioHeader header;
            Assert.NotEqual(AudioHeaderError.Ok, AudioHeader.TryParse(datagram, DirectionOf(v), out header));
        }

        [Theory]
        [MemberData(nameof(ValidNames))]
        public void AValidVectorIsRejectedTheOtherWay(string name)
        {
            // streamType fixes the direction (spec §10.2): a microphone datagram is invalid to a plugin, and CarPlay
            // audio is invalid to a tablet's microphone receiver.
            var v = Vector("valid", name);
            var other = DirectionOf(v) == AudioDirection.PcToTablet ? AudioDirection.TabletToPc : AudioDirection.PcToTablet;
            AudioHeader header;
            var expected = other == AudioDirection.TabletToPc ? AudioHeaderError.ReservedStreamType : AudioHeaderError.WrongDirection;
            Assert.Equal(expected, AudioHeader.TryParse(Hex((string)v["datagramHex"]), other, out header));
        }

        [Theory]
        [InlineData("too-short", AudioHeaderError.TooShort)]
        [InlineData("header-only", AudioHeaderError.NoPayload)]
        [InlineData("streamType-zero", AudioHeaderError.InvalidStreamType)]
        [InlineData("streamType-mic-from-tablet", AudioHeaderError.ReservedStreamType)]
        [InlineData("media-to-tablet", AudioHeaderError.WrongDirection)]
        [InlineData("mic-format-opus", AudioHeaderError.ReservedFormat)]
        [InlineData("format-zero", AudioHeaderError.InvalidFormat)]
        [InlineData("format-three", AudioHeaderError.InvalidFormat)]
        [InlineData("channels-three", AudioHeaderError.InvalidChannels)]
        [InlineData("sampleRate-zero", AudioHeaderError.InvalidSampleRate)]
        [InlineData("sampleRate-above-48k", AudioHeaderError.InvalidSampleRate)]
        [InlineData("payload-partial-frame", AudioHeaderError.PartialFrame)]
        [InlineData("opus-header-only", AudioHeaderError.NoPayload)]
        [InlineData("opus-code3-zero-frames", AudioHeaderError.BadOpusPacket)]
        [InlineData("opus-code3-truncated", AudioHeaderError.BadOpusPacket)]
        public void RejectsInvalidVectorForItsReason(string name, AudioHeaderError expected)
        {
            var v = Vector("invalid", name);
            var datagram = Hex((string)v["datagramHex"]);
            AudioHeader header;
            Assert.Equal(expected, AudioHeader.TryParse(datagram, DirectionOf(v), out header));
        }

        [Fact]
        public void IgnoresReservedFlagBits()
        {
            var datagram = Hex("000001fe0000000001e002010100ffff");
            AudioHeader header;
            Assert.Equal(AudioHeaderError.Ok, AudioHeader.TryParse(datagram, out header));
            Assert.False(header.IsStart);
            datagram[3] = 0xff;
            Assert.Equal(AudioHeaderError.Ok, AudioHeader.TryParse(datagram, out header));
            Assert.True(header.IsStart);
        }

        [Fact]
        public void AcceptsMaximumPayloadAndSampleRateBounds()
        {
            var header = AudioHeader.Create(1, AudioStreamType.Media, false, 0, 8000, 2);
            var datagram = header.Encode(new short[AudioHeader.MaxPayloadBytes / 2]);
            AudioHeader parsed;
            Assert.Equal(AudioHeaderError.Ok, AudioHeader.TryParse(datagram, out parsed));
            Assert.Equal(8000, parsed.SampleRate);
            Assert.Equal(AudioHeader.MaxPayloadBytes / 4, (datagram.Length - AudioHeader.Size) / parsed.BlockAlign);
        }

        [Fact]
        public void ParsesAtAnOffset()
        {
            var datagram = Hex((string)Vector("valid", "alt-24k-mono")["datagramHex"]);
            var padded = new byte[datagram.Length + 5];
            Buffer.BlockCopy(datagram, 0, padded, 3, datagram.Length);
            AudioHeader header;
            Assert.Equal(AudioHeaderError.Ok, AudioHeader.TryParse(padded, 3, datagram.Length, out header));
            Assert.Equal(AudioStreamType.Alt, header.StreamType);
            Assert.Equal(AudioHeaderError.TooShort, AudioHeader.TryParse(padded, 3, padded.Length, out header));
        }

        [Theory]
        [InlineData("media", AudioStreamType.Media)]
        [InlineData("alt", AudioStreamType.Alt)]
        [InlineData("telephony", AudioStreamType.Telephony)]
        public void StreamNamesRoundTrip(string name, AudioStreamType type)
        {
            AudioStreamType parsed;
            Assert.True(AudioHeader.TryParseStreamName(name, out parsed));
            Assert.Equal(type, parsed);
            Assert.Equal(name, AudioHeader.StreamName(type));
        }

        [Fact]
        public void RejectsUnknownNamesAndRates()
        {
            AudioStreamType type;
            AudioFormat format;
            Assert.False(AudioHeader.TryParseStreamName("mic", out type));
            Assert.False(AudioHeader.TryParseStreamName("Media", out type));
            Assert.False(AudioHeader.TryParseFormatName("flac", out format));
            Assert.False(AudioHeader.TryParseFormatName("Opus", out format));
            Assert.True(AudioHeader.TryParseFormatName("opus", out format));
            Assert.Equal(AudioFormat.Opus, format);
            Assert.True(AudioHeader.TryParseFormatName("pcm_s16le", out format));
            Assert.True(AudioHeader.IsOpusSampleRate(48000));
            Assert.True(AudioHeader.IsOpusSampleRate(16000));
            Assert.False(AudioHeader.IsOpusSampleRate(44100));
            Assert.False(AudioHeader.IsOpusSampleRate(32000));
            Assert.True(AudioHeader.IsValidSampleRate(44100));
            Assert.True(AudioHeader.IsValidSampleRate(8000));
            Assert.True(AudioHeader.IsValidSampleRate(48000));
            Assert.False(AudioHeader.IsValidSampleRate(22050));
            Assert.False(AudioHeader.IsValidSampleRate(7900));
            Assert.False(AudioHeader.IsValidSampleRate(96000));
        }
    }
}

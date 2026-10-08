// SPDX-License-Identifier: GPL-3.0-only
// ProtocolFixturesTests.cs: the C# conformance test of docs/protocol.md §17. Reads every file in protocol/fixtures
// (copied next to the test assembly): each valid sample decodes, re-encodes to the same value and decodes again to
// an equal message; each sample under invalid/ is rejected; each audio-header.json vector encodes and decodes
// byte for byte.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RigPlayPlugin.Audio;
using RigPlayPlugin.Protocol;
using Xunit;

namespace RigPlayPlugin.Tests
{
    public class ProtocolFixturesTests
    {
        public static string FixturesDir => Path.Combine(AppContext.BaseDirectory, "fixtures");

        public static IEnumerable<object[]> ValidFixtures()
        {
            return Directory.GetFiles(FixturesDir, "*.json", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(f => f != "audio-header.json")
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => new object[] { f });
        }

        public static IEnumerable<object[]> InvalidFixtures()
        {
            return Directory.GetFiles(Path.Combine(FixturesDir, "invalid"))
                .Select(Path.GetFileName)
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => new object[] { f });
        }

        [Fact]
        public void TheFixturesAreThere()
        {
            Assert.True(Directory.Exists(FixturesDir), "fixtures not copied to " + FixturesDir);
            Assert.True(ValidFixtures().Count() >= 30);
            Assert.True(InvalidFixtures().Count() >= 20);
            Assert.True(File.Exists(Path.Combine(FixturesDir, "audio-header.json")));
        }

        [Theory]
        [MemberData(nameof(ValidFixtures))]
        public void AValidSampleDecodesAndRoundTrips(string file)
        {
            var text = File.ReadAllText(Path.Combine(FixturesDir, file));
            var result = MessageCodec.TryDecode(text);
            Assert.True(result.Ok, file + ": " + result.Failure + " " + result.Reason);

            // The part of the file name before the first '.' is the type (fixtures README).
            Assert.Equal(file.Substring(0, file.IndexOf('.')), result.Message.Type);

            var encoded = MessageCodec.Encode(result.Message);
            Assert.DoesNotContain("\n", encoded);
            var again = MessageCodec.Decode(encoded);
            Assert.Equal(result.Message.GetType(), again.GetType());
            Assert.True(MessageCodec.Equivalent(result.Message, again), file + ": " + encoded + " vs " + MessageCodec.Encode(again));

            // Nothing was lost or invented: the encoding equals the sample member by member, by value.
            var original = (JObject)JToken.Parse(text);
            var reencoded = JObject.Parse(encoded);
            AssertSameValue(original, reencoded, file);
        }

        [Theory]
        [MemberData(nameof(InvalidFixtures))]
        public void AnInvalidSampleIsRejected(string file)
        {
            var text = File.ReadAllText(Path.Combine(FixturesDir, "invalid", file));
            var result = MessageCodec.TryDecode(text);
            Assert.False(result.Ok, file + " decoded to " + (result.Message == null ? "null" : MessageCodec.Encode(result.Message)));
            Assert.Null(result.Message);
            Assert.Throws<ProtocolException>(() => MessageCodec.Decode(text));
        }

        [Theory]
        [InlineData("truncated.json", DecodeFailure.Malformed)]
        [InlineData("not-an-object.json", DecodeFailure.Malformed)]
        [InlineData("missing-type.json", DecodeFailure.Malformed)]
        [InlineData("type-not-string.json", DecodeFailure.Malformed)]
        [InlineData("unknown-type.json", DecodeFailure.UnknownType)]
        [InlineData("hello-protocol-zero.json", DecodeFailure.Invalid)]
        [InlineData("command-unknown-action.json", DecodeFailure.Invalid)]
        public void InvalidSamplesFailForTheRightReason(string file, DecodeFailure expected)
        {
            var result = MessageCodec.TryDecode(File.ReadAllText(Path.Combine(FixturesDir, "invalid", file)));
            Assert.Equal(expected, result.Failure);
        }

        [Fact]
        public void EveryMessageTypeHasAFixture()
        {
            var types = ValidFixtures().Select(f => ((string)f[0]).Split('.')[0]).Distinct().ToList();
            foreach (var type in new[]
            {
                MessageTypes.Beacon, MessageTypes.Hello, MessageTypes.Welcome, MessageTypes.PairRequest, MessageTypes.PairResult,
                MessageTypes.Heartbeat, MessageTypes.State, MessageTypes.Status, MessageTypes.Command, MessageTypes.Telemetry,
                MessageTypes.Error, MessageTypes.AudioStart, MessageTypes.AudioStop, MessageTypes.Artwork,
                MessageTypes.MicStart, MessageTypes.MicStop,
            })
            {
                Assert.Contains(type, types);
            }
        }

        // Audio header vectors (spec §10.2)

        private static JObject AudioVectors => JObject.Parse(File.ReadAllText(Path.Combine(FixturesDir, "audio-header.json")));

        public static IEnumerable<object[]> ValidAudioVectors()
        {
            return ((JArray)AudioVectors["valid"]).Select(v => new object[] { (string)v["name"] });
        }

        public static IEnumerable<object[]> InvalidAudioVectors()
        {
            return ((JArray)AudioVectors["invalid"]).Select(v => new object[] { (string)v["name"] });
        }

        public static IEnumerable<object[]> OpusAudioVectors()
        {
            return ((JArray)AudioVectors["opus"]).Select(v => new object[] { (string)v["name"] });
        }

        [Theory]
        [MemberData(nameof(OpusAudioVectors))]
        public void AnOpusVectorEncodesValidatesAndPlacesByItsToc(string name)
        {
            var v = ((JArray)AudioVectors["opus"]).Single(x => (string)x["name"] == name);
            var h = v["header"];
            var header = new AudioHeader
            {
                Seq = (ushort)(int)h["seq"],
                StreamType = (AudioStreamType)(int)h["streamType"],
                Flags = (byte)(int)h["flags"],
                Timestamp = (uint)(long)h["timestamp"],
                SampleRateField = (ushort)(int)h["sampleRateField"],
                Channels = (byte)(int)h["channels"],
                Format = AudioFormat.Opus,
            };
            Assert.Equal((int)h["format"], (int)header.Format);
            var payload = Unhex((string)v["payloadHex"]);
            var datagram = new byte[AudioHeader.Size + payload.Length];
            header.Write(datagram, 0);
            Array.Copy(payload, 0, datagram, AudioHeader.Size, payload.Length);
            Assert.Equal((string)v["datagramHex"], Hex(datagram));
            Assert.Equal((string)v["headerHex"], Hex(datagram.Take(AudioHeader.Size).ToArray()));

            AudioHeader decoded;
            var bytes = Unhex((string)v["datagramHex"]);
            Assert.Equal(AudioHeaderError.Ok, AudioHeader.TryParse(bytes, 0, bytes.Length, out decoded));
            Assert.Equal(header, decoded);
            Assert.Equal(AudioFormat.Opus, decoded.Format);
            Assert.Equal((int)v["frames"], decoded.PayloadFrames(bytes, AudioHeader.Size, bytes.Length - AudioHeader.Size));
            // An opus datagram carries one Opus packet, not PCM samples: there is nothing to decode as PCM.
        }

        [Theory]
        [MemberData(nameof(ValidAudioVectors))]
        public void AnAudioVectorEncodesAndDecodesByteForByte(string name)
        {
            var v = ((JArray)AudioVectors["valid"]).Single(x => (string)x["name"] == name);
            var h = v["header"];
            var header = new AudioHeader
            {
                Seq = (ushort)(int)h["seq"],
                StreamType = (AudioStreamType)(int)h["streamType"],
                Flags = (byte)(int)h["flags"],
                Timestamp = (uint)(long)h["timestamp"],
                SampleRateField = (ushort)(int)h["sampleRateField"],
                Channels = (byte)(int)h["channels"],
                Format = (AudioFormat)(int)h["format"],
            };
            Assert.Equal((int)h["sampleRateHz"], header.SampleRate);
            Assert.Equal((string)h["stream"], AudioHeader.StreamName(header.StreamType));
            var samples = v["samples"].Select(s => (short)(int)s).ToArray();

            var datagram = header.Encode(samples);
            Assert.Equal((string)v["datagramHex"], Hex(datagram));
            Assert.Equal((string)v["headerHex"], Hex(datagram.Take(AudioHeader.Size).ToArray()));
            Assert.Equal((string)v["payloadHex"], Hex(datagram.Skip(AudioHeader.Size).ToArray()));

            AudioHeader decoded;
            var direction = AudioHeader.ParseDirection((string)v["direction"]);
            var bytes = Unhex((string)v["datagramHex"]);
            Assert.Equal(AudioHeaderError.Ok, AudioHeader.TryParse(bytes, 0, bytes.Length, direction, out decoded));
            var decodedSamples = AudioHeader.DecodeSamples(bytes, AudioHeader.Size, bytes.Length - AudioHeader.Size);
            Assert.Equal(header, decoded);
            Assert.Equal(samples, decodedSamples);
            Assert.Equal((int)v["frames"], decodedSamples.Length / decoded.Channels);
            Assert.Equal((int)h["flags"] == 1, decoded.IsStart);
        }

        [Theory]
        [MemberData(nameof(InvalidAudioVectors))]
        public void AnInvalidAudioVectorIsRejected(string name)
        {
            var v = ((JArray)AudioVectors["invalid"]).Single(x => (string)x["name"] == name);
            var bytes = Unhex((string)v["datagramHex"]);
            var direction = AudioHeader.ParseDirection((string)v["direction"]);
            AudioHeader header;
            Assert.NotEqual(AudioHeaderError.Ok, AudioHeader.TryParse(bytes, 0, bytes.Length, direction, out header));
        }

        // Helpers

        /// <summary>
        /// Compares two JSON values the way the fixtures README asks: numbers by value (6120 == 6120.0), member order
        /// ignored, and an absent member equal to an explicit null (spec §3).
        /// </summary>
        internal static void AssertSameValue(JToken expected, JToken actual, string path)
        {
            if (IsNull(expected) || IsNull(actual))
            {
                Assert.True(IsNull(expected) && IsNull(actual), path + ": " + expected + " vs " + actual);
                return;
            }
            if (expected is JObject eo)
            {
                var ao = Assert.IsType<JObject>(actual);
                foreach (var name in eo.Properties().Select(p => p.Name).Union(ao.Properties().Select(p => p.Name)))
                {
                    AssertSameValue(eo[name], ao[name], path + "." + name);
                }
                return;
            }
            if (expected is JArray ea)
            {
                var aa = Assert.IsType<JArray>(actual);
                Assert.Equal(ea.Count, aa.Count);
                for (var i = 0; i < ea.Count; i++) AssertSameValue(ea[i], aa[i], path + "[" + i + "]");
                return;
            }
            if (IsNumber(expected) && IsNumber(actual))
            {
                Assert.Equal(Convert.ToDouble(((JValue)expected).Value, CultureInfo.InvariantCulture),
                    Convert.ToDouble(((JValue)actual).Value, CultureInfo.InvariantCulture));
                return;
            }
            Assert.True(JToken.DeepEquals(expected, actual), path + ": " + expected + " vs " + actual);
        }

        private static bool IsNull(JToken t)
        {
            return t == null || t.Type == JTokenType.Null;
        }

        private static bool IsNumber(JToken t)
        {
            return t.Type == JTokenType.Integer || t.Type == JTokenType.Float;
        }

        internal static string Hex(byte[] bytes)
        {
            return string.Concat(bytes.Select(b => b.ToString("x2")));
        }

        internal static byte[] Unhex(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(2 * i, 2), 16);
            return bytes;
        }
    }
}

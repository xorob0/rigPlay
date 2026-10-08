// SPDX-License-Identifier: GPL-3.0-only
// MessageCodecTests.cs: codec rules beyond the fixtures (spec §3, §6, §7.1), line framing (§5.1) and the address
// helpers (§4.1, §11, §15).
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using RigPlayPlugin.Net;
using RigPlayPlugin.Protocol;
using Xunit;

namespace RigPlayPlugin.Tests
{
    public class MessageCodecTests
    {
        private const string Hello = "{\"type\":\"hello\",\"tabletId\":\"t-1\",\"name\":\"Tab\",\"appVersion\":\"0.3.0\",\"protocol\":1";

        [Theory]
        [InlineData(Hello + "}", true)]
        [InlineData(Hello + ",\"minProtocol\":1,\"features\":[]}", true)]
        [InlineData(Hello + ",\"somethingNew\":{\"a\":1}}", true)]
        [InlineData(Hello + ",\"features\":[1]}", false)]
        [InlineData(Hello + ",\"minProtocol\":0}", false)]
        [InlineData("{\"type\":\"hello\",\"tabletId\":\"t-1\",\"name\":\"Tab\",\"appVersion\":\"0.3.0\",\"protocol\":1.0}", false)]
        [InlineData("{\"type\":\"hello\",\"tabletId\":\"\",\"name\":\"Tab\",\"appVersion\":\"0.3.0\",\"protocol\":1}", false)]
        [InlineData(Hello + "} {}", false)]
        public void HelloValidation(string line, bool valid)
        {
            Assert.Equal(valid, MessageCodec.TryDecode(line).Ok);
        }

        [Theory]
        [InlineData("{\"type\":\"error\",\"code\":\"shutdown\"}", true)]
        [InlineData("{\"type\":\"error\",\"code\":\"replaced\",\"fatal\":false}", true)]
        [InlineData("{\"type\":\"error\",\"code\":\"notPaired\"}", false)]
        [InlineData("{\"type\":\"error\",\"code\":\"somethingNew\",\"fatal\":true}", true)]
        public void CodesListedAsFatalAreFatalWithoutTheFlag(string line, bool fatal)
        {
            Assert.Equal(fatal, ((ErrorMessage)MessageCodec.Decode(line)).IsFatal);
        }

        [Fact]
        public void EscapedSlashesAreAccepted()
        {
            var state = (StateMessage)MessageCodec.Decode("{\"type\":\"state\",\"dashboardUrl\":\"http:\\/\\/192.168.1.20:8888\\/Dash#Pit%20Board\",\"audio\":{\"enabled\":true,\"port\":23712,\"formats\":[\"pcm_s16le\"]}}");
            Assert.Equal("http://192.168.1.20:8888/Dash#Pit%20Board", state.DashboardUrl);
        }

        [Fact]
        public void ATabletIdOf65CharactersIsInvalid()
        {
            var line = "{\"type\":\"hello\",\"tabletId\":\"" + new string('a', 65) + "\",\"name\":\"T\",\"appVersion\":\"1\",\"protocol\":1}";
            Assert.Equal(DecodeFailure.Invalid, MessageCodec.TryDecode(line).Failure);
        }

        [Fact]
        public void ABeaconHostIdOf129CharactersIsInvalid()
        {
            var line = "{\"type\":\"beacon\",\"name\":\"RIG-PC\",\"hostId\":\"" + new string('a', 129) + "\",\"version\":\"0.1.0\",\"controlPort\":23711,\"audioPort\":23712,\"protocol\":1}";
            Assert.Equal(DecodeFailure.Invalid, MessageCodec.TryDecode(line).Failure);
        }

        [Fact]
        public void AWelcomeHostIdOf129CharactersIsInvalid()
        {
            var line = "{\"type\":\"welcome\",\"hostId\":\"" + new string('a', 129) + "\",\"name\":\"RIG-PC\",\"version\":\"0.1.0\",\"protocol\":1,\"features\":[]}";
            Assert.Equal(DecodeFailure.Invalid, MessageCodec.TryDecode(line).Failure);
        }

        [Theory]
        [InlineData("{\"type\":\"pairRequest\",\"pin\":\"12345a\"}", false)]
        [InlineData("{\"type\":\"pairRequest\",\"pin\":123456}", false)]
        [InlineData("{\"type\":\"pairRequest\",\"pin\":\"000000\"}", true)]
        [InlineData("{\"type\":\"pairRequest\",\"token\":null}", true)]
        [InlineData("{\"type\":\"pairResult\",\"ok\":true,\"token\":\"x\",\"reason\":\"denied\"}", false)]
        [InlineData("{\"type\":\"pairResult\",\"ok\":false,\"token\":\"x\",\"reason\":\"denied\"}", false)]
        [InlineData("{\"type\":\"pairResult\",\"ok\":false}", false)]
        [InlineData("{\"type\":\"heartbeat\"}", true)]
        [InlineData("{\"type\":\"heartbeat\",\"seq\":4294967296}", false)]
        [InlineData("{\"type\":\"heartbeat\",\"seq\":-1}", false)]
        [InlineData("{\"type\":\"command\",\"command\":\"showDashboard\",\"action\":\"whatever\"}", true)]
        [InlineData("{\"type\":\"command\",\"command\":\"toggle\"}", false)]
        [InlineData("{\"type\":\"status\",\"phoneConnected\":true,\"screen\":\"carplay\"}", false)]
        [InlineData("{\"type\":\"status\",\"phoneConnected\":false,\"screen\":\"off\",\"nowPlaying\":null}", true)]
        [InlineData("{\"type\":\"state\",\"audio\":{\"enabled\":true,\"port\":1,\"formats\":[]}}", false)]
        [InlineData("{\"type\":\"state\",\"dashboardUrl\":\"ftp://x/y\",\"audio\":{\"enabled\":true,\"port\":1,\"formats\":[]}}", false)]
        [InlineData("{\"type\":\"state\",\"dashboardUrl\":\"https:\\/\\/pc\\/dashboard\\/A\",\"audio\":{\"enabled\":true,\"port\":1,\"formats\":[]}}", true)]
        [InlineData("{\"type\":\"error\",\"code\":\"unsupportedProtocol\"}", false)]
        [InlineData("{\"type\":\"error\",\"code\":\"somethingNew\",\"fatal\":true}", true)]
        [InlineData("{\"type\":\"audioStart\",\"stream\":\"alt\",\"format\":\"pcm_s16le\",\"sampleRate\":7900,\"channels\":1}", false)]
        [InlineData("{\"type\":\"audioStart\",\"stream\":\"alt\",\"format\":\"opus\",\"sampleRate\":24000,\"channels\":1}", true)]
        [InlineData("{\"type\":\"audioStart\",\"stream\":\"media\",\"format\":\"opus\",\"sampleRate\":44100,\"channels\":2}", false)]
        [InlineData("{\"type\":\"audioStart\",\"stream\":\"media\",\"format\":\"flac\",\"sampleRate\":48000,\"channels\":2}", false)]
        [InlineData("{\"type\":\"audioStart\",\"stream\":\"alt\",\"format\":\"pcm_s16le\",\"sampleRate\":24000,\"channels\":3}", false)]
        [InlineData("{\"type\":\"beacon\",\"name\":\"RIG-PC\",\"hostId\":\"RIG-PC-01\",\"version\":\"0.1.0\",\"controlPort\":23711,\"audioPort\":23712,\"protocol\":1}", true)]
        [InlineData("{\"type\":\"beacon\",\"name\":\"RIG-PC\",\"hostId\":\"\",\"version\":\"0.1.0\",\"controlPort\":23711,\"audioPort\":23712,\"protocol\":1}", false)]
        [InlineData("{\"type\":\"welcome\",\"hostId\":\"RIG-PC-01\",\"name\":\"RIG-PC\",\"version\":\"0.1.0\",\"protocol\":1,\"features\":[]}", true)]
        [InlineData("{\"type\":\"welcome\",\"hostId\":\"\",\"name\":\"RIG-PC\",\"version\":\"0.1.0\",\"protocol\":1,\"features\":[]}", false)]
        public void ContentRules(string line, bool valid)
        {
            var result = MessageCodec.TryDecode(line);
            Assert.True(valid == result.Ok, line + ": " + result.Reason);
        }

        [Fact]
        public void TelemetryDegradesBadFieldsToNull()
        {
            var m = (TelemetryMessage)MessageCodec.Decode("{\"type\":\"telemetry\",\"speedMps\":\"fast\",\"gear\":\"X\",\"heading\":360,\"lat\":45.5,\"rpm\":-5,\"night\":1}");
            Assert.Null(m.SpeedMps);
            Assert.Null(m.Gear);
            Assert.Null(m.Heading);
            Assert.Equal(45.5, m.Lat);
            Assert.Null(m.Rpm);
            Assert.Null(m.Night);
        }

        [Fact]
        public void CommandActionIsOnlyKeptForMedia()
        {
            var m = (CommandMessage)MessageCodec.Decode("{\"type\":\"command\",\"command\":\"showCarPlay\",\"action\":\"next\"}");
            Assert.Null(m.Action);
            Assert.Equal("{\"type\":\"command\",\"command\":\"showCarPlay\"}", MessageCodec.Encode(m));
        }

        [Fact]
        public void EncodingIsOneLineAndKeepsRequiredNulls()
        {
            var state = MessageCodec.Encode(new StateMessage { DashboardUrl = null, Audio = new AudioInfo { Enabled = false, Port = 23712 } });
            Assert.Equal("{\"type\":\"state\",\"dashboardUrl\":null,\"audio\":{\"enabled\":false,\"port\":23712,\"formats\":[\"pcm_s16le\"]}}", state);
            var status = MessageCodec.Encode(new StatusMessage { PhoneConnected = false, Screen = Screens.Idle });
            Assert.Contains("\"nowPlaying\":null", status);
        }

        [Fact]
        public void ErrorFactoryOmitsFatalWhenFalse()
        {
            Assert.Equal("{\"type\":\"error\",\"code\":\"notPaired\",\"message\":\"m\",\"refType\":\"status\"}",
                MessageCodec.Encode(ErrorMessage.Of(ErrorCodes.NotPaired, "m", false, "status")));
            Assert.Equal("{\"type\":\"error\",\"code\":\"shutdown\",\"message\":\"bye\",\"fatal\":true}",
                MessageCodec.Encode(ErrorMessage.Of(ErrorCodes.Shutdown, "bye", true)));
        }

        // Line framing (spec §5.1)

        [Fact]
        public void LinesAreSplitOnNewlineWithOneCarriageReturnStripped()
        {
            var framer = new LineFramer();
            var bytes = Encoding.UTF8.GetBytes("a\r\n\nb");
            Assert.Equal(new[] { "a", "" }, framer.Feed(bytes, 0, bytes.Length));
            bytes = Encoding.UTF8.GetBytes("c\r\r\n");
            Assert.Equal(new[] { "bc\r" }, framer.Feed(bytes, 0, bytes.Length));
        }

        [Fact]
        public void ALineSplitAcrossReadsIsReassembled()
        {
            var framer = new LineFramer();
            var bytes = Encoding.UTF8.GetBytes("{\"type\":\"heartbeat\"}\n");
            var lines = new List<string>();
            foreach (var b in bytes) lines.AddRange(framer.Feed(new[] { b }, 0, 1));
            Assert.Equal(new[] { "{\"type\":\"heartbeat\"}" }, lines);
        }

        [Fact]
        public void ALineOfExactlyTheLimitIsAcceptedAndOneMoreIsNot()
        {
            var framer = new LineFramer(10);
            var ok = Encoding.ASCII.GetBytes(new string('x', 10) + "\r\n");
            Assert.Single(framer.Feed(ok, 0, ok.Length));
            var tooLong = Encoding.ASCII.GetBytes(new string('x', 11));
            Assert.Throws<LineTooLongException>(() => framer.Feed(tooLong, 0, tooLong.Length));
        }

        // Addresses

        [Theory]
        [InlineData("127.0.0.1", true)]
        [InlineData("10.1.2.3", true)]
        [InlineData("172.16.0.1", true)]
        [InlineData("172.31.255.255", true)]
        [InlineData("172.32.0.1", false)]
        [InlineData("192.168.1.30", true)]
        [InlineData("169.254.10.10", true)]
        [InlineData("8.8.8.8", false)]
        [InlineData("100.64.0.1", false)]
        [InlineData("::ffff:192.168.1.30", true)]
        [InlineData("::1", false)]
        [InlineData("fe80::1", false)]
        public void OnlyLanAddressesAreAllowed(string address, bool allowed)
        {
            Assert.Equal(allowed, NetUtil.IsAllowedRemote(IPAddress.Parse(address)));
        }

        [Fact]
        public void DirectedBroadcastsSkipLoopbackHostRoutesAndDuplicates()
        {
            var list = NetUtil.DirectedBroadcasts(new[]
            {
                Pair("192.168.1.20", "255.255.255.0"),
                Pair("192.168.1.21", "255.255.255.0"),
                Pair("10.0.5.9", "255.255.0.0"),
                Pair("127.0.0.1", "255.0.0.0"),
                Pair("172.20.0.2", "255.255.255.255"),
                new KeyValuePair<IPAddress, IPAddress>(IPAddress.Parse("192.168.56.1"), null),
            });
            Assert.Equal(new[] { "192.168.1.255", "10.0.255.255" }, list.Select(a => a.ToString()).ToArray());
        }

        private static KeyValuePair<IPAddress, IPAddress> Pair(string ip, string mask)
        {
            return new KeyValuePair<IPAddress, IPAddress>(IPAddress.Parse(ip), IPAddress.Parse(mask));
        }
    }
}

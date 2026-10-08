// SPDX-License-Identifier: GPL-3.0-only
// MicSenderTests.cs: the PC microphone to the phone (#34, docs/protocol.md §6.13, §10.4): 5 ms datagrams with the mic
// header (seq and timestamp from 0 per micStart, start flag), the stream lifecycle (setting off, no input device logged
// once, micStop only from the owner, takeover, 2 s watchdog), the stats and level meter, and the glue to the control
// server (feature mic in welcome, state.mic, micStart / micStop / session close, unexpectedMessage without the feature).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using RigPlayPlugin.Audio;
using RigPlayPlugin.Protocol;
using Xunit;

namespace RigPlayPlugin.Tests.Audio
{
    /// <summary>An input device the test drives by hand.</summary>
    internal sealed class FakeMicCapture : IMicCapture
    {
        public bool DevicePresent = true;
        public string StartError;
        public int Starts;
        public int Stops;
        public string LastDeviceId;
        public int LastSampleRate;
        public Action<short[], int> Sink;

        public bool Running => Sink != null;

        public string Start(string deviceId, int sampleRate, Action<short[], int> onSamples)
        {
            Starts++;
            LastDeviceId = deviceId;
            LastSampleRate = sampleRate;
            if (!DevicePresent) return "no recording device on this PC";
            if (StartError != null) return StartError;
            Sink = onSamples;
            return null;
        }

        public void Stop()
        {
            Stops++;
            Sink = null;
        }

        public bool HasDevice(string deviceId) => DevicePresent;

        public string DeviceName => Running ? "Fake Mic" : null;

        /// <summary>Delivers <paramref name="count"/> samples of value <paramref name="value"/>, as the capture thread would.</summary>
        public void Deliver(int count, short value = 1000)
        {
            var samples = Enumerable.Repeat(value, count).ToArray();
            Sink?.Invoke(samples, count);
        }

        public void Dispose()
        {
        }
    }

    public class MicPacketizerTests
    {
        [Theory]
        [InlineData(16000, 80)]
        [InlineData(24000, 120)]
        [InlineData(48000, 240)]
        [InlineData(8000, 40)]
        public void APacketIsFiveMilliseconds(int rate, int frames)
        {
            var p = new MicPacketizer(rate);
            Assert.Equal(frames, p.FramesPerPacket);
            Assert.Equal(AudioHeader.Size + 2 * frames, p.PacketBytes);
        }

        [Fact]
        public void PacketsCarryTheMicHeaderWithSeqTimestampAndStartFlag()
        {
            var p = new MicPacketizer(16000);
            var packets = new List<byte[]>();
            var samples = Enumerable.Range(0, 200).Select(i => (short)(i * 100 - 10000)).ToArray();
            p.Write(samples, 0, samples.Length, (d, n) => packets.Add(d.Take(n).ToArray()));

            Assert.Equal(2, packets.Count);
            Assert.Equal(40, p.Pending);
            for (var i = 0; i < 2; i++)
            {
                AudioHeader h;
                Assert.Equal(AudioHeaderError.Ok, AudioHeader.TryParse(packets[i], AudioDirection.PcToTablet, out h));
                Assert.Equal(AudioStreamType.Mic, h.StreamType);
                Assert.Equal(i, h.Seq);
                Assert.Equal((uint)(80 * i), h.Timestamp);
                Assert.Equal(i == 0, h.IsStart);
                Assert.Equal(16000, h.SampleRate);
                Assert.Equal(1, h.Channels);
                Assert.Equal(AudioFormat.PcmS16le, h.Format);
                Assert.Equal(samples.Skip(80 * i).Take(80).ToArray(), AudioHeader.DecodeSamples(packets[i], AudioHeader.Size, 160));
                // The plugin would drop its own datagram: streamType 4 never flows tablet to plugin.
                Assert.Equal(AudioHeaderError.ReservedStreamType, AudioHeader.TryParse(packets[i], out h));
            }
        }

        [Fact]
        public void ResetStartsANewStream()
        {
            var p = new MicPacketizer(24000);
            var count = 0;
            p.Write(new short[300], 0, 300, (d, n) => count++);
            Assert.Equal(2, count);
            Assert.Equal(2, p.NextSeq);
            Assert.Equal(240u, p.NextTimestamp);
            Assert.Equal(60, p.Pending);

            p.Reset();
            Assert.Equal(0, p.Pending);
            byte[] first = null;
            p.Write(new short[120], 0, 120, (d, n) => first = d.Take(n).ToArray());
            AudioHeader h;
            Assert.Equal(AudioHeaderError.Ok, AudioHeader.TryParse(first, AudioDirection.PcToTablet, out h));
            Assert.Equal(0, h.Seq);
            Assert.Equal(0u, h.Timestamp);
            Assert.True(h.IsStart);
        }

        [Fact]
        public void SeqWrapsAt65535()
        {
            var p = new MicPacketizer(8000);
            var block = new short[40];
            for (var i = 0; i < 65535; i++) p.Write(block, 0, 40, (d, n) => { });
            Assert.Equal(65535, p.NextSeq);
            byte[] last = null;
            p.Write(block, 0, 40, (d, n) => last = d.Take(n).ToArray());
            p.Write(block, 0, 40, (d, n) => last = d.Take(n).ToArray());
            AudioHeader h;
            Assert.Equal(AudioHeaderError.Ok, AudioHeader.TryParse(last, AudioDirection.PcToTablet, out h));
            Assert.Equal(0, h.Seq);
            Assert.Equal(65536u * 40, h.Timestamp);
        }

        [Fact]
        public void PeakAndLevelText()
        {
            Assert.Equal(0.0, MicPacketizer.Peak(new short[10], 0, 10));
            Assert.Equal(1.0, MicPacketizer.Peak(new short[] { 0, short.MinValue, 5 }, 0, 3));
            Assert.Equal(0.5, MicPacketizer.Peak(new short[] { 16384, -100 }, 0, 2), 3);
            Assert.StartsWith("silence", MicSender.FormatLevel(double.NegativeInfinity));
            Assert.Equal("0 dBFS ▮▮▮▮▮▮▮▮▮▮", MicSender.FormatLevel(0));
            Assert.Equal("-30 dBFS ▮▮▮▮▮▯▯▯▯▯", MicSender.FormatLevel(-30));
            Assert.Equal("-80 dBFS ▯▯▯▯▯▯▯▯▯▯", MicSender.FormatLevel(-80));
        }

        [Fact]
        public void AnInvalidRateIsRefused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new MicPacketizer(11025));
            Assert.Throws<ArgumentOutOfRangeException>(() => new MicPacketizer(96000));
        }
    }

    public sealed class MicSenderTests : IDisposable
    {
        private static readonly IPEndPoint Tablet = new IPEndPoint(IPAddress.Parse("192.168.1.42"), 23713);

        private readonly RigPlaySettings settings = new RigPlaySettings().Normalize();
        private readonly FakeMicCapture capture = new FakeMicCapture();
        private readonly ManualClock clock = new ManualClock();
        private readonly List<KeyValuePair<IPEndPoint, byte[]>> sent = new List<KeyValuePair<IPEndPoint, byte[]>>();
        private readonly List<string> log = new List<string>();
        private readonly MicSender sender;
        private long lastLine;

        public MicSenderTests()
        {
            PluginLog.Sink = (level, message) => { lock (log) log.Add(message); };
            lastLine = clock.NowMs;
            sender = new MicSender(() => settings, capture, clock, (d, n, to) => sent.Add(new KeyValuePair<IPEndPoint, byte[]>(to, d.Take(n).ToArray())));
        }

        public void Dispose()
        {
            sender.Dispose();
            PluginLog.Sink = null;
        }

        private bool Start(int session = 1, int rate = 16000, IPEndPoint to = null)
        {
            return sender.Start(session, "Lenovo Tab P11", to ?? Tablet, rate, () => lastLine);
        }

        /// <summary>The log lines matching <paramref name="match"/>, read under the lock the sink writes with, so a
        /// background logging thread cannot modify the list mid-enumeration.</summary>
        private int LogCount(Func<string, bool> match)
        {
            lock (log) return log.Count(match);
        }

        /// <summary>A snapshot of the log, taken under the sink's lock, safe to enumerate.</summary>
        private List<string> LogSnapshot()
        {
            lock (log) return new List<string>(log);
        }

        private AudioHeader Header(int index)
        {
            AudioHeader h;
            Assert.Equal(AudioHeaderError.Ok, AudioHeader.TryParse(sent[index].Value, AudioDirection.PcToTablet, out h));
            return h;
        }

        [Fact]
        public void MicStartCapturesTheChosenDeviceAndSendsToTheTablet()
        {
            settings.MicDeviceId = "{0.0.1.00000000}.{mic}";
            Assert.True(Start(rate: 24000));
            Assert.Equal("{0.0.1.00000000}.{mic}", capture.LastDeviceId);
            Assert.Equal(24000, capture.LastSampleRate);
            Assert.Equal(1, sender.OwnerSessionId);

            capture.Deliver(240 + 100);
            Assert.Equal(2, sent.Count);
            Assert.All(sent, s => Assert.Equal(Tablet, s.Key));
            Assert.Equal(AudioHeader.Size + 240, sent[0].Value.Length);
            Assert.True(Header(0).IsStart);
            Assert.Equal(0, Header(0).Seq);
            Assert.Equal(1, Header(1).Seq);
            Assert.Equal(120u, Header(1).Timestamp);
            Assert.Equal(24000, Header(1).SampleRate);
        }

        [Fact]
        public void ANewMicStartRestartsSeqAndTimestampFromZero()
        {
            Assert.True(Start());
            capture.Deliver(80 * 3);
            Assert.Equal(3, sent.Count);
            Assert.True(Start(rate: 16000));
            capture.Deliver(80);
            Assert.Equal(4, sent.Count);
            Assert.Equal(0, Header(3).Seq);
            Assert.Equal(0u, Header(3).Timestamp);
            Assert.True(Header(3).IsStart);
        }

        [Fact]
        public void WithTheSettingOffMicStartIsIgnored()
        {
            settings.MicEnabled = false;
            Assert.False(Start());
            Assert.Equal(0, capture.Starts);
            Assert.Equal(0, sender.OwnerSessionId);
            Assert.False(sender.Available);
            Assert.Equal("Off", sender.Stats.StateText);
        }

        [Fact]
        public void WithoutAnInputDeviceTheFailureIsLoggedOnceAndNothingIsSent()
        {
            capture.DevicePresent = false;
            Assert.False(sender.Available);
            Assert.False(Start());
            Assert.False(Start());
            Assert.False(Start(session: 2));
            Assert.Equal(1, LogCount(l => l.Contains("not started") && l.Contains("no recording device")));
            Assert.Empty(sent);
            var stats = sender.Stats;
            Assert.True(stats.Started);
            Assert.False(stats.Capturing);
            Assert.Contains("no input device", stats.StateText);
            Assert.Contains("No input device", stats.LastEvent);
            Assert.Equal("—", stats.LevelText);

            // A device appears: the next micStart captures, and a later failure is logged again.
            capture.DevicePresent = true;
            Assert.True(Start());
            capture.DevicePresent = false;
            Assert.False(Start());
            Assert.Equal(2, LogCount(l => l.Contains("not started")));
        }

        [Fact]
        public void ADeviceThatFailsToOpenIsReportedAndNothingIsSent()
        {
            capture.StartError = "the device is in use by another application";
            Assert.False(Start());
            Assert.Empty(sent);
            Assert.False(sender.Stats.Capturing);
            Assert.Contains("the device is in use", sender.Stats.LastEvent);
            Assert.Contains(LogSnapshot(), l => l.Contains("not started") && l.Contains("the device is in use"));
        }

        [Fact]
        public void MicStopOnlyFromTheOwnerStopsTheStream()
        {
            Assert.True(Start(session: 7));
            sender.StopIfOwner(8, "micStop from the tablet");
            Assert.True(capture.Running);
            sender.StopIfOwner(7, "micStop from the tablet");
            Assert.False(capture.Running);
            Assert.Equal(0, sender.OwnerSessionId);
            Assert.Contains("micStop", sender.Stats.LastEvent);

            // Late samples from the capture thread are not sent.
            var before = sent.Count;
            sender.OnSamples(1, new short[400], 400);
            Assert.Equal(before, sent.Count);
            sender.StopIfOwner(7, "again"); // no stream: ignored
        }

        [Fact]
        public void AnotherTabletTakesTheStreamOver()
        {
            var other = new IPEndPoint(IPAddress.Parse("192.168.1.43"), 23800);
            Assert.True(Start(session: 1));
            Assert.True(Start(session: 2, to: other));
            Assert.Equal(2, sender.OwnerSessionId);
            sender.StopIfOwner(1, "the tablet disconnected");
            Assert.Equal(2, sender.OwnerSessionId);
            capture.Deliver(80);
            Assert.Equal(other, sent.Single().Key);
        }

        [Fact]
        public void TwoSecondsWithoutALineFromTheOwnerStopsTheStream()
        {
            Assert.True(Start());
            clock.Advance(1999);
            sender.Tick();
            Assert.True(capture.Running);
            lastLine = clock.NowMs; // a heartbeat arrives
            clock.Advance(1999);
            sender.Tick();
            Assert.True(capture.Running);
            clock.Advance(1);
            sender.Tick();
            Assert.False(capture.Running);
            Assert.Contains("no heartbeat", sender.Stats.LastEvent);
        }

        [Fact]
        public void SwitchingTheSettingOffStopsTheStream()
        {
            var changes = 0;
            sender.Changed += () => changes++;
            Assert.True(Start());
            settings.MicEnabled = false;
            sender.SettingsChanged();
            Assert.False(capture.Running);
            Assert.True(changes >= 2);

            settings.MicEnabled = true;
            Assert.True(Start());
            settings.MicEnabled = false;
            sender.Tick();
            Assert.False(capture.Running);
        }

        [Fact]
        public void ANewDeviceRestartsTheStreamOnIt()
        {
            Assert.True(Start());
            settings.MicDeviceId = "other";
            sender.SettingsChanged();
            Assert.True(capture.Running);
            Assert.Equal("other", capture.LastDeviceId);
            Assert.Equal(2, capture.Starts);
        }

        [Fact]
        public void StatsShowTheStreamAndTheLevel()
        {
            // No boost: the meter shows the raw capture level (the boost has its own test below).
            settings.MicAutoBoost = false;
            settings.MicBoostDb = 0;
            Assert.True(Start());
            for (var i = 0; i < 8; i++)
            {
                capture.Deliver(80 * 50, 16384); // 250 ms of audio at -6 dBFS
                clock.Advance(250);
                lastLine = clock.NowMs; // heartbeats keep arriving
                sender.Tick();
            }
            var stats = sender.Stats;
            Assert.True(stats.Capturing);
            Assert.Equal(400, stats.Packets);
            Assert.InRange(stats.PacketsPerSecond, 190, 210);
            Assert.Equal("Lenovo Tab P11", stats.Tablet);
            Assert.Equal("192.168.1.42:23713", stats.Target);
            Assert.Equal("Fake Mic", stats.Device);
            Assert.InRange(stats.LevelDb, -6.1, -5.9);
            Assert.StartsWith("Sending to Lenovo Tab P11 (192.168.1.42:23713) · 16 kHz · ", stats.StateText);
            Assert.StartsWith("-6 dBFS ▮▮▮▮▮▮▮▮▮", stats.LevelText);
            Assert.Contains("· boost 0 dB", stats.LevelText);
            Assert.DoesNotContain("automatic", stats.LevelText);

            // Silence: the meter falls 20 dB/s rather than dropping at once.
            capture.Deliver(80, 0);
            clock.Advance(250);
            sender.Tick();
            Assert.InRange(sender.Stats.LevelDb, -11.1, -10.9);
        }

        [Fact]
        public void TheBoostIsAppliedToTheSamplesSentAndShownInTheStats()
        {
            settings.MicAutoBoost = false;
            settings.MicBoostDb = 6; // ×1.995
            Assert.True(Start());
            capture.Deliver(80, 1000);
            Assert.Single(sent);
            var samples = AudioHeader.DecodeSamples(sent[0].Value, AudioHeader.Size, 160);
            Assert.Equal(80, samples.Length);
            Assert.All(samples, v => Assert.InRange(v, 1994, 1996));

            sender.Tick();
            var stats = sender.Stats;
            Assert.InRange(stats.GainDb, 5.9, 6.1);
            Assert.False(stats.AutoGain);
            Assert.Contains("boost +6 dB", stats.LevelText);
            Assert.DoesNotContain("automatic", stats.LevelText);
            // The meter shows what the phone gets: 1995/32768 is about -24.3 dBFS, not the raw -30.3.
            Assert.InRange(stats.LevelDb, -24.5, -24.1);

            // Automatic mode by default: the gain climbs while someone talks and the text says so.
            settings.MicAutoBoost = true;
            settings.MicBoostDb = 20;
            for (var i = 0; i < 8; i++)
            {
                capture.Deliver(80 * 50, 1000); // 250 ms of speech at -30 dBFS
                clock.Advance(250);
                lastLine = clock.NowMs;
                sender.Tick();
            }
            stats = sender.Stats;
            Assert.True(stats.AutoGain);
            Assert.InRange(stats.GainDb, 17.5, 18.5); // from the +6 dB in effect, 2 s at 6 dB/s
            Assert.EndsWith("(automatic)", stats.LevelText);
            Assert.Contains("boost +18 dB", stats.LevelText);
        }

        [Fact]
        public void SendFailuresAreCountedNotThrown()
        {
            var failing = new MicSender(() => settings, capture, clock, (d, n, to) => throw new System.Net.Sockets.SocketException(10051));
            Assert.True(failing.Start(1, "t", Tablet, 16000, () => clock.NowMs));
            capture.Deliver(160);
            failing.Tick();
            Assert.Equal(2, failing.Stats.SendErrors);
            Assert.Equal(0, failing.Stats.Packets);
            failing.Dispose();
        }

        [Fact]
        public void ASlowSendToDoesNotFreezeTheCapture()
        {
            // The send blocks after it signals it has started: a datagram leaving the socket is in flight. OnSamples
            // must return while the send is still blocked, proving the I/O no longer runs under the stream lock and so
            // a slow SendTo cannot freeze the capture thread (or the stats/Stop path that also takes the lock).
            var sendStarted = new System.Threading.ManualResetEventSlim(false);
            var release = new System.Threading.ManualResetEventSlim(false);
            var firstSend = 1;
            using (var blocking = new MicSender(() => settings, capture, clock, (d, n, to) =>
                   {
                       // Only the first send blocks; later sends return at once, so a second OnSamples can finish.
                       if (System.Threading.Interlocked.Exchange(ref firstSend, 0) == 1)
                       {
                           sendStarted.Set();
                           release.Wait(5000);
                       }
                   }))
            {
                Assert.True(blocking.Start(1, "t", Tablet, 16000, () => clock.NowMs));

                // Deliver one full datagram on a worker; the send inside it will block.
                var worker = new System.Threading.Thread(() => blocking.OnSamples(1, new short[80], 80));
                worker.Start();
                Assert.True(sendStarted.Wait(5000), "the send never started");

                // While the first send is still blocked, the capture thread hands back: a second OnSamples does its
                // work (produces and sends its datagram) and returns without waiting for the blocked first send. If the
                // I/O ran under the lock this Join would time out, because the first send still holds the lock.
                var second = new System.Threading.Thread(() => blocking.OnSamples(1, new short[80], 80));
                second.Start();
                Assert.True(second.Join(5000), "OnSamples blocked behind a slow send under the lock");

                release.Set();
                Assert.True(worker.Join(5000));
            }
        }

        [Fact]
        public void TheDefaultSenderSendsUdpDatagramsToTheTabletPort()
        {
            using (var listener = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                listener.Client.ReceiveTimeout = 5000;
                var port = ((IPEndPoint)listener.Client.LocalEndPoint).Port;
                using (var udp = new MicSender(() => settings, capture, clock))
                {
                    Assert.True(udp.Start(1, "t", new IPEndPoint(IPAddress.Loopback, port), 16000, () => clock.NowMs));
                    capture.Deliver(80 * 3);
                    for (var i = 0; i < 3; i++)
                    {
                        IPEndPoint from = null;
                        var datagram = listener.Receive(ref from);
                        AudioHeader h;
                        Assert.Equal(AudioHeaderError.Ok, AudioHeader.TryParse(datagram, AudioDirection.PcToTablet, out h));
                        Assert.Equal(i, h.Seq);
                        Assert.Equal(AudioHeader.Size + 160, datagram.Length);
                    }
                }
            }
        }

        [Fact]
        public void WithoutNAudioEveryMicStartFails()
        {
            var none = new MicSender(() => settings, null, clock, (d, n, to) => { });
            Assert.False(none.Available);
            Assert.False(none.Start(1, "t", Tablet, 16000, () => clock.NowMs));
            Assert.Contains("NAudio", none.Stats.LastEvent);
            none.Dispose();
        }
    }

    public sealed class MicGlueTests : IDisposable
    {
        private readonly RigPlaySettings settings = new RigPlaySettings().Normalize();
        private readonly RigPlayHost host;
        private readonly FakeMicCapture capture = new FakeMicCapture();
        private readonly MicSender sender;

        public MicGlueTests()
        {
            host = new RigPlayHost(settings, new HostEnvironment { PluginVersion = "0.1.0", MachineName = "RIG-PC" }, null, ControlServerTests.FastNoWatchdog)
            {
                ControlPortOverride = 0,
                BeaconEnabled = false,
                ProbeEnabled = false,
                TelemetryTimerEnabled = false,
            };
            host.Start();
            sender = new MicSender(() => settings, capture, host.Clock, (d, n, to) => { });
        }

        public void Dispose()
        {
            sender.Dispose();
            host.Stop();
        }

        private StateMessage Pair(FakeTablet t, bool mic)
        {
            var hello = FakeTablet.NewHello();
            if (mic) hello.Features.Add(Features.Mic);
            t.Send(hello);
            var welcome = t.Expect<WelcomeMessage>();
            Assert.Equal(mic, welcome.Features.Contains(Features.Mic));
            t.Send(new PairRequestMessage());
            Assert.Equal(PairReasons.PinRequired, t.Expect<PairResultMessage>().Reason);
            Assert.True(FakeTablet.WaitFor(() => host.Pairing.Pending.Any()));
            t.Send(new PairRequestMessage { Pin = host.Pairing.Pending.Single().Pin });
            Assert.True(t.Expect<PairResultMessage>().Ok);
            return t.Expect<StateMessage>();
        }

        private static MicStartMessage MicStart(int port = 23713, int rate = 16000)
        {
            return new MicStartMessage { SampleRate = rate, Port = port };
        }

        [Fact]
        public void StateMicFollowsTheSettingAndTheDevice()
        {
            using (var glue = new MicGlue(host, sender))
            using (var t = new FakeTablet(host.Server.Port))
            {
                var state = Pair(t, mic: true);
                Assert.NotNull(state.Mic);
                Assert.True(state.Mic.Enabled);

                settings.MicEnabled = false;
                sender.SettingsChanged();
                Assert.False(t.Expect<StateMessage>().Mic.Enabled);

                settings.MicEnabled = true;
                capture.DevicePresent = false;
                sender.SettingsChanged();
                // Still no device: nothing changed, no new state.
                Assert.DoesNotContain(t.Drain(300), m => m is StateMessage);

                capture.DevicePresent = true;
                sender.SettingsChanged();
                Assert.True(t.Expect<StateMessage>().Mic.Enabled);
            }
        }

        [Fact]
        public void ATabletWithoutTheFeatureGetsNoStateMicAndCannotStartTheMicrophone()
        {
            using (new MicGlue(host, sender))
            using (var t = new FakeTablet(host.Server.Port))
            {
                var state = Pair(t, mic: false);
                Assert.Null(state.Mic);
                t.Send(MicStart());
                var error = t.ExpectError(ErrorCodes.UnexpectedMessage);
                Assert.Equal(MessageTypes.MicStart, error.RefType);
                Assert.Equal(0, capture.Starts);
            }
        }

        [Fact]
        public void MicStartAndMicStopDriveTheCaptureForThatTablet()
        {
            using (new MicGlue(host, sender))
            {
                using (var t = new FakeTablet(host.Server.Port))
                {
                    Pair(t, mic: true);
                    t.Send(MicStart(port: 23999, rate: 24000));
                    Assert.True(FakeTablet.WaitFor(() => capture.Running));
                    Assert.Equal(24000, capture.LastSampleRate);
                    Assert.Equal("Lenovo Tab P11", sender.Stats.Tablet);
                    Assert.Equal(new IPEndPoint(IPAddress.Loopback, 23999).ToString(), sender.Stats.Target);

                    t.Send(new MicStopMessage());
                    Assert.True(FakeTablet.WaitFor(() => !capture.Running));

                    t.Send(MicStart());
                    Assert.True(FakeTablet.WaitFor(() => capture.Running));
                }
                // The tablet's session closed: the stream stops with it.
                Assert.True(FakeTablet.WaitFor(() => !capture.Running));
                Assert.Contains("disconnected", sender.Stats.LastEvent);
            }
        }

        [Fact]
        public void TheFeatureIsOfferedAndMicMessagesNeedAPairedSession()
        {
            Assert.Contains(Features.Mic, RigPlayHost.OfferedFeatures);
            using (new MicGlue(host, sender))
            using (var t = new FakeTablet(host.Server.Port))
            {
                var hello = FakeTablet.NewHello();
                hello.Features.Add(Features.Mic);
                t.Send(hello);
                t.Expect<WelcomeMessage>();
                t.Send(MicStart());
                Assert.Equal(MessageTypes.MicStart, t.ExpectError(ErrorCodes.NotPaired).RefType);
                Assert.Equal(0, capture.Starts);
            }
        }
    }
}

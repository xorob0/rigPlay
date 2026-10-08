// SPDX-License-Identifier: GPL-3.0-only
// AudioReceiverTests.cs: the receiver's stream lifecycle (audioStart / audioStop / link loss, the start-flag
// fallback, format mismatch, the 2 s idle stop), the source filter, the stats snapshot, and one real UDP
// round trip on the loopback interface.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using RigPlayPlugin.Audio;
using Xunit;

namespace RigPlayPlugin.Tests.Audio
{
    public class AudioReceiverTests
    {
        private sealed class FakeSink : IAudioSink
        {
            public readonly List<string> Events = new List<string>();
            public readonly List<AudioStream> Active = new List<AudioStream>();

            public void StreamActivated(AudioStream stream)
            {
                lock (Events)
                {
                    Events.Add("+" + AudioHeader.StreamName(stream.Type));
                    Active.Add(stream);
                }
            }

            public void StreamDeactivated(AudioStream stream)
            {
                lock (Events)
                {
                    Events.Add("-" + AudioHeader.StreamName(stream.Type));
                    Active.Remove(stream);
                }
            }
        }

        private long now = 1000;
        private readonly FakeSink sink = new FakeSink();

        /// <summary>Target 20 ms, learning up to 200 ms, skip-ahead 180 ms above the target (200 ms at the start).</summary>
        private AudioReceiver NewReceiver()
        {
            return new AudioReceiver(0, sink, 20, 200, 180, () => now);
        }

        private static byte[] Datagram(AudioStreamType type, int seq, bool start, int rate = 48000, int channels = 2, int frames = 240)
        {
            var header = AudioHeader.Create((ushort)seq, type, start, (uint)(seq * frames), rate, channels);
            return header.Encode(new short[frames * channels]);
        }

        private static void Feed(AudioReceiver r, byte[] datagram)
        {
            r.ProcessDatagram(datagram, datagram.Length, IPAddress.Loopback);
        }

        [Fact]
        public void AudioStartThenDatagramsActivateTheStream()
        {
            var r = NewReceiver();
            Assert.True(r.OnAudioStart("media", "pcm_s16le", 48000, 2));
            Assert.True(r.IsStarted(AudioStreamType.Media));
            Assert.Empty(sink.Events); // nothing plays before datagrams arrive

            Feed(r, Datagram(AudioStreamType.Media, 0, true));
            Feed(r, Datagram(AudioStreamType.Media, 1, false));
            Assert.Equal(new[] { "+media" }, sink.Events);
            var stream = r.GetStream(AudioStreamType.Media);
            Assert.False(stream.AutoStarted);
            Assert.Equal(2, stream.Buffer.Counters.Received);
        }

        [Fact]
        public void AudioStopAndLinkLossDeactivateAtOnce()
        {
            var r = NewReceiver();
            r.OnAudioStart(AudioStreamType.Media, 48000, 2, AudioFormat.PcmS16le);
            r.OnAudioStart(AudioStreamType.Alt, 24000, 1, AudioFormat.PcmS16le);
            Feed(r, Datagram(AudioStreamType.Media, 0, true));
            Feed(r, Datagram(AudioStreamType.Alt, 0, true, 24000, 1));
            r.OnAudioStop("media");
            Assert.False(r.IsStarted(AudioStreamType.Media));
            Assert.Equal(new[] { "+media", "+alt", "-media" }, sink.Events);

            r.OnLinkLost();
            Assert.False(r.IsStarted(AudioStreamType.Alt));
            Assert.Empty(sink.Active);

            // After a stop, datagrams without a new start are rejected.
            Feed(r, Datagram(AudioStreamType.Media, 5, false));
            r.Tick(now);
            Assert.Equal(1, r.Stats.Rejected);
        }

        [Fact]
        public void TheStartFlagStartsAStreamAsAFallback()
        {
            var r = NewReceiver();
            Feed(r, Datagram(AudioStreamType.Media, 0, true, 44100, 2));
            var stream = r.GetStream(AudioStreamType.Media);
            Assert.NotNull(stream);
            Assert.True(stream.AutoStarted);
            Assert.Equal(44100, stream.SampleRate);
            Assert.Equal(new[] { "+media" }, sink.Events);

            // A new start flag with another format restarts an auto-started stream.
            Feed(r, Datagram(AudioStreamType.Media, 0, true, 48000, 2));
            Assert.Equal(48000, r.GetStream(AudioStreamType.Media).SampleRate);
            Assert.Equal(new[] { "+media", "-media", "+media" }, sink.Events);
        }

        [Fact]
        public void WithoutTheFallbackOrTheFlagNothingStarts()
        {
            var r = NewReceiver();
            Feed(r, Datagram(AudioStreamType.Media, 3, false));
            r.AutoStartOnFirstFlag = false;
            Feed(r, Datagram(AudioStreamType.Media, 0, true));
            Assert.False(r.IsStarted(AudioStreamType.Media));
            r.Tick(now);
            Assert.Equal(2, r.Stats.Rejected);
            Assert.Empty(sink.Events);
        }

        [Fact]
        public void DatagramsInAnotherFormatThanAudioStartAreDropped()
        {
            var r = NewReceiver();
            r.OnAudioStart(AudioStreamType.Media, 48000, 2, AudioFormat.PcmS16le);
            Feed(r, Datagram(AudioStreamType.Media, 0, true, 44100, 2));
            Feed(r, Datagram(AudioStreamType.Media, 1, false, 48000, 1));
            Assert.Empty(sink.Events);
            Assert.Equal(48000, r.GetStream(AudioStreamType.Media).SampleRate);
            r.Tick(now);
            Assert.Equal(2, r.Stats.Rejected);
        }

        [Fact]
        public void InvalidAnnouncementsAreRefused()
        {
            var r = NewReceiver();
            Assert.False(r.OnAudioStart("mic", "pcm_s16le", 48000, 2));
            Assert.False(r.OnAudioStart("media", "opus", 48000, 2));
            Assert.False(r.OnAudioStart("media", "pcm_s16le", 22050, 2));
            Assert.False(r.OnAudioStart("media", "pcm_s16le", 48000, 3));
            Assert.False(r.IsStarted(AudioStreamType.Media));
            r.OnAudioStop("media"); // not started: ignored
            r.OnAudioStop("nonsense");
        }

        [Fact]
        public void InvalidDatagramsAreCounted()
        {
            var r = NewReceiver();
            Feed(r, new byte[5]);
            Feed(r, Convert.FromHexString("000004010000000001e002010100ffff"));
            r.Tick(now);
            Assert.Equal(2, r.Stats.Invalid);
            Assert.Equal(2, r.Stats.Datagrams);
        }

        [Fact]
        public void TheSourceFilterRejectsOtherHosts()
        {
            var r = NewReceiver();
            r.SourceFilter = ip => ip.Equals(IPAddress.Parse("192.168.1.20"));
            Feed(r, Datagram(AudioStreamType.Media, 0, true));
            Assert.False(r.IsStarted(AudioStreamType.Media));
            var d = Datagram(AudioStreamType.Media, 0, true);
            r.ProcessDatagram(d, d.Length, IPAddress.Parse("192.168.1.20"));
            Assert.True(r.IsStarted(AudioStreamType.Media));
            r.Tick(now);
            Assert.Equal(1, r.Stats.Rejected);
        }

        [Fact]
        public void TwoSecondsWithoutDatagramsStopsTheOutputAndDatagramsResumeIt()
        {
            var r = NewReceiver();
            Feed(r, Datagram(AudioStreamType.Media, 0, true));
            now += 1900;
            r.Tick(now);
            Assert.Equal(new[] { "+media" }, sink.Events);
            now += 200;
            r.Tick(now);
            Assert.Equal(new[] { "+media", "-media" }, sink.Events);
            Assert.True(r.IsStarted(AudioStreamType.Media)); // still started: only the output stopped
            Assert.Equal(0, r.GetStream(AudioStreamType.Media).Buffer.BufferedFrames); // flushed
            Assert.False(r.Stats.Find(AudioStreamType.Media).Active);

            Feed(r, Datagram(AudioStreamType.Media, 1, false));
            Assert.Equal(new[] { "+media", "-media", "+media" }, sink.Events);
        }

        [Fact]
        public void StatsReportEveryStreamWithRatesAndBuffer()
        {
            var r = NewReceiver();
            r.Tick(now);
            for (var i = 0; i < 100; i++)
            {
                if (i % 20 == 7) continue; // 5 % loss
                Feed(r, Datagram(AudioStreamType.Media, i, i == 0));
            }
            now += 500;
            r.Tick(now);
            var stats = r.Stats;
            Assert.Equal(3, stats.Streams.Count);
            var media = stats.Find(AudioStreamType.Media);
            Assert.True(media.Started);
            Assert.True(media.Active);
            Assert.True(media.AutoStarted);
            Assert.Equal(95, media.Received);
            Assert.Equal(5, media.Lost); // seq 7, 27, 47, 67 and 87
            Assert.Equal(5.0, media.LossPercent, 6);
            Assert.Equal(190, media.PacketsPerSecond, 6);
            Assert.True(media.BufferMs > 0);
            Assert.Equal(20, media.TargetMs, 6);
            Assert.Equal(0, media.Underruns);
            Assert.True(media.Skips >= 1); // nothing pulled the 500 ms pushed: the buffer passed its 200 ms skip ceiling
            Assert.Equal(0, media.LongestStallMs, 6);
            Assert.Equal("48 kHz stereo pcm_s16le", media.FormatText);
            Assert.False(stats.Find(AudioStreamType.Alt).Started);
            Assert.Equal("stopped", stats.Find(AudioStreamType.Telephony).ToDisplayString());
        }

        [Fact]
        public void ARestartedStreamStartsFromTheTargetTheLastOneLearned()
        {
            var r = NewReceiver(); // target 20 ms, learning up to 200 ms
            var learned = new List<double>();
            r.TargetLearned += learned.Add;
            r.OnAudioStart("media", "pcm_s16le", 48000, 2);
            for (var i = 0; i < 5; i++) Feed(r, Datagram(AudioStreamType.Media, i, i == 0));
            var first = r.GetStream(AudioStreamType.Media).Buffer;
            Assert.True(first.IsPlaying);
            // A 100 ms stall: the 25 ms of audio held (five datagrams) drained and 75 ms of silence followed.
            first.Read(new byte[4800 * 4], 0, 4800 * 4);
            Assert.Equal(1, first.Counters.Underruns);
            for (var i = 5; i < 30; i++) Feed(r, Datagram(AudioStreamType.Media, i, false));
            Assert.True(first.IsPlaying);
            // The target becomes a quarter more than the 100 ms stall.
            Assert.Equal(125, first.TargetMs, 2);
            r.Tick(now);
            Assert.Equal(new[] { 125.0 }, learned.Select(l => Math.Round(l, 2)));
            Assert.Equal(125, r.LearnedTargetMs, 2);

            // The tablet stops the stream (a pause longer than its idle timeout) and starts it again, at another rate.
            r.OnAudioStop("media");
            r.OnAudioStart("media", "pcm_s16le", 44100, 2);
            var second = r.GetStream(AudioStreamType.Media).Buffer;
            Assert.NotSame(first, second);
            Assert.InRange(second.TargetMs, 124.9, 125); // 44.1 kHz rounds to whole frames
            Assert.Equal(0, second.Counters.Underruns);

            // A restart with a new format (audioStart while started) keeps it as well, and so does another type:
            // the network is the same for every stream.
            r.OnAudioStart("media", "pcm_s16le", 48000, 1);
            Assert.InRange(r.GetStream(AudioStreamType.Media).Buffer.TargetMs, 124.9, 125);
            r.OnAudioStart("alt", "pcm_s16le", 24000, 1);
            Assert.InRange(r.GetStream(AudioStreamType.Alt).Buffer.TargetMs, 124.9, 125);

            // Forgetting puts the streams back to the minimum, the playing ones included.
            r.ForgetLearnedTarget();
            Assert.Equal(0, r.LearnedTargetMs);
            Assert.Equal(20, r.GetStream(AudioStreamType.Media).Buffer.TargetMs, 6);
            r.OnAudioStart("telephony", "pcm_s16le", 16000, 1);
            Assert.Equal(20, r.GetStream(AudioStreamType.Telephony).Buffer.TargetMs, 6);
        }

        [Fact]
        public void TheLearnedDepthIsSeededFromTheLastRunUnderEachStreamsCap()
        {
            var r = new AudioReceiver(0, sink, 80, 2000, 1000, () => now);
            r.InheritLearnedTarget(1800);
            Assert.Equal(1800, r.LearnedTargetMs);
            r.OnAudioStart("media", "pcm_s16le", 48000, 2);
            r.OnAudioStart("alt", "pcm_s16le", 24000, 1);
            r.OnAudioStart("telephony", "pcm_s16le", 16000, 1);
            Assert.Equal(1800, r.GetStream(AudioStreamType.Media).Buffer.TargetMs, 6);
            Assert.Equal(AudioReceiver.AltMaxTargetMs, r.GetStream(AudioStreamType.Alt).Buffer.TargetMs, 6);
            Assert.Equal(AudioReceiver.TelephonyMaxTargetMs, r.GetStream(AudioStreamType.Telephony).Buffer.TargetMs, 6);

            // Seeding never lowers, and what was seeded is not reported as newly learned.
            var learned = new List<double>();
            r.TargetLearned += learned.Add;
            r.InheritLearnedTarget(500);
            Assert.Equal(1800, r.LearnedTargetMs);
            r.Tick(now);
            Assert.Empty(learned);

            // The minimum from the settings applies to streams started afterwards.
            r.SetTargetMs(120);
            r.ForgetLearnedTarget();
            r.OnAudioStart("media", "pcm_s16le", 48000, 2);
            Assert.Equal(120, r.GetStream(AudioStreamType.Media).Buffer.TargetMs, 6);
        }

        [Fact]
        public void OutputStatusAndErrorsAppearInTheStats()
        {
            var r = NewReceiver();
            r.OutputStatus = () => "No output device: receiving only";
            r.ReportError("No audio output device: audio is received but not played");
            r.Tick(now);
            Assert.Equal("No output device: receiving only", r.Stats.Output);
            Assert.Equal("No audio output device: audio is received but not played", r.Stats.LastError);
        }

        [Fact]
        public void ReceivesOverUdpOnLoopback()
        {
            using (var r = new AudioReceiver(0, sink, 20, 200))
            {
                r.Start();
                Assert.True(r.Listening);
                Assert.NotEqual(0, r.BoundPort);
                using (var client = new UdpClient())
                {
                    var target = new IPEndPoint(IPAddress.Loopback, r.BoundPort);
                    for (var i = 0; i < 10; i++)
                    {
                        var d = Datagram(AudioStreamType.Telephony, i, i == 0, 16000, 1, 80);
                        client.Send(d, d.Length, target);
                    }
                }
                var watch = Stopwatch.StartNew();
                AudioStream stream = null;
                while (watch.ElapsedMilliseconds < 5000)
                {
                    stream = r.GetStream(AudioStreamType.Telephony);
                    if (stream != null && stream.Buffer.Counters.Received == 10) break;
                    Thread.Sleep(10);
                }
                Assert.NotNull(stream);
                Assert.Equal(10, stream.Buffer.Counters.Received);
                Assert.Equal(16000, stream.SampleRate);
                Assert.Equal(1, stream.Channels);
                Assert.Contains("+telephony", sink.Events);
            }
            Assert.Contains("-telephony", sink.Events); // Dispose stops every stream
        }

        [Fact]
        public void AnOccupiedPortIsReportedNotThrown()
        {
            using (var blocker = new UdpClient(new IPEndPoint(IPAddress.Any, 0)))
            {
                var port = ((IPEndPoint)blocker.Client.LocalEndPoint).Port;
                using (var r = new AudioReceiver(port, sink))
                {
                    r.Start();
                    Assert.False(r.Listening);
                    r.Tick(now);
                    Assert.Contains("could not be bound", r.Stats.LastError);
                    Assert.False(r.Stats.Listening);
                }
            }
        }
    }
}

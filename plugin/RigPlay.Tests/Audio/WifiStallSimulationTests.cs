// SPDX-License-Identifier: GPL-3.0-only
// WifiStallSimulationTests.cs: the jitter buffer against the network of the second rig test (2026-10-03): a tablet
// on Wi-Fi whose uplink holds the audio datagrams back for a few hundred milliseconds every few seconds and then
// releases them in a burst, with the plugin page reading 190–230 pkt/s, 0 % loss, 18 underruns and 10 skips in
// 100 s. The simulation runs a real-time sender (5 ms datagrams at 48 kHz stereo) and a real-time output through a
// scripted sequence of stalls, and asserts what the user hears: after the first stall or two the buffer has learned
// the depth the network needs and nothing cuts out again, no matter how many stalls follow.
using System;
using System.Collections.Generic;
using RigPlayPlugin.Audio;
using Xunit;

namespace RigPlayPlugin.Tests.Audio
{
    public class WifiStallSimulationTests
    {
        private const int Rate = 48000;
        private const int Channels = 2;
        private const int DatagramFrames = 240; // 5 ms
        private const int OutputChunkFrames = 480; // the output pulls 10 ms at a time

        /// <summary>A stall: datagrams due from <see cref="At"/> are held for <see cref="Ms"/> and then all delivered at once.</summary>
        private struct Stall
        {
            public double At;
            public double Ms;
        }

        private sealed class Result
        {
            public long Underruns;
            public long Skips;
            public long SilenceAfterLearning;
            public long UnderrunsAfterLearning;
            public double FinalTargetMs;
            public double LongestStallMs;
            public double MaxBufferMs;
            public readonly List<double> UnderrunTimes = new List<double>();
        }

        /// <summary>
        /// Runs <paramref name="seconds"/> of a continuous stream through <paramref name="buffer"/>. Time advances in 1 ms
        /// steps: the sender emits a datagram every 5 ms (delivered at once, or at the end of the stall that covers its
        /// send time); the output reads 10 ms of audio every 10 ms. Counters after <paramref name="learningSeconds"/>
        /// are what the user heard once the buffer had seen the network.
        /// </summary>
        private static Result Run(JitterBuffer buffer, IList<Stall> stalls, double seconds, double learningSeconds)
        {
            var result = new Result();
            var pending = new List<KeyValuePair<double, byte[]>>(); // delivery time, datagram
            var samples = new short[DatagramFrames * Channels];
            for (var i = 0; i < samples.Length; i++) samples[i] = (short)(i * 7); // not silence, so a skip is distinguishable
            var output = new byte[OutputChunkFrames * Channels * 2];
            ushort seq = 0;
            uint timestamp = 0;
            long silenceAtLearningEnd = -1;
            long underrunsAtLearningEnd = -1;
            var lastUnderruns = 0L;
            var totalMs = (long)(seconds * 1000);
            for (long ms = 0; ms <= totalMs; ms++)
            {
                if (ms % 5 == 0)
                {
                    var deliverAt = (double)ms;
                    foreach (var stall in stalls)
                    {
                        if (ms >= stall.At && ms < stall.At + stall.Ms) deliverAt = stall.At + stall.Ms;
                    }
                    var header = AudioHeader.Create(seq, AudioStreamType.Media, seq == 0, timestamp, Rate, Channels);
                    var datagram = header.Encode(samples);
                    pending.Add(new KeyValuePair<double, byte[]>(deliverAt, datagram));
                    seq++;
                    timestamp += DatagramFrames;
                }
                // The uplink is a queue: what it held back comes out in the order it went in.
                var delivered = 0;
                while (delivered < pending.Count && pending[delivered].Key <= ms)
                {
                    var d = pending[delivered].Value;
                    AudioHeader h;
                    AudioHeader.TryParse(d, 0, d.Length, out h);
                    buffer.Push(h.Seq, h.Timestamp, h.IsStart, d, AudioHeader.Size, d.Length - AudioHeader.Size);
                    delivered++;
                }
                if (delivered > 0) pending.RemoveRange(0, delivered);
                if (ms % 10 == 0)
                {
                    buffer.Read(output, 0, output.Length);
                    var c = buffer.Counters;
                    if (c.Underruns > lastUnderruns)
                    {
                        lastUnderruns = c.Underruns;
                        result.UnderrunTimes.Add(ms / 1000.0);
                    }
                    var depth = c.BufferedFrames * 1000.0 / Rate;
                    if (depth > result.MaxBufferMs) result.MaxBufferMs = depth;
                }
                if (ms == (long)(learningSeconds * 1000))
                {
                    var c = buffer.Counters;
                    silenceAtLearningEnd = c.SilenceFrames;
                    underrunsAtLearningEnd = c.Underruns;
                }
            }
            var final = buffer.Counters;
            result.Underruns = final.Underruns;
            result.Skips = final.Overflows;
            result.SilenceAfterLearning = silenceAtLearningEnd < 0 ? final.SilenceFrames : final.SilenceFrames - silenceAtLearningEnd;
            result.UnderrunsAfterLearning = underrunsAtLearningEnd < 0 ? final.Underruns : final.Underruns - underrunsAtLearningEnd;
            result.FinalTargetMs = final.TargetFrames * 1000.0 / Rate;
            result.LongestStallMs = final.LongestStallFrames * 1000.0 / Rate;
            return result;
        }

        /// <summary>Stalls of the given lengths, one every <paramref name="everySeconds"/>.</summary>
        private static List<Stall> Stalls(double everySeconds, double untilSeconds, params double[] lengthsMs)
        {
            var list = new List<Stall>();
            var i = 0;
            for (var t = 2.0; t < untilSeconds; t += everySeconds, i++)
            {
                list.Add(new Stall { At = t * 1000, Ms = lengthsMs[i % lengthsMs.Length] });
            }
            return list;
        }

        [Fact]
        public void TheRigTestsStallsStopCuttingTheAudioAfterTheFirstOnes()
        {
            // Stalls of 300–650 ms every 6 s, as the page's counters implied. With the old rules (target capped at 250 ms,
            // skip-ahead at 500 ms) every one of them was a dropout and many were followed by a skip.
            var buffer = new JitterBuffer(Rate, Channels);
            var stalls = Stalls(6, 118, 400, 300, 650, 500, 350, 600, 450);
            var r = Run(buffer, stalls, 120, 20);

            Assert.True(r.Underruns <= 3, "underruns " + r.Underruns + " at " + string.Join(", ", r.UnderrunTimes));
            Assert.Equal(0, r.Skips);
            Assert.Equal(0, r.UnderrunsAfterLearning);
            Assert.Equal(0, r.SilenceAfterLearning);
            Assert.InRange(r.FinalTargetMs, 650, 2000);
            Assert.InRange(r.LongestStallMs, 600, 700);
            // The excess the learning left behind was trimmed: the depth settles near the target plus a stall's worth.
            Assert.True(r.MaxBufferMs < r.FinalTargetMs + 1000, "max depth " + r.MaxBufferMs);
        }

        [Fact]
        public void AStallLongerThanAnythingLearnedCutsOnceAndThenNeverAgain()
        {
            var buffer = new JitterBuffer(Rate, Channels);
            var stalls = new List<Stall>
            {
                new Stall { At = 3000, Ms = 300 },
                new Stall { At = 12000, Ms = 1200 }, // far longer than the 375 ms learned from the first
                new Stall { At = 25000, Ms = 1200 },
                new Stall { At = 40000, Ms = 1100 },
                new Stall { At = 55000, Ms = 900 },
            };
            var r = Run(buffer, stalls, 70, 14);
            Assert.Equal(2, r.Underruns);
            Assert.Equal(0, r.Skips); // the burst after the long stall is kept, not thrown away
            Assert.Equal(0, r.UnderrunsAfterLearning);
            Assert.Equal(0, r.SilenceAfterLearning);
            Assert.InRange(r.FinalTargetMs, 1500, 2000);
        }

        [Fact]
        public void AQuietNetworkNeverTouchesTheTarget()
        {
            var buffer = new JitterBuffer(Rate, Channels);
            var r = Run(buffer, new List<Stall>(), 30, 0);
            Assert.Equal(0, r.Underruns);
            Assert.Equal(0, r.Skips);
            Assert.Equal(0, r.SilenceAfterLearning);
            Assert.Equal(JitterBuffer.DefaultTargetMs, r.FinalTargetMs, 3);
            Assert.InRange(r.MaxBufferMs, 70, 100);
        }

        [Fact]
        public void TheDepthLearnedLastTimeMakesTheFirstStallSilent()
        {
            // The plugin seeds a new stream (or a new SimHub session) with the depth saved in the settings.
            var buffer = new JitterBuffer(Rate, Channels);
            buffer.InheritTarget(750);
            var stalls = Stalls(6, 58, 400, 300, 650, 500);
            var r = Run(buffer, stalls, 60, 0);
            Assert.Equal(0, r.Underruns);
            Assert.Equal(0, r.Skips);
            Assert.Equal(0, r.SilenceAfterLearning);
            Assert.Equal(750, r.FinalTargetMs, 3);
        }

        [Fact]
        public void TheOldRulesWouldHaveCutOutOnEveryStall()
        {
            // Documents why the rules changed: a buffer capped where 0.2.0-rc.1 capped it (target up to 250 ms, skipping
            // 250 ms above it) keeps dropping out and skipping on the same stalls.
            var buffer = new JitterBuffer(Rate, Channels, 80, 250, 250);
            var stalls = Stalls(6, 118, 400, 300, 650, 500, 350, 600, 450);
            var r = Run(buffer, stalls, 120, 20);
            Assert.True(r.UnderrunsAfterLearning >= 8, "underruns after learning " + r.UnderrunsAfterLearning);
            Assert.True(r.Skips >= 3, "skips " + r.Skips);
        }
    }
}

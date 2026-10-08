// SPDX-License-Identifier: GPL-3.0-only
// JitterBufferTests.cs: the play-out buffer: fill to the target before playing, in-order and reordered
// play-out, silence for gaps with loss counted, late and duplicate drops, reset on the start flag, skip-ahead
// above the ceiling (which follows the target), underrun refill with the target learned from the measured stall
// (and not from a source pause), the learned target surviving resets and restarts, the quiet catch-up that trims
// excess depth, and sequence / timestamp wrap-around.
using System;
using System.Collections.Generic;
using System.Linq;
using RigPlayPlugin.Audio;
using Xunit;

namespace RigPlayPlugin.Tests.Audio
{
    public class JitterBufferTests
    {
        // 1 kHz mono keeps the numbers small: 10 frames per datagram = 10 ms; target 30 ms, skip ceiling 30 ms above it.
        private const int Rate = 1000;
        private const int Frames = 10;

        /// <summary>
        /// Catch-up is off unless a test asks for it (<paramref name="catchUpStartMs"/>): its start threshold is put just
        /// under the skip slack, where no other test's depth reaches.
        /// </summary>
        private static JitterBuffer NewBuffer(int targetMs = 30, int maxTargetMs = 60, int skipSlackMs = 30, int catchUpStartMs = -1, int catchUpStopMs = 5, int channels = 1)
        {
            if (catchUpStartMs < 0) catchUpStartMs = skipSlackMs - 1;
            return new JitterBuffer(Rate, channels, targetMs, maxTargetMs, skipSlackMs, catchUpStartMs, catchUpStopMs);
        }

        /// <summary>A datagram whose samples are all <paramref name="value"/>.</summary>
        private static byte[] Payload(short value, int frames = Frames, int channels = 1)
        {
            var bytes = new byte[frames * channels * 2];
            for (var i = 0; i < frames * channels; i++)
            {
                bytes[2 * i] = (byte)value;
                bytes[2 * i + 1] = (byte)(value >> 8);
            }
            return bytes;
        }

        /// <summary>A datagram whose sample at frame f, channel c is <paramref name="first"/> + f + <paramref name="channelOffset"/> × c.</summary>
        private static byte[] Ramp(int first, int frames = Frames, int channels = 1, int channelOffset = 0)
        {
            var bytes = new byte[frames * channels * 2];
            for (var f = 0; f < frames; f++)
            {
                for (var c = 0; c < channels; c++)
                {
                    var v = (short)(first + f + channelOffset * c);
                    var i = f * channels + c;
                    bytes[2 * i] = (byte)v;
                    bytes[2 * i + 1] = (byte)(v >> 8);
                }
            }
            return bytes;
        }

        private static bool Push(JitterBuffer b, int seq, long timestamp, short value, bool start = false)
        {
            var p = Payload(value, Frames, b.Channels);
            return b.Push((ushort)seq, (uint)timestamp, start, p, 0, p.Length);
        }

        private static short[] Read(JitterBuffer b, int frames)
        {
            var bytes = new byte[frames * b.BlockAlign];
            Assert.Equal(bytes.Length, b.Read(bytes, 0, bytes.Length));
            return AudioHeader.DecodeSamples(bytes, 0, bytes.Length);
        }

        [Fact]
        public void PlaysSilenceUntilTheTargetDepthIsReached()
        {
            var b = NewBuffer();
            Push(b, 0, 0, 1, start: true);
            Push(b, 1, 10, 2);
            Assert.False(b.IsPlaying);
            Assert.Equal(20, b.BufferedFrames);
            Assert.All(Read(b, 10), s => Assert.Equal(0, s));
            Assert.Equal(20, b.BufferedFrames); // filling does not consume

            Push(b, 2, 20, 3);
            Assert.True(b.IsPlaying);
            Assert.Equal(30, b.BufferedFrames);
            Assert.Equal(Enumerable.Repeat((short)1, 10).Concat(Enumerable.Repeat((short)2, 10)), Read(b, 20));
            Assert.Equal(10, b.BufferedFrames);
            Assert.Equal(0, b.Counters.SilenceFrames);
        }

        [Fact]
        public void ReadsAcrossDatagramBoundaries()
        {
            var b = NewBuffer(targetMs: 30);
            for (var i = 0; i < 4; i++) Push(b, i, i * Frames, (short)(i + 1), start: i == 0);
            var samples = Read(b, 25);
            Assert.Equal(new short[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3 }, samples);
            Assert.Equal(new short[] { 3, 3, 3, 3, 3, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4 }, Read(b, 15));
        }

        [Fact]
        public void FillsASequenceGapWithSilenceAndCountsTheLoss()
        {
            var b = NewBuffer(targetMs: 30);
            Push(b, 0, 0, 1, start: true);
            Push(b, 1, 10, 2);
            // seq 2 (timestamp 20) is lost
            Push(b, 3, 30, 4);
            Push(b, 4, 40, 5);
            Assert.True(b.IsPlaying);

            var samples = Read(b, 50);
            Assert.Equal(Enumerable.Repeat((short)1, 10)
                .Concat(Enumerable.Repeat((short)2, 10))
                .Concat(Enumerable.Repeat((short)0, 10))
                .Concat(Enumerable.Repeat((short)4, 10))
                .Concat(Enumerable.Repeat((short)5, 10)), samples);

            var c = b.Counters;
            Assert.Equal(10, c.SilenceFrames);
            Assert.Equal(1, c.Lost);
            Assert.Equal(4, c.Received);
            Assert.Equal(5, c.Expected);
            Assert.Equal(0, c.Underruns);
        }

        [Fact]
        public void PlaysSilenceForATimestampJumpWithoutLoss()
        {
            var b = NewBuffer(targetMs: 30);
            Push(b, 0, 0, 1, start: true);
            Push(b, 1, 25, 2); // the sender skipped 15 frames of audio
            Read(b, 0);
            var samples = Read(b, 35);
            Assert.Equal(Enumerable.Repeat((short)1, 10).Concat(Enumerable.Repeat((short)0, 15)).Concat(Enumerable.Repeat((short)2, 10)), samples);
            Assert.Equal(15, b.Counters.SilenceFrames);
            Assert.Equal(0, b.Counters.Lost);
        }

        [Fact]
        public void ReordersWithinTheBufferAndTakesTheLossBack()
        {
            var b = NewBuffer(targetMs: 40);
            Push(b, 0, 0, 1, start: true);
            Push(b, 2, 20, 3);
            Push(b, 3, 30, 4);
            Assert.Equal(1, b.Counters.Lost);
            Push(b, 1, 10, 2); // late but still before its play-out time
            Assert.Equal(0, b.Counters.Lost);
            Assert.True(b.IsPlaying);
            Assert.Equal(new short[] { 1, 2, 3, 4 }.SelectMany(v => Enumerable.Repeat(v, 10)), Read(b, 40));
            Assert.Equal(0, b.Counters.SilenceFrames);
        }

        [Fact]
        public void DropsADatagramThatArrivesAfterItsPlayOutTime()
        {
            var b = NewBuffer(targetMs: 20);
            Push(b, 0, 0, 1, start: true);
            Push(b, 2, 20, 3);
            Assert.True(b.IsPlaying);
            Read(b, 25); // 1, silence for the missing 2, half of 3
            Assert.False(Push(b, 1, 10, 2));
            var c = b.Counters;
            Assert.Equal(1, c.Late);
            Assert.Equal(3, c.Received);
            Assert.Equal(0, c.Lost); // it did arrive
        }

        [Fact]
        public void DropsDuplicates()
        {
            var b = NewBuffer(targetMs: 30);
            Push(b, 0, 0, 1, start: true);
            Push(b, 1, 10, 2);
            Assert.False(Push(b, 1, 10, 2));
            Assert.False(Push(b, 0, 0, 1, start: true)); // a repeated start datagram is not a new epoch
            Push(b, 2, 20, 3);
            Assert.True(b.IsPlaying);
            var c = b.Counters;
            Assert.Equal(2, c.Duplicates);
            Assert.Equal(3, c.Received);
            Assert.Equal(1, c.Resets);
        }

        [Fact]
        public void TheStartFlagResetsTheBuffer()
        {
            var b = NewBuffer(targetMs: 20);
            for (var i = 0; i < 5; i++) Push(b, i, i * Frames, 1, start: i == 0);
            Assert.True(b.IsPlaying);
            Read(b, 10);
            Assert.Equal(40, b.BufferedFrames);

            // A new stream (audioStart): seq and timestamp from 0 again, different content.
            Push(b, 0, 0, 7, start: true);
            Assert.False(b.IsPlaying);
            Assert.Equal(10, b.BufferedFrames);
            Push(b, 1, 10, 8);
            Assert.True(b.IsPlaying);
            Assert.Equal(Enumerable.Repeat((short)7, 10).Concat(Enumerable.Repeat((short)8, 10)), Read(b, 20));
            var c = b.Counters;
            Assert.Equal(2, c.Resets);
            Assert.Equal(0, c.Lost);
            Assert.Equal(0, c.Late);
        }

        [Fact]
        public void SkipsAheadToTheTargetAboveTheCeiling()
        {
            var b = NewBuffer(targetMs: 30, skipSlackMs: 30);
            Assert.Equal(60, b.MaxFrames);
            for (var i = 0; i < 3; i++) Push(b, i, i * Frames, (short)i, start: i == 0);
            Assert.True(b.IsPlaying);
            for (var i = 3; i < 6; i++) Push(b, i, i * Frames, (short)i);
            Assert.Equal(60, b.BufferedFrames);
            Push(b, 6, 60, 6); // 70 frames > 60: skip to 30 frames before the end
            Assert.Equal(30, b.BufferedFrames);
            Assert.Equal(40, b.Counters.OverflowFrames);
            Assert.Equal(1, b.Counters.Overflows);
            Assert.Equal(Enumerable.Range(4, 3).SelectMany(v => Enumerable.Repeat((short)v, 10)), Read(b, 30));
        }

        [Fact]
        public void TheCeilingFollowsTheTarget()
        {
            var b = NewBuffer(targetMs: 20, maxTargetMs: 200, skipSlackMs: 30);
            Assert.Equal(50, b.MaxFrames);
            b.InheritTarget(60);
            Assert.Equal(90, b.MaxFrames);
            for (var i = 0; i < 9; i++) Push(b, i, i * Frames, 1, start: i == 0);
            Assert.True(b.IsPlaying);
            Assert.Equal(90, b.BufferedFrames); // 90 > the old ceiling of 50, no skip
            Assert.Equal(0, b.Counters.Overflows);
            Push(b, 9, 90, 1);
            Assert.Equal(1, b.Counters.Overflows); // 100 > 90
            Assert.Equal(60, b.BufferedFrames);
        }

        [Fact]
        public void AnUnderrunPlaysSilenceLearnsFromTheStallAndRefillsToTheNewTargetBeforeResuming()
        {
            var b = NewBuffer(targetMs: 20, maxTargetMs: 200);
            Push(b, 0, 0, 1, start: true);
            Push(b, 1, 10, 2);
            Assert.Equal(Enumerable.Repeat((short)1, 10).Concat(Enumerable.Repeat((short)2, 10)).Concat(Enumerable.Repeat((short)0, 5)), Read(b, 25));
            var c = b.Counters;
            Assert.Equal(1, c.Underruns);
            Assert.Equal(5, c.SilenceFrames);
            Assert.False(c.Playing);
            Assert.Equal(20, b.TargetFrames); // measured when the datagrams come back, not now
            Assert.All(Read(b, 15), s => Assert.Equal(0, s)); // 20 ms of silence so far

            // The held-back datagrams arrive in a burst: nothing of them was due yet, so none is lost. Once the old
            // target is back the stall is known (20 drained, 20 of silence) and the target becomes a quarter more
            // than that; play-out resumes when that much is buffered.
            Push(b, 2, 20, 3);
            Assert.Equal(20, b.TargetFrames);
            Push(b, 3, 30, 4);
            Assert.Equal(50, b.TargetFrames);
            Assert.False(b.IsPlaying);
            Push(b, 4, 40, 5);
            Push(b, 5, 50, 6);
            Assert.False(b.IsPlaying);
            Push(b, 6, 60, 7);
            Assert.True(b.IsPlaying);
            Assert.Equal(50, b.BufferedFrames);
            Assert.Equal(Enumerable.Repeat((short)3, 10).Concat(Enumerable.Repeat((short)4, 10)), Read(b, 20));
            Assert.Equal(0, b.Counters.Late);
            Assert.Equal(40, b.Counters.LongestStallFrames);
        }

        [Fact]
        public void TheStallIsMeasuredFromTheDepthThereWasNotFromTheTarget()
        {
            // The depth sits below the target (the page's Forget raised nothing; here the target was inherited and the
            // stream then ran at 80): the stall is what the last datagram found buffered plus the silence.
            var b = NewBuffer(targetMs: 20, maxTargetMs: 200);
            b.InheritTarget(100);
            for (var i = 0; i < 10; i++) Push(b, i, i * Frames, 1, start: i == 0);
            Assert.True(b.IsPlaying);
            Read(b, 20);
            for (var i = 10; i < 12; i++)
            {
                Push(b, i, i * Frames, 1);
                Read(b, 10);
            }
            Assert.Equal(80, b.BufferedFrames); // live: every datagram finds 80 buffered and leaves 90
            Read(b, 110); // 80 played, 30 of silence: a stall of 120 since the last datagram, not of 130
            for (var i = 12; i < 27; i++) Push(b, i, i * Frames, 1);
            Assert.Equal(120, b.Counters.LongestStallFrames);
            Assert.Equal(150, b.TargetFrames);
            Assert.True(b.IsPlaying);
        }

        [Fact]
        public void TheTargetGrowsToAQuarterMoreThanEachLongerStallUpToTheCap()
        {
            var b = NewBuffer(targetMs: 20, maxTargetMs: 100, skipSlackMs: 1000);
            Assert.Equal(100, b.MaxTargetFrames);
            var seq = 0;
            void Burst(int datagrams)
            {
                for (var i = 0; i < datagrams; i++, seq++) Push(b, seq, seq * Frames, 1, start: seq == 0);
            }
            // Each round: play everything held plus some silence, then the delayed datagrams arrive in a burst.
            Burst(2);
            Read(b, 40); // 20 played, 20 silence: stall 40 -> target 50
            Burst(6);
            Assert.Equal(50, b.TargetFrames);
            Read(b, 110); // 60 played, 50 silence: stall 100 -> target 100 (capped from 125)
            Burst(10);
            Assert.Equal(100, b.TargetFrames);
            Read(b, 300); // 100 played, 200 silence: the cap holds
            Burst(10);
            Assert.Equal(100, b.TargetFrames);
            Assert.Equal(3, b.Counters.Underruns);
            Assert.Equal(300, b.Counters.LongestStallFrames);
        }

        [Fact]
        public void TheDepthAfterLearningAbsorbsTheSameStallAgain()
        {
            var b = NewBuffer(targetMs: 20, maxTargetMs: 200, skipSlackMs: 1000);
            for (var i = 0; i < 2; i++) Push(b, i, i * Frames, 1, start: i == 0);
            Read(b, 100); // 20 played, 80 silence: stall 100
            for (var i = 2; i < 15; i++) Push(b, i, i * Frames, 1); // the burst (100) and three live datagrams
            Assert.Equal(125, b.TargetFrames);
            Assert.True(b.IsPlaying);
            Assert.Equal(130, b.BufferedFrames);
            // The same stall again: 100 frames pass with nothing arriving, and nothing is heard.
            Read(b, 100);
            Assert.Equal(1, b.Counters.Underruns);
            Assert.Equal(80, b.Counters.SilenceFrames);
            for (var i = 15; i < 25; i++) Push(b, i, i * Frames, 1);
            Assert.Equal(125, b.TargetFrames);
            Assert.Equal(130, b.BufferedFrames);
        }

        [Fact]
        public void APausedSourceTeachesNothing()
        {
            // Datagrams stop because the phone paused the music. Two ways it shows: the sender declares the pause as a
            // timestamp jump (§10.2), or the datagrams come back at their normal pace instead of in a burst.
            var b = NewBuffer(targetMs: 30, maxTargetMs: 200, skipSlackMs: 1000);
            for (var i = 0; i < 3; i++) Push(b, i, i * Frames, 1, start: i == 0);
            Read(b, 90); // underrun after 30, 60 of silence
            // A 90-frame pause: the clock jumped over it, and the sender is live again.
            Push(b, 3, 120, 1);
            Push(b, 4, 130, 1);
            Push(b, 5, 140, 1);
            Assert.True(b.IsPlaying);
            Assert.Equal(30, b.TargetFrames);
            Assert.Equal(0, b.Counters.LongestStallFrames);

            Read(b, 100); // 30 played, 70 silence
            Assert.Equal(2, b.Counters.Underruns);
            // The datagrams come back one at a time with play-out silence in between: the sender is live, not catching
            // up, so the depth would not have helped.
            Push(b, 6, 150, 1);
            Read(b, 10);
            Push(b, 7, 160, 1);
            Read(b, 10);
            Push(b, 8, 170, 1);
            Assert.True(b.IsPlaying);
            Assert.Equal(30, b.TargetFrames);
            Assert.Equal(0, b.Counters.LongestStallFrames);

            // A real stall after that: the burst teaches as usual, with the earlier pauses not counted.
            Read(b, 60); // 30 played, 30 silence
            for (var i = 9; i < 17; i++) Push(b, i, 180 + (i - 9) * Frames, 1);
            Assert.True(b.IsPlaying);
            Assert.Equal(75, b.TargetFrames);
            Assert.Equal(60, b.Counters.LongestStallFrames);
            Assert.Equal(3, b.Counters.Underruns);
        }

        [Fact]
        public void TheMaximumTargetIsNeverBelowTheConfiguredOne()
        {
            var b = NewBuffer(targetMs: 30, maxTargetMs: 20);
            Assert.Equal(30, b.MaxTargetFrames);
            Push(b, 0, 0, 1, start: true);
            for (var i = 1; i < 3; i++) Push(b, i, i * Frames, 1);
            Read(b, 100);
            for (var i = 3; i < 6; i++) Push(b, i, i * Frames, 1);
            Assert.Equal(1, b.Counters.Underruns);
            Assert.Equal(30, b.TargetFrames);
            Assert.True(b.IsPlaying);
            Assert.Equal(100, b.Counters.LongestStallFrames); // measured all the same
        }

        [Fact]
        public void TheLearnedTargetSurvivesAResetAndTheStartFlag()
        {
            var b = NewBuffer(targetMs: 20, maxTargetMs: 200, skipSlackMs: 1000);
            for (var i = 0; i < 3; i++) Push(b, i, i * Frames, 1, start: i == 0);
            Read(b, 100); // 30 played, 70 silence: stall 100
            for (var i = 3; i < 16; i++) Push(b, i, i * Frames, 1);
            Assert.True(b.IsPlaying);
            Assert.Equal(125, b.TargetFrames);
            b.Reset();
            Assert.Equal(125, b.TargetFrames);
            for (var i = 0; i < 12; i++) Push(b, i, i * Frames, 2, start: i == 0);
            Assert.False(b.IsPlaying); // 120 frames: not the learned target yet
            Push(b, 12, 120, 2);
            Assert.True(b.IsPlaying);

            // "Forget" on the page: back to the configured target; the extra depth is trimmed, not dropped.
            b.ResetTarget(20);
            Assert.Equal(20, b.TargetFrames);
            Assert.Equal(130, b.BufferedFrames);
            Assert.Equal(0, b.Counters.Overflows);
        }

        [Fact]
        public void AResetWhileRefillingForgetsThatStallsMeasurement()
        {
            var b = NewBuffer(targetMs: 20, maxTargetMs: 200, skipSlackMs: 1000);
            for (var i = 0; i < 2; i++) Push(b, i, i * Frames, 1, start: i == 0);
            Read(b, 100);
            for (var i = 0; i < 2; i++) Push(b, i, i * Frames, 2, start: i == 0); // a new epoch, not the delayed datagrams
            Assert.True(b.IsPlaying);
            Assert.Equal(20, b.TargetFrames);
        }

        [Fact]
        public void InheritTargetOnlyRaisesAndClampsToTheMaximumTarget()
        {
            var b = NewBuffer(targetMs: 20, maxTargetMs: 100);
            b.InheritTarget(10);
            Assert.Equal(20, b.TargetFrames);
            b.InheritTarget(35);
            Assert.Equal(35, b.TargetFrames);
            b.InheritTarget(500);
            Assert.Equal(100, b.TargetFrames);
        }

        [Fact]
        public void AfterAnUnderrunOlderDatagramsAreLate()
        {
            var b = NewBuffer(targetMs: 10);
            Push(b, 0, 0, 1, start: true);
            Push(b, 2, 20, 3);
            Read(b, 40); // 1, gap, 3, underrun
            Assert.False(Push(b, 1, 10, 2));
            Assert.Equal(1, b.Counters.Late);
        }

        [Fact]
        public void ExcessDepthIsTrimmedQuietlyByPlayingSlightlyFaster()
        {
            var b = NewBuffer(targetMs: 30, maxTargetMs: 60, skipSlackMs: 1000, catchUpStartMs: 20, catchUpStopMs: 5);
            // 400 frames of a ramp (0, 1, 2, ...): 370 above the target.
            for (var i = 0; i < 40; i++)
            {
                var p = Ramp(i * Frames);
                b.Push((ushort)i, (uint)(i * Frames), i == 0, p, 0, p.Length);
            }
            Assert.True(b.IsPlaying);
            Assert.False(b.IsCatchingUp);

            var output = new List<short>();
            output.AddRange(Read(b, 200));
            Assert.True(b.IsCatchingUp);
            // 200 output frames consumed 203 input frames (give or take the rounding of the fractional position).
            Assert.InRange(b.BufferedFrames, 197, 198);
            output.AddRange(Read(b, 100));
            output.AddRange(Read(b, 60));
            // 360 output frames consumed about 365 input frames: within 5 of the target, catch-up stops at the next read.
            Assert.InRange(b.BufferedFrames, 34, 36);
            output.AddRange(Read(b, 5));
            Assert.False(b.IsCatchingUp);
            Assert.InRange(b.Counters.CatchUpFrames, 360, 365);

            // The output follows the ramp a little faster than real time: never a jump, never a step back.
            for (var i = 1; i < output.Count; i++)
            {
                Assert.InRange(output[i] - output[i - 1], 0, 2);
            }
            Assert.InRange(output[359], 362, 366);
            Assert.Equal(0, b.Counters.Overflows);
            Assert.Equal(0, b.Counters.SilenceFrames);
        }

        [Fact]
        public void CatchUpKeepsStereoChannelsInStep()
        {
            var b = NewBuffer(targetMs: 30, maxTargetMs: 60, skipSlackMs: 1000, catchUpStartMs: 20, catchUpStopMs: 5, channels: 2);
            for (var i = 0; i < 20; i++)
            {
                var p = Ramp(i * Frames, Frames, 2, 1000); // L = frame index, R = L + 1000
                b.Push((ushort)i, (uint)(i * Frames), i == 0, p, 0, p.Length);
            }
            var samples = Read(b, 100);
            Assert.True(b.IsCatchingUp);
            for (var f = 0; f < 100; f++)
            {
                Assert.Equal(1000, samples[2 * f + 1] - samples[2 * f]);
                if (f > 0) Assert.InRange(samples[2 * f] - samples[2 * (f - 1)], 0, 2);
            }
        }

        [Fact]
        public void CatchUpPlaysSilenceThroughAGapAndStopsAtAnUnderrun()
        {
            var b = NewBuffer(targetMs: 20, maxTargetMs: 60, skipSlackMs: 1000, catchUpStartMs: 20, catchUpStopMs: 5);
            for (var i = 0; i < 10; i++)
            {
                if (i == 5) continue; // lost
                Push(b, i, i * Frames, (short)(i + 1), start: i == 0);
            }
            var samples = Read(b, 90);
            Assert.True(b.Counters.CatchUpFrames > 0);
            Assert.Contains((short)0, samples);
            Assert.Contains((short)10, samples); // the last datagram was reached
            Assert.True(b.Counters.SilenceFrames >= 9);
            Read(b, 20);
            Assert.Equal(1, b.Counters.Underruns);
            Assert.False(b.IsCatchingUp);
            Assert.False(b.IsPlaying);
        }

        [Fact]
        public void HandlesSequenceAndTimestampWrapAround()
        {
            var b = NewBuffer(targetMs: 30);
            const long ts = 4294967290; // 6 frames before the 32-bit wrap
            Push(b, 65534, ts, 1);
            Push(b, 65535, ts + 10, 2);
            Push(b, 0, (ts + 20) & 0xffffffff, 3);
            Push(b, 2, (ts + 40) & 0xffffffff, 5); // seq 1 lost across the wrap
            Assert.True(b.IsPlaying);
            var samples = Read(b, 50);
            Assert.Equal(Enumerable.Repeat((short)1, 10)
                .Concat(Enumerable.Repeat((short)2, 10))
                .Concat(Enumerable.Repeat((short)3, 10))
                .Concat(Enumerable.Repeat((short)0, 10))
                .Concat(Enumerable.Repeat((short)5, 10)), samples);
            Assert.Equal(1, b.Counters.Lost);
            Assert.Equal(10, b.Counters.SilenceFrames);
        }

        [Fact]
        public void StereoFramesStayInterleaved()
        {
            var b = new JitterBuffer(Rate, 2, 10, 60);
            var p = new byte[] { 1, 0, 2, 0, 3, 0, 4, 0 }; // L1 R2 L3 R4
            Assert.True(b.Push(0, 0, true, p, 0, p.Length));
            Assert.Equal(2, b.BufferedFrames);
            Push(b, 1, 2, 9);
            Assert.True(b.IsPlaying);
            var bytes = new byte[8];
            b.Read(bytes, 0, 8);
            Assert.Equal(new short[] { 1, 2, 3, 4 }, AudioHeader.DecodeSamples(bytes, 0, 8));
        }

        [Fact]
        public void ReadAlwaysFillsTheRequestAndZeroesAPartialFrame()
        {
            var b = new JitterBuffer(Rate, 2, 0, 60);
            var bytes = Enumerable.Repeat((byte)0xAA, 7).ToArray();
            Assert.Equal(7, b.Read(bytes, 0, 7));
            Assert.All(bytes, x => Assert.Equal(0, x));
        }

        [Fact]
        public void TheDefaultsAreEightyMillisecondsLearningUpToTwoSecondsWithASecondOfSkipSlack()
        {
            var b = new JitterBuffer(48000, 2);
            Assert.Equal(3840, b.TargetFrames);
            Assert.Equal(96000, b.MaxTargetFrames); // the target grows to at most 2 s
            Assert.Equal(3840 + 48000, b.MaxFrames); // skip-ahead a second above the target
            Assert.Equal(4, b.BlockAlign);
            Assert.False(b.IsCatchingUp);
        }

        [Fact]
        public void TheRunningMaxEndMatchesTheOldRecalculationThroughReorderDropsAndReset()
        {
            // Non-regression for the incremental maxEnd: on an out-of-order sequence with drops, a full drain and a
            // reset, BufferedFrames and the skip-ahead must read the same end the old O(n) scan produced.
            var b = NewBuffer(targetMs: 20, maxTargetMs: 200, skipSlackMs: 1000);
            Push(b, 0, 0, 1, start: true); // end 10
            Push(b, 2, 20, 3); // end 30
            Push(b, 1, 10, 2); // out of order, end 20: does not lower the max
            Assert.True(b.IsPlaying);
            Assert.Equal(30, b.BufferedFrames); // 30 - readPos 0: the running max is the newest end, 30

            Push(b, 3, 30, 4); // end 40
            Assert.Equal(40, b.BufferedFrames);

            // Drain everything: once empty the depth is 0 regardless of the last max seen.
            Read(b, 40);
            Assert.Equal(0, b.BufferedFrames);

            // Refill, then reset: the max is forgotten and rebuilt from the new epoch.
            Push(b, 4, 40, 5);
            Push(b, 5, 50, 6);
            Assert.Equal(20, b.BufferedFrames); // ends 50 and 60, readPos 40
            b.Reset();
            Assert.Equal(0, b.BufferedFrames);
            Push(b, 0, 0, 7, start: true);
            Push(b, 1, 10, 8);
            Assert.Equal(20, b.BufferedFrames); // 20 - 0: rebuilt
        }

        [Fact]
        public void SkipAheadUsesTheRunningMaxEndOfTheNewestDatagram()
        {
            // The skip-ahead target is maxEnd - TargetFrames: once the depth tops the ceiling, play-out jumps to the
            // newest end minus the target. The running max feeds that jump without a scan.
            var b = NewBuffer(targetMs: 20, maxTargetMs: 200, skipSlackMs: 10);
            Assert.Equal(30, b.MaxFrames);
            Push(b, 0, 0, 1, start: true); // end 10
            Push(b, 1, 10, 2); // end 20: buffered 20 == target, play-out starts
            Assert.True(b.IsPlaying);
            Push(b, 2, 20, 3); // end 30: buffered 30 == ceiling, not over yet
            Assert.Equal(30, b.BufferedFrames);
            Assert.Equal(0, b.Counters.Overflows);
            Push(b, 3, 30, 4); // end 40 > ceiling 30: skip to maxEnd 40 - target 20 = readPos 20
            Assert.Equal(20, b.BufferedFrames);
            Assert.Equal(1, b.Counters.Overflows);
        }

        [Fact]
        public void PoolingReusesPacketsWithoutCorruptingWhatIsStillReadable()
        {
            // A long contiguous ramp, pushed with per-round reorder, read in lockstep: because the coverage is gapless
            // the output must be the ramp exactly, so a buffer recycled on drain is never one still being read. The
            // free-list has to be exercised (hits > 0) for the test to cover reuse at all.
            // A roomy ceiling (big skip slack) keeps the steady depth below it, so no audible skip reshuffles the
            // alignment: the output stays a faithful copy of the ramp and any corruption would show as a wrong sample.
            var b = NewBuffer(targetMs: 20, maxTargetMs: 60, skipSlackMs: 1000);
            short Expected(long frame) => (short)(frame % 7000);

            byte[] RampAt(long startFrame)
            {
                var bytes = new byte[Frames * 2];
                for (var f = 0; f < Frames; f++)
                {
                    var v = Expected(startFrame + f);
                    bytes[2 * f] = (byte)v;
                    bytes[2 * f + 1] = (byte)(v >> 8);
                }
                return bytes;
            }

            var seq = 0;
            var pushed = 0L; // frames pushed so far (contiguous from 0)
            var read = 0L;   // frames read so far (play-out starts at frame 0)

            void PushOne(long startFrame, bool start)
            {
                var p = RampAt(startFrame);
                b.Push((ushort)(startFrame / Frames), (uint)startFrame, start, p, 0, p.Length);
                seq++;
            }

            void PushPair()
            {
                // Push the later datagram first, then the earlier one, so each pair arrives out of order (no start flag
                // mid-stream: a reset would drop the companion datagram).
                PushOne(pushed + Frames, start: false);
                PushOne(pushed, start: false);
                pushed += 2 * Frames;
            }

            var all = new List<short>();
            PushOne(0, start: true); // epoch start, in order
            pushed = Frames;
            PushPair(); // frames 10..30 reorderd; depth now 30 >= target, play-out from frame 0
            Assert.True(b.IsPlaying);
            for (var round = 0; round < 80; round++)
            {
                PushPair();
                all.AddRange(Read(b, 20));
                read += 20;
            }
            for (var i = 0; i < read; i++) Assert.Equal(Expected(i), all[i]);
            Assert.True(b.Counters.PoolHits > 0, "the free-list was never reused");
            Assert.Equal(0, b.Counters.SilenceFrames); // gapless, so no silence

            // A reset recycles everything held; the next epoch starts a fresh ramp at frame 0 and reads back clean.
            b.Reset();
            var hitsBeforeReset = b.Counters.PoolHits;
            for (var i = 0; i < 3; i++) Push(b, i, i * Frames, (short)(i + 1), start: i == 0);
            Assert.True(b.IsPlaying);
            Assert.Equal(new short[] { 1, 2, 3 }.SelectMany(v => Enumerable.Repeat(v, 10)), Read(b, 30));
            Assert.True(b.Counters.PoolHits > hitsBeforeReset, "the reset returned its packets to the pool");
        }

        [Fact]
        public void TheFirstDatagramsAllocateThenLaterOnesComeFromThePool()
        {
            var b = NewBuffer(targetMs: 20);
            for (var i = 0; i < 3; i++) Push(b, i, i * Frames, 1, start: i == 0);
            Assert.True(b.IsPlaying);
            var afterFill = b.Counters;
            Assert.Equal(3, afterFill.PoolAllocations); // an empty pool allocates for the first datagrams
            Assert.Equal(0, afterFill.PoolHits);

            // Drain the oldest datagrams back into the pool, then feed more: a freed packet must be reused.
            Read(b, 30); // consumes all three, recycling them
            Push(b, 3, 30, 1);
            var c = b.Counters;
            Assert.True(c.PoolHits > 0, "a later datagram should reuse a freed packet");
            Assert.Equal(3, c.PoolAllocations); // no further allocation while the pool has entries
        }

        [Fact]
        public void ResetKeepsTheCounters()
        {
            var b = NewBuffer(targetMs: 10);
            Push(b, 0, 0, 1, start: true);
            Push(b, 2, 20, 3);
            b.Reset();
            Assert.Equal(0, b.BufferedFrames);
            Assert.False(b.IsPlaying);
            Assert.Equal(2, b.Counters.Received);
            Assert.Equal(1, b.Counters.Lost);
        }
    }
}

// SPDX-License-Identifier: GPL-3.0-only
// AudioMathAndStatsTests.cs: the volume curve, mute and ducking gains, the gain ramp, and the stats rates and
// display strings shown on the page.
using System;
using System.Linq;
using RigPlayPlugin.Audio;
using Xunit;

namespace RigPlayPlugin.Tests.Audio
{
    public class AudioMathTests
    {
        [Theory]
        [InlineData(100, 1.0)]
        [InlineData(50, 0.25)]
        [InlineData(10, 0.01)]
        [InlineData(0, 0.0)]
        [InlineData(150, 1.0)]
        [InlineData(-5, 0.0)]
        public void VolumeFollowsASquareLaw(int volume, double gain)
        {
            Assert.Equal(gain, AudioMath.VolumeToGain(volume, false), 5);
        }

        [Fact]
        public void HalfVolumeIsAboutMinusTwelveDecibels()
        {
            Assert.Equal(-12.04, AudioMath.GainToDb(AudioMath.VolumeToGain(50, false)), 2);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(50)]
        [InlineData(100)]
        public void MuteIsSilenceAtAnyVolume(int volume)
        {
            Assert.Equal(0f, AudioMath.VolumeToGain(volume, true));
        }

        [Fact]
        public void DuckingIsMinusTwelveDecibels()
        {
            Assert.Equal(0.2512, AudioMath.DuckGain, 4);
            Assert.Equal(-12.0, AudioMath.GainToDb(AudioMath.DuckGain), 4);
            Assert.Equal(1.0, AudioMath.DbToGain(0), 10);
            Assert.True(double.IsNegativeInfinity(AudioMath.GainToDb(0)));
        }

        [Theory]
        [InlineData(AudioStreamType.Media, false, false, 1.0)]
        [InlineData(AudioStreamType.Media, true, false, 0.2512)]
        [InlineData(AudioStreamType.Media, false, true, 0.2512)]
        [InlineData(AudioStreamType.Media, true, true, 0.2512)]
        [InlineData(AudioStreamType.Alt, true, true, 1.0)]
        [InlineData(AudioStreamType.Telephony, true, true, 1.0)]
        public void OnlyMediaIsDuckedWhileSiriOrACallPlays(AudioStreamType type, bool alt, bool telephony, double gain)
        {
            Assert.Equal(gain, AudioMath.StreamGain(type, alt, telephony), 4);
        }

        [Fact]
        public void AnotherProgramTalkingLowersMediaToTheTalkVolume()
        {
            // 25 % on the square-law curve is -24 dB; 50 % is -12 dB, the same as Siri's ducking.
            Assert.Equal(0.0625, AudioMath.TalkDuckGain(25), 4);
            Assert.Equal(0.0625, AudioMath.StreamGain(AudioStreamType.Media, false, false, true, AudioMath.TalkDuckGain(25)), 4);
            Assert.Equal(1.0, AudioMath.StreamGain(AudioStreamType.Media, false, false, false, AudioMath.TalkDuckGain(25)), 4);
            // With Siri at the same time the lower of the two applies.
            Assert.Equal(0.0625, AudioMath.StreamGain(AudioStreamType.Media, true, false, true, AudioMath.TalkDuckGain(25)), 4);
            Assert.Equal(0.2512, AudioMath.StreamGain(AudioStreamType.Media, true, false, true, AudioMath.TalkDuckGain(80)), 4);
            // Siri and calls themselves are never lowered by another program.
            Assert.Equal(1.0, AudioMath.StreamGain(AudioStreamType.Alt, false, false, true, 0f), 4);
            Assert.Equal(0.0, AudioMath.StreamGain(AudioStreamType.Media, false, false, true, AudioMath.TalkDuckGain(0)), 4);
        }

        [Fact]
        public void AConstantGainScalesEverySample()
        {
            var buffer = new float[] { 1, -1, 0.5f, -0.5f };
            var reached = AudioMath.ApplyGainRamp(buffer, 0, 4, 2, 0.5f, 0.5f);
            Assert.Equal(0.5f, reached);
            Assert.Equal(new[] { 0.5f, -0.5f, 0.25f, -0.25f }, buffer);
        }

        [Fact]
        public void UnityGainLeavesTheBufferAlone()
        {
            var buffer = new float[] { 0.1f, 0.2f };
            AudioMath.ApplyGainRamp(buffer, 0, 2, 2, 1f, 1f);
            Assert.Equal(new[] { 0.1f, 0.2f }, buffer);
        }

        [Fact]
        public void ARampMovesPerFrameAndEndsOnTheTarget()
        {
            var buffer = Enumerable.Repeat(1f, 8).ToArray(); // 4 stereo frames
            var reached = AudioMath.ApplyGainRamp(buffer, 0, 8, 2, 0f, 1f);
            Assert.Equal(1f, reached);
            Assert.Equal(new[] { 0.25f, 0.25f, 0.5f, 0.5f, 0.75f, 0.75f, 1f, 1f }, buffer);
        }

        [Fact]
        public void ARampRespectsTheOffset()
        {
            var buffer = Enumerable.Repeat(1f, 6).ToArray();
            AudioMath.ApplyGainRamp(buffer, 2, 4, 2, 1f, 0f);
            Assert.Equal(new[] { 1f, 1f, 0.5f, 0.5f, 0f, 0f }, buffer);
        }
    }

    public class AudioStatsTests
    {
        [Fact]
        public void RatesComeFromTheCounterDeltas()
        {
            var m = new RateMeter(2.0);
            m.Add(0.0, 0, 0);
            Assert.Equal(0, m.PacketsPerSecond);
            m.Add(0.5, 95, 5);
            Assert.Equal(190, m.PacketsPerSecond, 6);
            Assert.Equal(5.0, m.LossPercent, 6);
            m.Add(1.0, 195, 5);
            Assert.Equal(195, m.PacketsPerSecond, 6);
            Assert.Equal(2.5, m.LossPercent, 6);
        }

        [Fact]
        public void TheWindowSlides()
        {
            var m = new RateMeter(1.0);
            m.Add(0.0, 0, 0);
            m.Add(0.5, 50, 50); // a burst of loss early on
            m.Add(1.0, 150, 50);
            m.Add(1.5, 250, 50);
            m.Add(2.0, 350, 50);
            // window now 1.0 s .. 2.0 s: 200 received, no loss
            Assert.Equal(200, m.PacketsPerSecond, 6);
            Assert.Equal(0, m.LossPercent, 6);
        }

        [Fact]
        public void ATakenBackLossNeverGoesNegative()
        {
            var m = new RateMeter();
            m.Add(0, 10, 3);
            m.Add(1, 20, 1);
            Assert.Equal(0, m.LossPercent);
            Assert.Equal(10, m.PacketsPerSecond, 6);
        }

        [Theory]
        [InlineData(95, 5, 5.0)]
        [InlineData(0, 0, 0.0)]
        [InlineData(0, 10, 100.0)]
        [InlineData(200, 0, 0.0)]
        public void LossIsLostOverExpected(long received, long lost, double percent)
        {
            Assert.Equal(percent, RateMeter.Compute(received, lost), 6);
        }

        [Theory]
        [InlineData(48000, 2, "48 kHz stereo pcm_s16le")]
        [InlineData(44100, 2, "44.1 kHz stereo pcm_s16le")]
        [InlineData(16000, 1, "16 kHz mono pcm_s16le")]
        [InlineData(0, 0, "no format")]
        public void FormatsAreDescribed(int rate, int channels, string text)
        {
            Assert.Equal(text, AudioStats.DescribeFormat(rate, channels, AudioFormat.PcmS16le));
        }

        [Fact]
        public void AStreamLineShowsRateLossBufferAndFormat()
        {
            var s = new AudioStreamStats
            {
                Stream = AudioStreamType.Media,
                Started = true,
                Active = true,
                SampleRate = 48000,
                Channels = 2,
                Format = AudioFormat.PcmS16le,
                PacketsPerSecond = 199.6,
                LossPercent = 4.96,
                BufferMs = 81.7,
                TargetMs = 120,
                Underruns = 3,
                Late = 1,
                Skips = 2,
            };
            Assert.Equal("200 pkt/s · loss 5.0 % · buffer 82 ms (target 120) · 48 kHz stereo pcm_s16le · underruns 3 · longest stall 0 ms · late 1 · skips 2", s.ToDisplayString());
            s.LongestStallMs = 618.4;
            s.CatchingUp = true;
            Assert.Equal("200 pkt/s · loss 5.0 % · buffer 82 ms (target 120, trimming) · 48 kHz stereo pcm_s16le · underruns 3 · longest stall 618 ms · late 1 · skips 2", s.ToDisplayString());
            s.Underruns = 0;
            s.CatchingUp = false;
            Assert.DoesNotContain("longest stall", s.ToDisplayString());
            s.Underruns = 3;
            s.Active = false;
            s.AutoStarted = true;
            Assert.StartsWith("idle · ", s.ToDisplayString());
            Assert.Contains("pcm_s16le (auto-started) · underruns 3", s.ToDisplayString());
            Assert.Equal("stopped", new AudioStreamStats().ToDisplayString());
        }
    }
}

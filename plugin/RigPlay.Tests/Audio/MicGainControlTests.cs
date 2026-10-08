// SPDX-License-Identifier: GPL-3.0-only
// MicGainControlTests.cs: the microphone boost (#34): a fixed boost multiplies and clips; automatic mode climbs at
// 6 dB/s toward -6 dBFS peaks while someone talks, stops at the boost from the settings, holds on room noise and
// is cut at once when the output would clip; the gain in effect is reported for the page.
using System;
using System.Linq;
using RigPlayPlugin.Audio;
using Xunit;

namespace RigPlayPlugin.Tests.Audio
{
    public class MicGainControlTests
    {
        private const int Rate = 16000;
        private const int Buffer = 160; // 10 ms

        /// <summary>A buffer whose peak is <paramref name="peak"/> (0..1): a square wave of that amplitude.</summary>
        private static short[] Tone(double peak)
        {
            var v = (short)Math.Round(peak * 32767);
            return Enumerable.Range(0, Buffer).Select(i => (i % 2 == 0) ? v : (short)-v).ToArray();
        }

        /// <summary>Feeds <paramref name="seconds"/> of buffers at <paramref name="peak"/>; returns the last output peak.</summary>
        private static double Feed(MicGainControl control, double peak, double seconds)
        {
            var last = 0.0;
            for (var i = 0; i < seconds * Rate / Buffer; i++) last = control.Process(Tone(peak), 0, Buffer);
            return last;
        }

        [Fact]
        public void AFixedBoostMultipliesAndClips()
        {
            var control = new MicGainControl(Rate) { Automatic = false, BoostDb = 6.02 };
            var samples = new short[] { 1000, -1000, 20000, -20000 };
            var peak = control.Process(samples, 0, samples.Length);
            Assert.Equal(new short[] { 2000, -2000, short.MaxValue, short.MinValue }, samples);
            Assert.Equal(1.0, peak, 3);
            Assert.Equal(6.02, control.GainDb, 2);

            control.BoostDb = 0;
            var unity = new short[] { 123, -456 };
            Assert.Equal(456 / 32768.0, control.Process(unity, 0, 2), 6);
            Assert.Equal(new short[] { 123, -456 }, unity);
        }

        [Fact]
        public void TheBoostSettingIsClampedToItsRange()
        {
            var control = new MicGainControl(Rate) { BoostDb = 99 };
            Assert.Equal(MicGainControl.MaxBoostDb, control.BoostDb);
            control.BoostDb = -3;
            Assert.Equal(MicGainControl.MinBoostDb, control.BoostDb);
            Assert.Equal(MicGainControl.DefaultBoostDb, new MicGainControl(Rate).BoostDb);
            Assert.True(new MicGainControl(Rate).Automatic);
        }

        [Fact]
        public void AutomaticModeBringsQuietSpeechUpToTheTargetAtSixDecibelsPerSecond()
        {
            var control = new MicGainControl(Rate) { BoostDb = 30 };
            // Speech peaking at -26 dBFS needs +20 dB.
            var quiet = Math.Pow(10, -26 / 20.0);
            Feed(control, quiet, 1.0);
            Assert.InRange(control.GainDb, 5.5, 6.5);
            Feed(control, quiet, 2.0);
            Assert.InRange(control.GainDb, 17.5, 18.5);
            var peak = Feed(control, quiet, 1.0);
            Assert.InRange(control.GainDb, 19.5, 20.5);
            Assert.InRange(peak, 0.45, 0.55); // -6 dBFS
            Feed(control, quiet, 5.0);
            Assert.InRange(control.GainDb, 19.5, 20.5); // and no further
        }

        [Fact]
        public void AutomaticModeStopsAtTheBoostFromTheSettings()
        {
            var control = new MicGainControl(Rate) { BoostDb = 12 };
            Feed(control, Math.Pow(10, -30 / 20.0), 6.0);
            Assert.InRange(control.GainDb, 11.9, 12.0);
            control.BoostDb = 6;
            Feed(control, Math.Pow(10, -30 / 20.0), 0.01);
            Assert.InRange(control.GainDb, 5.9, 6.0); // lowered at once
        }

        [Fact]
        public void AutomaticModeDoesNotRiseOnRoomNoise()
        {
            var control = new MicGainControl(Rate);
            Feed(control, Math.Pow(10, -55 / 20.0), 10.0);
            Assert.Equal(0, control.GainDb, 6);
            // Talk, then fall silent: the gain gained while talking is held through the silence.
            Feed(control, Math.Pow(10, -26 / 20.0), 1.0);
            Assert.InRange(control.GainDb, 5.5, 6.5);
            Feed(control, 0.0, 5.0);
            Feed(control, Math.Pow(10, -55 / 20.0), 5.0);
            Assert.InRange(control.GainDb, 5.5, 6.5);
        }

        [Fact]
        public void ALoudBurstCutsTheGainAtOnceSoNothingClips()
        {
            var control = new MicGainControl(Rate) { BoostDb = 30 };
            Feed(control, Math.Pow(10, -30 / 20.0), 5.0);
            Assert.InRange(control.GainDb, 23.5, 24.5);
            var peak = control.Process(Tone(0.9), 0, Buffer);
            Assert.True(peak <= MicGainControl.CeilingPeak + 0.001, "peak " + peak);
            Assert.InRange(control.GainDb, 0, 0.8); // 0.98 / 0.9
            // Someone merely louder than before, short of clipping (-26 dBFS at +24 dB peaks at 0.79): the gain
            // comes down at 24 dB/s, not at once.
            Feed(control, Math.Pow(10, -30 / 20.0), 5.0);
            Assert.InRange(control.GainDb, 23.5, 24.5);
            Feed(control, Math.Pow(10, -26 / 20.0), 0.1);
            Assert.InRange(control.GainDb, 21.0, 22.0);
            Feed(control, Math.Pow(10, -26 / 20.0), 1.0);
            Assert.InRange(control.GainDb, 19.5, 20.5); // what -26 dBFS needs for -6
        }

        [Fact]
        public void SwitchingAutomaticOffAppliesTheFixedBoostAtOnce()
        {
            var control = new MicGainControl(Rate) { BoostDb = 10 };
            Feed(control, Math.Pow(10, -30 / 20.0), 0.5);
            Assert.InRange(control.GainDb, 2.5, 3.5);
            control.Automatic = false;
            control.Process(Tone(0.1), 0, Buffer);
            Assert.Equal(10, control.GainDb, 6);
        }
    }
}

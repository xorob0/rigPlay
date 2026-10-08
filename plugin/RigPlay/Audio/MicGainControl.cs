// SPDX-License-Identifier: GPL-3.0-only
// MicGainControl.cs: the boost applied to the PC microphone before it goes to the phone (#34). A PC microphone sits
// on a desk or a headset arm with the Windows input level wherever it was left, and Siri hears someone far away.
// Fixed mode multiplies by the boost from the settings. Automatic mode (the default) brings speech peaks up to about
// -6 dBFS with a gain between 0 dB and the boost from the settings: it climbs slowly while someone is talking (so
// a pause does not pump), never rises on background noise, and is cut at once when the output would clip. Pure
// (compiled into RigPlay.Tests); MicSender runs every captured buffer through it on the capture thread.
using System;

namespace RigPlayPlugin.Audio
{
    public sealed class MicGainControl
    {
        /// <summary>Bounds of the boost setting (dB).</summary>
        public const int MinBoostDb = 0;
        public const int MaxBoostDb = 30;
        public const int DefaultBoostDb = 20;

        /// <summary>Where automatic mode brings speech peaks.</summary>
        public const double TargetPeakDbfs = -6.0;

        /// <summary>Below this input peak the buffer is room noise: the gain does not rise on it.</summary>
        public const double GatePeakDbfs = -45.0;

        /// <summary>How fast the gain climbs toward what the level needs.</summary>
        public const double RiseDbPerSecond = 6.0;

        /// <summary>How fast it comes down when the speaker gets louder (short of clipping, which is instant).</summary>
        public const double FallDbPerSecond = 24.0;

        /// <summary>The output peak the gain is cut to at once.</summary>
        public const double CeilingPeak = 0.98;

        private readonly int sampleRate;
        private double gain = 1.0;

        public MicGainControl(int sampleRate)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            this.sampleRate = sampleRate;
        }

        /// <summary>Automatic: the gain follows the speech level up to <see cref="BoostDb"/>. Off: <see cref="BoostDb"/> is applied as is.</summary>
        public bool Automatic { get; set; } = true;

        /// <summary>The fixed boost, or the most automatic mode applies (dB, clamped to 0..30).</summary>
        public double BoostDb
        {
            get { return boostDb; }
            set { boostDb = Math.Max(MinBoostDb, Math.Min(MaxBoostDb, double.IsNaN(value) ? 0 : value)); }
        }

        private double boostDb = DefaultBoostDb;

        /// <summary>The gain in effect after the last buffer (dB).</summary>
        public double GainDb
        {
            get { return AudioMath.GainToDb(Automatic ? gain : AudioMath.DbToGain(BoostDb)); }
        }

        /// <summary>
        /// Applies the gain to <paramref name="count"/> mono samples in place (clipped to 16 bits) and returns the output
        /// peak as a fraction of full scale (0..1), for the level meter.
        /// </summary>
        public double Process(short[] samples, int offset, int count)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            if (count <= 0) return 0;
            var inputPeak = MicPacketizer.Peak(samples, offset, count);
            double g;
            if (!Automatic)
            {
                g = AudioMath.DbToGain(BoostDb);
                gain = g;
            }
            else
            {
                g = NextGain(inputPeak, count);
                gain = g;
            }
            if (Math.Abs(g - 1.0) < 1e-6) return inputPeak;
            var peak = 0;
            for (var i = offset; i < offset + count; i++)
            {
                var v = (int)Math.Round(samples[i] * g);
                if (v > short.MaxValue) v = short.MaxValue;
                else if (v < short.MinValue) v = short.MinValue;
                samples[i] = (short)v;
                var a = v == short.MinValue ? 32768 : Math.Abs(v);
                if (a > peak) peak = a;
            }
            return peak / 32768.0;
        }

        private double NextGain(double inputPeak, int count)
        {
            var max = AudioMath.DbToGain(BoostDb);
            var g = Math.Min(gain, max);
            if (inputPeak <= 0) return g;
            // Clipping: cut at once to the gain that just fits.
            if (inputPeak * g > CeilingPeak) return Math.Max(1.0, Math.Min(max, CeilingPeak / inputPeak));
            // Noise: hold what we have.
            if (AudioMath.GainToDb(inputPeak) < GatePeakDbfs) return g;
            var wanted = Math.Max(1.0, Math.Min(max, AudioMath.DbToGain(TargetPeakDbfs) / inputPeak));
            var seconds = count / (double)sampleRate;
            var currentDb = AudioMath.GainToDb(g);
            var wantedDb = AudioMath.GainToDb(wanted);
            double nextDb;
            if (wantedDb > currentDb) nextDb = Math.Min(wantedDb, currentDb + RiseDbPerSecond * seconds);
            else nextDb = Math.Max(wantedDb, currentDb - FallDbPerSecond * seconds);
            return AudioMath.DbToGain(nextDb);
        }
    }
}

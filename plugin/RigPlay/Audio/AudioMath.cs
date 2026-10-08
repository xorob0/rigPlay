// SPDX-License-Identifier: GPL-3.0-only
// AudioMath.cs: the gain arithmetic of the audio output: the master volume curve, mute, the media ducking
// while Siri or a call plays, and a click-free gain ramp over a float buffer. Pure: no SimHub, WPF or NAudio
// types (compiled into RigPlay.Tests); AudioOutput.cs wraps it in NAudio sample providers.
using System;

namespace RigPlayPlugin.Audio
{
    public static class AudioMath
    {
        /// <summary>The common mix format: every stream is converted to 48 kHz stereo float.</summary>
        public const int MixSampleRate = 48000;
        public const int MixChannels = 2;

        /// <summary>How far media is lowered while alt (Siri) or telephony plays (§10.3).</summary>
        public const double DuckDb = -12.0;

        public static readonly float DuckGain = (float)DbToGain(DuckDb);

        public static double DbToGain(double db)
        {
            return Math.Pow(10.0, db / 20.0);
        }

        public static double GainToDb(double gain)
        {
            return gain <= 0 ? double.NegativeInfinity : 20.0 * Math.Log10(gain);
        }

        /// <summary>
        /// The master gain for a 0-100 volume: a square-law curve (50 % is -12 dB, 25 % is -24 dB), which tracks
        /// loudness better than a linear one. Out-of-range values are clamped; mute is 0.
        /// </summary>
        public static float VolumeToGain(int volume, bool muted)
        {
            if (muted) return 0f;
            var v = Math.Min(100, Math.Max(0, volume)) / 100.0;
            return (float)(v * v);
        }

        /// <summary>The gain for one stream: media is ducked while alt or telephony is active; the others play at 1.</summary>
        public static float StreamGain(AudioStreamType type, bool altActive, bool telephonyActive)
        {
            return StreamGain(type, altActive, telephonyActive, false, 1f);
        }

        /// <summary>
        /// As above, and while another program on the PC talks (<paramref name="externalTalking"/>, #58) media is lowered to
        /// <paramref name="externalGain"/>; with Siri or a call at the same time the lower of the two gains applies.
        /// </summary>
        public static float StreamGain(AudioStreamType type, bool altActive, bool telephonyActive, bool externalTalking, float externalGain)
        {
            if (type != AudioStreamType.Media) return 1f;
            var gain = 1f;
            if (altActive || telephonyActive) gain = DuckGain;
            if (externalTalking && externalGain < gain) gain = externalGain < 0 ? 0f : externalGain;
            return gain;
        }

        /// <summary>The media gain for the talk watch's volume setting (percent of normal): the same square-law curve as the master volume.</summary>
        public static float TalkDuckGain(int percent)
        {
            return VolumeToGain(percent, false);
        }

        /// <summary>
        /// Multiplies <paramref name="count"/> interleaved samples by a gain that moves linearly from
        /// <paramref name="current"/> to <paramref name="target"/> over the buffer (per frame, so the channels stay
        /// in step), and returns the gain reached, to pass as <paramref name="current"/> next time. A constant gain
        /// of 1 leaves the buffer untouched.
        /// </summary>
        public static float ApplyGainRamp(float[] buffer, int offset, int count, int channels, float current, float target)
        {
            if (channels < 1) channels = 1;
            var frames = count / channels;
            if (frames == 0) return target;
            if (current == target)
            {
                if (target == 1f) return target;
                for (var i = 0; i < count; i++) buffer[offset + i] *= target;
                return target;
            }
            var step = (target - current) / frames;
            var g = current;
            var n = offset;
            for (var f = 0; f < frames; f++)
            {
                g += step;
                for (var c = 0; c < channels; c++) buffer[n++] *= g;
            }
            for (; n < offset + count; n++) buffer[n] *= target;
            return target;
        }
    }
}

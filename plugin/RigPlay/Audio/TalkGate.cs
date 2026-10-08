// SPDX-License-Identifier: GPL-3.0-only
// TalkGate.cs: decides whether a watched program (CrewChief, a spotter, Discord) is talking, from the peak level of
// its audio sessions sampled every few tens of milliseconds (#58). Speech is bursty: the peak drops to nothing
// between words, so the gate opens on the first sample above the threshold and closes only after the level has stayed
// under it for the hold time. Pure (compiled into RigPlay.Tests); TalkMonitor feeds it from WASAPI's session meters.
using System;

namespace RigPlayPlugin.Audio
{
    public sealed class TalkGate
    {
        /// <summary>A session peak above this (about -34 dBFS) is speech, not the noise floor of a muted program.</summary>
        public const double DefaultThreshold = 0.02;

        /// <summary>How long the level may stay under the threshold before the program counts as quiet again.</summary>
        public const int DefaultHoldMs = 700;

        private readonly double threshold;
        private readonly int holdMs;
        private long lastLoudMs = long.MinValue;

        public TalkGate(double threshold = DefaultThreshold, int holdMs = DefaultHoldMs)
        {
            if (threshold < 0 || threshold > 1) throw new ArgumentOutOfRangeException(nameof(threshold));
            if (holdMs < 0) throw new ArgumentOutOfRangeException(nameof(holdMs));
            this.threshold = threshold;
            this.holdMs = holdMs;
        }

        public bool Talking { get; private set; }

        /// <summary>The peak (0..1) of the last update, for the page.</summary>
        public double LastPeak { get; private set; }

        /// <summary>
        /// Feeds the highest peak of the watched sessions at <paramref name="nowMs"/> and returns true when the state
        /// changed. A peak below zero (no session to read) counts as silence.
        /// </summary>
        public bool Update(double peak, long nowMs)
        {
            LastPeak = peak < 0 ? 0 : peak;
            var was = Talking;
            if (peak > threshold)
            {
                lastLoudMs = nowMs;
                Talking = true;
            }
            else if (Talking && lastLoudMs != long.MinValue && nowMs - lastLoudMs >= holdMs)
            {
                Talking = false;
            }
            return Talking != was;
        }

        /// <summary>Back to quiet at once (the watch was turned off or the program went away).</summary>
        public bool Reset()
        {
            var was = Talking;
            Talking = false;
            LastPeak = 0;
            lastLoudMs = long.MinValue;
            return was;
        }
    }

    /// <summary>Which running programs the talk watch listens to: the settings' list against WASAPI session process names.</summary>
    public static class TalkProcessMatch
    {
        /// <summary>Process names match the list without regard to case or an ".exe" suffix.</summary>
        public static bool Matches(System.Collections.Generic.List<string> processes, string processName)
        {
            if (processes == null || string.IsNullOrEmpty(processName)) return false;
            var name = RigPlaySettings.NormalizeProcessName(processName);
            if (name.Length == 0) return false;
            foreach (var p in processes)
            {
                if (string.Equals(RigPlaySettings.NormalizeProcessName(p), name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}

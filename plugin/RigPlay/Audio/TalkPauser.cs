// SPDX-License-Identifier: GPL-3.0-only
// TalkPauser.cs: the "pause the music" mode of the talk watch (#58). The protocol only has a play/pause toggle
// (command media playPause, spec §6.8), so the pauser remembers what it did: when a watched program starts talking
// while the phone reports it is playing, it sends one toggle and marks the music as paused by itself; when the
// program goes quiet it sends one more toggle, unless the user resumed the music in between (the phone reports
// playing again). The phone's playing flag lags our own toggle by a status round trip, so for a moment after a
// toggle the pauser trusts what it did rather than what the phone says. Pure (compiled into RigPlay.Tests);
// RigPlay.cs runs it with the host's RunAction(PlayPause) and the surface's Playing flag.
using System;

namespace RigPlayPlugin.Audio
{
    public sealed class TalkPauser
    {
        /// <summary>How long after our own toggle the phone's playing flag is taken as not yet updated.</summary>
        public const int StatusLagMs = 1500;

        private readonly Func<bool> playing;
        private readonly Func<bool> sendPlayPause;
        private bool pausedByUs;
        private long toggledAtMs = long.MinValue;

        /// <param name="playing">The phone's current playing flag (status.nowPlaying.playing of the primary tablet).</param>
        /// <param name="sendPlayPause">Sends the toggle; false when no tablet took it.</param>
        public TalkPauser(Func<bool> playing, Func<bool> sendPlayPause)
        {
            this.playing = playing ?? throw new ArgumentNullException(nameof(playing));
            this.sendPlayPause = sendPlayPause ?? throw new ArgumentNullException(nameof(sendPlayPause));
        }

        /// <summary>True between the pause this sent and the resume it owes.</summary>
        public bool PausedByUs
        {
            get { return pausedByUs; }
        }

        /// <summary>Toggles sent, for the page.</summary>
        public long Toggles { get; private set; }

        /// <summary>A watched program started (<paramref name="talking"/>) or stopped talking at <paramref name="nowMs"/>. Returns true when a toggle was sent.</summary>
        public bool OnTalking(bool talking, long nowMs)
        {
            if (talking)
            {
                if (pausedByUs || !PlayingNow(nowMs)) return false;
                if (!sendPlayPause()) return false;
                Toggles++;
                pausedByUs = true;
                toggledAtMs = nowMs;
                return true;
            }
            if (!pausedByUs) return false;
            // The user pressed play while the program talked: nothing to give back.
            var resumedByUser = PlayingNow(nowMs);
            pausedByUs = false;
            if (resumedByUser) return false;
            if (!sendPlayPause()) return false;
            Toggles++;
            toggledAtMs = nowMs;
            return true;
        }

        /// <summary>Forgets a pause it owes (the mode was switched or the music source changed).</summary>
        public void Reset()
        {
            pausedByUs = false;
            toggledAtMs = long.MinValue;
        }

        private bool PlayingNow(long nowMs)
        {
            // Right after our own toggle the phone has not reported the new state yet: assume the toggle worked.
            if (toggledAtMs != long.MinValue && nowMs - toggledAtMs < StatusLagMs) return !pausedByUs;
            return playing();
        }
    }
}

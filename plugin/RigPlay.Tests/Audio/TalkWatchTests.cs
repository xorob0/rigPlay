// SPDX-License-Identifier: GPL-3.0-only
// TalkWatchTests.cs: the talk watch (#58) without WASAPI: TalkGate opens on the first loud sample and closes only after
// the hold time of quiet, so the gaps between words do not flicker; TalkPauser sends exactly one play/pause toggle when
// a watched program starts talking over playing music and one more when it stops, trusts its own toggle while the
// phone's status lags, and gives nothing back when the user resumed the music in between; the process name match.
using System.Collections.Generic;
using RigPlayPlugin.Audio;
using Xunit;

namespace RigPlayPlugin.Tests.Audio
{
    public class TalkGateTests
    {
        [Fact]
        public void OpensOnALoudSampleAndClosesAfterTheHold()
        {
            var g = new TalkGate(threshold: 0.02, holdMs: 700);
            Assert.False(g.Update(0.01, 0));
            Assert.False(g.Talking);
            Assert.True(g.Update(0.3, 50)); // speech starts
            Assert.True(g.Talking);
            Assert.False(g.Update(0.0, 100)); // a gap between words
            Assert.False(g.Update(0.0, 700));
            Assert.True(g.Talking);
            Assert.False(g.Update(0.25, 740)); // the next word resets the hold
            Assert.False(g.Update(0.0, 1400));
            Assert.True(g.Talking);
            Assert.True(g.Update(0.0, 1440)); // 700 ms after the last word
            Assert.False(g.Talking);
        }

        [Fact]
        public void NoSessionCountsAsSilenceAndResetClosesAtOnce()
        {
            var g = new TalkGate();
            g.Update(0.5, 0);
            Assert.True(g.Talking);
            Assert.False(g.Update(-1, 100)); // no session to read: not yet quiet
            Assert.Equal(0, g.LastPeak);
            Assert.True(g.Reset());
            Assert.False(g.Talking);
            Assert.False(g.Reset());
        }

        [Fact]
        public void TheThresholdSeparatesSpeechFromTheNoiseFloor()
        {
            var g = new TalkGate();
            for (var t = 0; t < 5000; t += 50) g.Update(0.019, t);
            Assert.False(g.Talking);
            Assert.True(g.Update(0.021, 5000));
        }
    }

    public class TalkPauserTests
    {
        private bool playing = true;
        private int toggles;
        private bool accept = true;

        private TalkPauser NewPauser()
        {
            return new TalkPauser(() => playing, () =>
            {
                if (!accept) return false;
                toggles++;
                playing = !playing; // the phone obeys, but reports it later (see the lag tests)
                return true;
            });
        }

        [Fact]
        public void PausesWhenTalkingStartsOverMusicAndResumesWhenItStops()
        {
            var p = NewPauser();
            Assert.True(p.OnTalking(true, 1000));
            Assert.True(p.PausedByUs);
            Assert.Equal(1, toggles);
            Assert.False(p.OnTalking(true, 1200)); // still talking: nothing more
            Assert.True(p.OnTalking(false, 4000));
            Assert.False(p.PausedByUs);
            Assert.Equal(2, toggles);
            Assert.True(playing);
            Assert.Equal(2, p.Toggles);
        }

        [Fact]
        public void DoesNothingWhenTheMusicWasNotPlaying()
        {
            playing = false;
            var p = NewPauser();
            Assert.False(p.OnTalking(true, 1000));
            Assert.False(p.OnTalking(false, 3000));
            Assert.Equal(0, toggles);
        }

        [Fact]
        public void GivesNothingBackWhenTheUserResumedDuringTheTalk()
        {
            var p = NewPauser();
            Assert.True(p.OnTalking(true, 1000));
            playing = true; // the user pressed play on the phone
            Assert.False(p.OnTalking(false, 5000));
            Assert.Equal(1, toggles);
            Assert.False(p.PausedByUs);
        }

        [Fact]
        public void TrustsItsOwnToggleWhileThePhonesStatusLags()
        {
            var p = NewPauser();
            Assert.True(p.OnTalking(true, 1000));
            playing = true; // the status still says playing: the phone has not reported the pause yet
            Assert.True(p.OnTalking(false, 1800)); // within the lag: resume anyway
            Assert.Equal(2, toggles);
            // And right after the resume toggle, a new talk burst pauses again even though the status lags.
            playing = false;
            Assert.True(p.OnTalking(true, 2000));
            Assert.Equal(3, toggles);
        }

        [Fact]
        public void AToggleNobodyTookIsNotRemembered()
        {
            accept = false;
            var p = NewPauser();
            Assert.False(p.OnTalking(true, 1000));
            Assert.False(p.PausedByUs);
            accept = true;
            Assert.False(p.OnTalking(false, 2000));
            Assert.Equal(0, toggles);
        }

        [Fact]
        public void ResetForgetsAnOwedResume()
        {
            var p = NewPauser();
            p.OnTalking(true, 1000);
            p.Reset();
            Assert.False(p.OnTalking(false, 3000));
            Assert.Equal(1, toggles);
        }
    }

    public class TalkProcessMatchTests
    {
        [Fact]
        public void ProcessNamesMatchWithoutCaseOrExe()
        {
            var list = new List<string> { "CrewChiefV4", "Discord" };
            Assert.True(TalkProcessMatch.Matches(list, "crewchiefv4"));
            Assert.True(TalkProcessMatch.Matches(list, "CrewChiefV4.exe"));
            Assert.True(TalkProcessMatch.Matches(list, "discord"));
            Assert.False(TalkProcessMatch.Matches(list, "CrewChief"));
            Assert.False(TalkProcessMatch.Matches(list, ""));
            Assert.False(TalkProcessMatch.Matches(list, null));
        }
    }
}

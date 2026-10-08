// SPDX-License-Identifier: GPL-3.0-only
// SimHubSurfaceTests.cs: the primary-tablet rule (docs/protocol.md §12), the position extrapolation (§6.7), the
// commands behind the SimHub actions (§16.2), and the properties and actions end to end with fake tablets (#23).
using System;
using System.Linq;
using RigPlayPlugin.Protocol;
using Xunit;

namespace RigPlayPlugin.Tests
{
    public class SimHubSurfaceTests
    {
        private static TabletCandidate C(int id, bool paired, bool phone, long phoneOrder, long pairedOrder)
        {
            return new TabletCandidate { SessionId = id, Paired = paired, PhoneConnected = phone, PhoneConnectedOrder = phoneOrder, PairedOrder = pairedOrder };
        }

        [Fact]
        public void NoPairedSessionMeansNoPrimary()
        {
            Assert.Equal(0, PrimaryTabletSelector.Select(new TabletCandidate[0]));
            Assert.Equal(0, PrimaryTabletSelector.Select(new[] { C(1, false, true, 5, 0) }));
        }

        [Fact]
        public void WithoutPhonesTheMostRecentlyPairedSessionIsPrimary()
        {
            Assert.Equal(2, PrimaryTabletSelector.Select(new[] { C(1, true, false, 0, 1), C(2, true, false, 0, 3), C(3, false, false, 0, 9) }));
        }

        [Fact]
        public void TheSessionWhosePhoneConnectedMostRecentlyIsPrimary()
        {
            Assert.Equal(1, PrimaryTabletSelector.Select(new[] { C(1, true, true, 7, 1), C(2, true, true, 4, 3), C(3, true, false, 0, 9) }));
        }

        [Theory]
        [InlineData(true, 10.0, 330.0, 2500, 12.5)]
        [InlineData(false, 10.0, 330.0, 2500, 10.0)]
        [InlineData(true, 329.0, 330.0, 5000, 330.0)]
        [InlineData(true, 0.0, null, 60000, 60.0)]
        public void PositionIsExtrapolatedWhilePlayingAndClampedToDuration(bool playing, double position, double? duration, long elapsedMs, double expected)
        {
            var np = new NowPlaying { Playing = playing, Position = position, Duration = duration };
            Assert.Equal(expected, PlaybackClock.Position(np, 1000, 1000 + elapsedMs), 6);
        }

        [Fact]
        public void NoNowPlayingMeansZero()
        {
            Assert.Equal(0, PlaybackClock.Position(null, 0, 1000));
            Assert.Equal("", SurfaceSnapshot.Empty.Title);
            Assert.Equal(Screens.Off, SurfaceSnapshot.Empty.Screen);
            Assert.False(SurfaceSnapshot.Empty.TabletConnected);
            Assert.Equal(0, SurfaceSnapshot.Empty.Duration);
        }

        [Theory]
        [InlineData(SurfaceAction.PlayPause, null, "{\"type\":\"command\",\"command\":\"media\",\"action\":\"playPause\"}")]
        [InlineData(SurfaceAction.NextTrack, null, "{\"type\":\"command\",\"command\":\"media\",\"action\":\"next\"}")]
        [InlineData(SurfaceAction.PreviousTrack, null, "{\"type\":\"command\",\"command\":\"media\",\"action\":\"previous\"}")]
        [InlineData(SurfaceAction.Siri, null, "{\"type\":\"command\",\"command\":\"media\",\"action\":\"siri\"}")]
        [InlineData(SurfaceAction.ShowDashboard, null, "{\"type\":\"command\",\"command\":\"showDashboard\"}")]
        [InlineData(SurfaceAction.ShowCarPlay, null, "{\"type\":\"command\",\"command\":\"showCarPlay\"}")]
        [InlineData(SurfaceAction.ToggleScreen, "dashboard", "{\"type\":\"command\",\"command\":\"showCarPlay\"}")]
        [InlineData(SurfaceAction.ToggleScreen, "carplay", "{\"type\":\"command\",\"command\":\"showDashboard\"}")]
        [InlineData(SurfaceAction.ToggleScreen, null, "{\"type\":\"command\",\"command\":\"showDashboard\"}")]
        public void EachActionSendsItsCommand(SurfaceAction action, string screen, string expected)
        {
            var command = SurfaceActions.CommandFor(action, screen);
            Assert.Equal(expected, MessageCodec.Encode(command));
            // Each command matches a fixture shape and decodes.
            Assert.IsType<CommandMessage>(MessageCodec.Decode(expected));
        }

        [Fact]
        public void ActionNamesMatchTheSpec()
        {
            Assert.Equal(new[] { "PlayPause", "NextTrack", "PreviousTrack", "Siri", "ShowDashboard", "ShowCarPlay", "ToggleScreen" },
                Enum.GetValues(typeof(SurfaceAction)).Cast<SurfaceAction>().Select(SurfaceActions.Name).ToArray());
        }

        // End to end

        private static string Status(bool phone, string screen, string title, bool playing = true, double position = 83.4)
        {
            var nowPlaying = title == null ? "null"
                : "{\"title\":\"" + title + "\",\"artist\":\"Massive Attack\",\"album\":null,\"app\":\"Spotify\",\"playing\":" + (playing ? "true" : "false")
                  + ",\"position\":" + position.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"duration\":330.0,\"updatedAt\":1791043200123}";
            return "{\"type\":\"status\",\"phoneConnected\":" + (phone ? "true" : "false") + ",\"screen\":\"" + screen + "\",\"nowPlaying\":" + nowPlaying + "}";
        }

        private static FakeTablet Pair(RigPlayHost host, string id, string name)
        {
            var t = new FakeTablet(host.Server.Port);
            t.Hello(id, name);
            host.Pairing.PinGenerator = () => "123456";
            t.Send(new PairRequestMessage());
            t.Expect<PairResultMessage>();
            t.Send(new PairRequestMessage { Pin = "123456" });
            Assert.True(t.Expect<PairResultMessage>().Ok);
            t.Expect<StateMessage>();
            return t;
        }

        [Fact]
        public void PropertiesDescribeThePrimaryTabletAndActionsReachOnlyIt()
        {
            var settings = new RigPlaySettings().Normalize();
            // The fake tablets do not send heartbeats here, so give them a watchdog longer than the test.
            var timings = ControlServerTests.FastNoWatchdog;
            var host = new RigPlayHost(settings, new HostEnvironment { PluginVersion = "0.1.0" }, null, timings)
            {
                ControlPortOverride = 0,
                BeaconEnabled = false,
                ProbeEnabled = false,
            };
            host.Start();
            try
            {
                Assert.False(host.Surface.TabletConnected);
                Assert.False(host.RunAction(SurfaceAction.NextTrack));

                using (var a = Pair(host, "tablet-a", "Driver"))
                using (var b = Pair(host, "tablet-b", "Passenger"))
                {
                    Assert.True(FakeTablet.WaitFor(() => host.Surface.TabletConnected));
                    // No phone anywhere: the most recently paired tablet (b) is primary.
                    Assert.True(FakeTablet.WaitFor(() => host.PrimarySession?.TabletId == "tablet-b"));
                    Assert.Equal(Screens.Off, host.Surface.Screen);

                    a.SendLine(Status(true, "carplay", "Teardrop"));
                    Assert.True(FakeTablet.WaitFor(() => host.Surface.Title == "Teardrop"));
                    Assert.Equal("tablet-a", host.PrimarySession.TabletId);
                    Assert.True(host.Surface.PhoneConnected);
                    Assert.Equal("carplay", host.Surface.Screen);
                    Assert.Equal("Massive Attack", host.Surface.Artist);
                    Assert.Equal("", host.Surface.Album);
                    Assert.Equal("Spotify", host.Surface.App);
                    Assert.True(host.Surface.Playing);
                    Assert.Equal(330.0, host.Surface.Duration);
                    var p1 = host.NowPlayingPosition;
                    Assert.InRange(p1, 83.4, 90);
                    System.Threading.Thread.Sleep(300);
                    Assert.True(host.NowPlayingPosition > p1, "position should advance while playing");

                    Assert.True(host.RunAction(SurfaceAction.NextTrack));
                    var command = a.Expect<CommandMessage>();
                    Assert.Equal(Commands.Media, command.Command);
                    Assert.Equal(Commands.Next, command.Action);

                    host.RunAction(SurfaceAction.ToggleScreen);
                    Assert.Equal(Commands.ShowDashboard, a.Expect<CommandMessage>().Command);
                    a.SendLine(Status(true, "dashboard", "Teardrop"));
                    Assert.True(FakeTablet.WaitFor(() => host.Surface.Screen == "dashboard"));
                    host.RunAction(SurfaceAction.ToggleScreen);
                    Assert.Equal(Commands.ShowCarPlay, a.Expect<CommandMessage>().Command);

                    // b's phone connects later: b becomes primary.
                    b.SendLine(Status(true, "carplay", "Unfinished Sympathy", playing: false, position: 12));
                    Assert.True(FakeTablet.WaitFor(() => host.Surface.Title == "Unfinished Sympathy"));
                    Assert.False(host.Surface.Playing);
                    Assert.Equal(12.0, host.NowPlayingPosition);
                    host.RunAction(SurfaceAction.Siri);
                    Assert.Equal(Commands.Siri, b.Expect<CommandMessage>().Action);

                    // a received nothing for that: its next message is a heartbeat or nothing, not a command.
                    Assert.DoesNotContain(a.Drain(300), m => m is CommandMessage);

                    // b drops its phone: a (still on a phone) is primary again.
                    b.SendLine(Status(false, "idle", null));
                    Assert.True(FakeTablet.WaitFor(() => host.PrimarySession?.TabletId == "tablet-a"));
                }
                Assert.True(FakeTablet.WaitFor(() => !host.Surface.TabletConnected));
                Assert.Equal("", host.Surface.Title);
                Assert.Equal(0, host.NowPlayingPosition);
            }
            finally
            {
                host.Stop();
            }
        }
    }
}

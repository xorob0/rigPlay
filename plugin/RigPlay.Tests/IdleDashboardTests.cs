// SPDX-License-Identifier: GPL-3.0-only
// IdleDashboardTests.cs: the idle dashboard of #38 end to end: state.idleDashboardUrl only for tablets that named
// feature idleDashboard (docs/protocol.md §6.6, §7.3), pushed within 1 s when only the idle choice changes and left out
// again when it is cleared; RigPlay.Screen reports `idle` from status.screen (§16.1); the screen actions (§16.2).
using System;
using System.Collections.Generic;
using RigPlayPlugin.Protocol;
using Xunit;

namespace RigPlayPlugin.Tests
{
    public class IdleDashboardTests
    {
        private static readonly DashboardTests.TinyHttpServer Http = new DashboardTests.TinyHttpServer();

        private static RigPlayHost NewHost(RigPlaySettings settings)
        {
            // A web dash server this test owns, so the probe result cannot flip under it.
            settings.WebDashPort = Http.Port;
            var host = new RigPlayHost(settings, new HostEnvironment { PluginVersion = "0.1.0" }, null, ControlServerTests.FastNoWatchdog)
            {
                ControlPortOverride = 0,
                BeaconEnabled = false,
                ProbeEnabled = false,
            };
            host.Start();
            // Probe once now, so the later re-probes after a dashboard change find the same result and change nothing.
            host.Probe.ProbeNow();
            host.Pairing.PinGenerator = () => "123456";
            return host;
        }

        private static StateMessage Pair(FakeTablet t, string id, List<string> features)
        {
            var hello = FakeTablet.NewHello(id, name: id);
            hello.Features = features;
            t.Send(hello);
            t.Expect<WelcomeMessage>();
            t.Send(new PairRequestMessage());
            t.Expect<PairResultMessage>();
            t.Send(new PairRequestMessage { Pin = "123456" });
            Assert.True(t.Expect<PairResultMessage>().Ok);
            return t.Expect<StateMessage>();
        }

        [Fact]
        public void TheIdleUrlGoesOnlyToTabletsWithTheFeatureAndFollowsTheSelection()
        {
            var settings = new RigPlaySettings { SelectedDashboard = "Pit Board", IdleDashboard = "Rig Clock" }.Normalize();
            var host = NewHost(settings);
            try
            {
                using (var with = new FakeTablet(host.Server.Port))
                using (var without = new FakeTablet(host.Server.Port))
                {
                    var a = Pair(with, "with-idle", new List<string> { Features.IdleDashboard });
                    var b = Pair(without, "without-idle", null);
                    var expected = "http://127.0.0.1:" + host.EffectiveWebDashPort + "/Dash#Rig%20Clock";
                    Assert.Equal(expected, a.IdleDashboardUrl);
                    Assert.Null(b.IdleDashboardUrl);
                    Assert.NotNull(b.DashboardUrl);
                    // On the wire the member is left out, not sent as null, for a tablet without the feature.
                    Assert.DoesNotContain(without.Received, l => l.Contains("idleDashboardUrl"));

                    // Only the idle choice changes: the tablet with the feature gets a new state within 1 s.
                    var before = DateTime.UtcNow;
                    settings.IdleDashboard = "Night Board";
                    host.DashboardSettingsChanged();
                    var changed = with.Expect<StateMessage>();
                    Assert.True((DateTime.UtcNow - before).TotalMilliseconds < 1000);
                    Assert.Equal("http://127.0.0.1:" + host.EffectiveWebDashPort + "/Dash#Night%20Board", changed.IdleDashboardUrl);
                    Assert.Equal(a.DashboardUrl, changed.DashboardUrl);
                    // The other tablet's state did not change, so it gets nothing (state is sent on change only).
                    Assert.DoesNotContain(without.Drain(300), m => m is StateMessage);

                    // Cleared ("(None)" on the page): the member disappears again.
                    settings.IdleDashboard = "";
                    host.DashboardSettingsChanged();
                    Assert.Null(with.Expect<StateMessage>().IdleDashboardUrl);
                    Assert.Contains("\"dashboardUrl\"", with.Received[with.Received.Count - 1]);
                    Assert.DoesNotContain("idleDashboardUrl", with.Received[with.Received.Count - 1]);
                }
            }
            finally
            {
                host.Stop();
            }
        }

        [Fact]
        public void ScreenReportsIdleAndTheScreenActionsReachThePrimaryTablet()
        {
            var host = NewHost(new RigPlaySettings().Normalize());
            try
            {
                using (var t = new FakeTablet(host.Server.Port))
                {
                    Pair(t, "tablet", new List<string> { Features.IdleDashboard });
                    Assert.True(FakeTablet.WaitFor(() => host.Surface.TabletConnected));
                    // Before any status: off (spec §16.1 "value without data").
                    Assert.Equal(Screens.Off, host.Surface.Screen);

                    t.Send(new StatusMessage { PhoneConnected = false, Screen = Screens.Idle });
                    Assert.True(FakeTablet.WaitFor(() => host.Surface.Screen == Screens.Idle));
                    Assert.False(host.Surface.PhoneConnected);

                    Assert.True(host.RunAction(SurfaceAction.ShowDashboard));
                    Assert.Equal(Commands.ShowDashboard, t.Expect<CommandMessage>().Command);
                    Assert.True(host.RunAction(SurfaceAction.ShowCarPlay));
                    Assert.Equal(Commands.ShowCarPlay, t.Expect<CommandMessage>().Command);
                    // From idle, toggling shows the dashboard.
                    Assert.True(host.RunAction(SurfaceAction.ToggleScreen));
                    Assert.Equal(Commands.ShowDashboard, t.Expect<CommandMessage>().Command);

                    t.Send(new StatusMessage { PhoneConnected = false, Screen = Screens.Dashboard });
                    Assert.True(FakeTablet.WaitFor(() => host.Surface.Screen == Screens.Dashboard));
                    Assert.True(host.RunAction(SurfaceAction.ToggleScreen));
                    Assert.Equal(Commands.ShowCarPlay, t.Expect<CommandMessage>().Command);
                }
                Assert.True(FakeTablet.WaitFor(() => host.Surface.Screen == Screens.Off));
            }
            finally
            {
                host.Stop();
            }
        }
    }
}

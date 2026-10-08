// SPDX-License-Identifier: GPL-3.0-only
// TelemetryTests.cs: the telemetry message of #40 (docs/protocol.md §6.9): gear and speed mapping, the heading tracker,
// the per-field switches, staleness, the settings, and the 10 Hz sender end to end with fake tablets (who gets it,
// and the final gameRunning: false).
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RigPlayPlugin.Protocol;
using RigPlayPlugin.Telemetry;
using Xunit;

namespace RigPlayPlugin.Tests
{
    public class TelemetryTests
    {
        internal static TelemetryInput Frame(double kmh = 100, string gear = "3", double rpm = 6120, double yaw = 0, bool running = true)
        {
            var f = TelemetryInput.Empty;
            f.GameRunning = running;
            f.SpeedKmh = kmh;
            f.Gear = gear;
            f.Rpm = rpm;
            f.YawDeg = yaw;
            f.TrackName = "Spa-Francorchamps";
            f.SessionType = "Race";
            return f;
        }

        [Theory]
        [InlineData("R", false, "R")]
        [InlineData("r", true, "R")]
        [InlineData("-1", false, "R")]
        [InlineData("N", false, "N")]
        [InlineData("0", false, "N")]
        [InlineData("N", true, "P")]
        [InlineData("0", true, "P")]
        [InlineData("1", false, "D")]
        [InlineData("1", true, "D")]
        [InlineData("8", false, "D")]
        [InlineData(" 3 ", false, "D")]
        [InlineData("P", false, "P")]
        [InlineData("D", false, "D")]
        [InlineData("", false, null)]
        [InlineData(null, false, null)]
        [InlineData("?", false, null)]
        public void TheSimGearMapsToPRND(string gear, bool inPits, string expected)
        {
            Assert.Equal(expected, TelemetrySampler.MapGear(gear, inPits));
        }

        [Fact]
        public void SpeedIsMetresPerSecondNeverNegative()
        {
            Assert.Equal(27.78, TelemetrySampler.SpeedMps(100));
            Assert.Equal(0.0, TelemetrySampler.SpeedMps(-3));
            Assert.Null(TelemetrySampler.SpeedMps(double.NaN));
            Assert.Null(TelemetrySampler.SpeedMps(double.PositiveInfinity));
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(-90, 270)]
        [InlineData(360, 0)]
        [InlineData(725, 5)]
        [InlineData(-0.0000001, 359.9999999)]
        public void HeadingsAreNormalised(double input, double expected)
        {
            Assert.Equal(expected, HeadingTracker.Normalize(input), 6);
        }

        [Fact]
        public void HeadingComesFromYawOnceTheGamePublishesItOtherwiseFromMovement()
        {
            var h = new HeadingTracker();
            var f = Frame(yaw: 0);
            f.X = 0;
            f.Z = 0;
            h.Update(ref f);
            Assert.True(double.IsNaN(h.Heading), "yaw 0 alone says nothing, and one position gives no direction");
            // Moving east (+X) by 5 m with yaw stuck at 0: the direction of travel.
            f.X = 5;
            h.Update(ref f);
            Assert.Equal(90, h.Heading, 6);
            // Less than a metre: no change.
            f.X = 5.5;
            f.Z = -0.2;
            h.Update(ref f);
            Assert.Equal(90, h.Heading, 6);
            // The game starts publishing yaw: it wins from then on, even when it returns to exactly 0.
            f.YawDeg = -45;
            h.Update(ref f);
            Assert.Equal(315, h.Heading, 6);
            f.YawDeg = 0;
            f.X = 100;
            h.Update(ref f);
            Assert.Equal(0, h.Heading, 6);
            h.Reset();
            Assert.True(double.IsNaN(h.Heading));
        }

        [Fact]
        public void AFullFrameBecomesAFullMessageAndSwitchesRemoveFields()
        {
            var sampler = new TelemetrySampler();
            var f = Frame(kmh: 150.12, gear: "4", rpm: 6120.4, yaw: 274.5);
            sampler.Update(ref f, 10.0);
            var all = sampler.Build(new TelemetrySettings(), 10.05);
            Assert.True(all.GameRunning);
            Assert.Equal(41.7, all.SpeedMps);
            Assert.Equal("D", all.Gear);
            Assert.Equal(274.5, all.Heading);
            Assert.Equal(6120.0, all.Rpm);
            Assert.Equal("Spa-Francorchamps", all.TrackName);
            Assert.Equal("Race", all.SessionType);
            Assert.Null(all.Lat);
            Assert.Null(all.Lon);
            // The encoding is a valid telemetry line.
            var decoded = Assert.IsType<TelemetryMessage>(MessageCodec.Decode(MessageCodec.Encode(all)));
            Assert.True(MessageCodec.Equivalent(all, decoded));

            var none = sampler.Build(new TelemetrySettings
            {
                SendSpeed = false, SendGear = false, SendHeading = false, SendRpm = false, SendTrackName = false, SendSessionType = false,
            }, 10.05);
            Assert.Equal("{\"type\":\"telemetry\",\"gameRunning\":true}", MessageCodec.Encode(none));
        }

        [Fact]
        public void WithoutARunningGameOrWithStaleFramesOnlyGameRunningFalseIsLeft()
        {
            var sampler = new TelemetrySampler();
            Assert.Equal("{\"type\":\"telemetry\",\"gameRunning\":false}", MessageCodec.Encode(sampler.Build(new TelemetrySettings(), 1.0)));
            var f = Frame();
            sampler.Update(ref f, 1.0);
            Assert.True(sampler.Build(new TelemetrySettings(), 3.9).GameRunning);
            // No frame for more than 3 s: SimHub stalled; the game counts as stopped.
            Assert.Equal("{\"type\":\"telemetry\",\"gameRunning\":false}", MessageCodec.Encode(sampler.Build(new TelemetrySettings(), 4.1)));
            var stopped = Frame(running: false);
            sampler.Update(ref stopped, 5.0);
            Assert.Equal("{\"type\":\"telemetry\",\"gameRunning\":false}", MessageCodec.Encode(sampler.Build(new TelemetrySettings(), 5.0)));
        }

        [Fact]
        public void AFieldWithNothingKnownIsLeftOut()
        {
            var sampler = new TelemetrySampler();
            var f = TelemetryInput.Empty;
            f.GameRunning = true;
            sampler.Update(ref f, 1.0);
            Assert.Equal("{\"type\":\"telemetry\",\"gameRunning\":true}", MessageCodec.Encode(sampler.Build(new TelemetrySettings(), 1.0)));
        }

        // Settings

        [Fact]
        public void TelemetrySettingsDefaultRepairAndRoundTrip()
        {
            var fresh = new RigPlaySettings().Normalize();
            Assert.True(fresh.Telemetry.Enabled);
            Assert.True(fresh.Telemetry.AnyFieldEnabled());
            Assert.Equal(GpsStrategies.Off, fresh.Telemetry.GpsStrategy);

            var repaired = new RigPlaySettings { Telemetry = null }.Normalize();
            Assert.NotNull(repaired.Telemetry);
            Assert.Equal(GpsStrategies.Off, new RigPlaySettings { Telemetry = new TelemetrySettings { GpsStrategy = "teleport" } }.Normalize().Telemetry.GpsStrategy);

            // A schema 2 file has no Telemetry: it gets the defaults and moves to the current schema.
            var old = JsonConvert.DeserializeObject<RigPlaySettings>("{\"SchemaVersion\":2,\"ControlPort\":23711,\"AudioPort\":23712}").Normalize();
            Assert.Equal(RigPlaySettings.CurrentSchemaVersion, old.SchemaVersion);
            Assert.True(old.Telemetry.SendSpeed);

            var custom = new RigPlaySettings { Telemetry = new TelemetrySettings { Enabled = false, SendRpm = false } }.Normalize();
            var copy = JsonConvert.DeserializeObject<RigPlaySettings>(JsonConvert.SerializeObject(custom)).Normalize();
            Assert.False(copy.Telemetry.Enabled);
            Assert.False(copy.Telemetry.SendRpm);
            Assert.True(copy.Telemetry.SendGear);

            var nothing = new TelemetrySettings
            {
                SendSpeed = false, SendGear = false, SendHeading = false, SendNight = false, SendFuel = false, SendRange = false,
                SendRpm = false, SendTrackName = false, SendSessionType = false,
            };
            Assert.False(nothing.AnyFieldEnabled());
        }

        // The sender, end to end

        internal static RigPlayHost NewHost(RigPlaySettings settings)
        {
            var host = new RigPlayHost(settings, new HostEnvironment { PluginVersion = "0.1.0" }, null, ControlServerTests.FastNoWatchdog)
            {
                ControlPortOverride = 0,
                BeaconEnabled = false,
                ProbeEnabled = false,
                TelemetryTimerEnabled = false,
            };
            host.Start();
            host.Pairing.PinGenerator = () => "123456";
            return host;
        }

        internal static FakeTablet Pair(RigPlayHost host, string id, List<string> features, bool phone)
        {
            var t = new FakeTablet(host.Server.Port);
            var hello = FakeTablet.NewHello(id, name: id);
            hello.Features = features;
            t.Send(hello);
            var welcome = t.Expect<WelcomeMessage>();
            Assert.Equal(features.Contains(Features.Telemetry), welcome.Features.Contains(Features.Telemetry));
            t.Send(new PairRequestMessage());
            t.Expect<PairResultMessage>();
            t.Send(new PairRequestMessage { Pin = "123456" });
            Assert.True(t.Expect<PairResultMessage>().Ok);
            t.Expect<StateMessage>();
            t.Send(new StatusMessage { PhoneConnected = phone, Screen = phone ? Screens.CarPlay : Screens.Idle });
            Assert.True(FakeTablet.WaitFor(() => host.Server.PairedSessions.Any(s => s.TabletId == id && s.LastStatus != null)));
            return t;
        }

        [Fact]
        public void TelemetryGoesToPairedTabletsWithTheFeatureAndAPhoneUntilTheGameStops()
        {
            var settings = new RigPlaySettings().Normalize();
            var host = NewHost(settings);
            try
            {
                var sender = host.TelemetrySender;
                Assert.Equal(0, sender.Tick());
                Assert.Equal("No paired tablet with a connected iPhone asks for telemetry", sender.Idle);

                using (var carplay = Pair(host, "with-phone", new List<string> { Features.Telemetry, Features.IdleDashboard }, true))
                using (var noPhone = Pair(host, "no-phone", new List<string> { Features.Telemetry }, false))
                using (var noFeature = Pair(host, "no-feature", new List<string> { Features.IdleDashboard }, true))
                {
                    // No game yet: nothing.
                    Assert.Equal(0, sender.Tick());
                    Assert.Equal("No game running", sender.Idle);

                    var f = Frame(kmh: 90, gear: "N");
                    f.InPitLane = true;
                    host.TelemetrySampler.Update(ref f);
                    Assert.Equal(1, sender.Tick());
                    var m = carplay.Expect<TelemetryMessage>();
                    Assert.True(m.GameRunning);
                    Assert.Equal(25.0, m.SpeedMps);
                    Assert.Equal("P", m.Gear);
                    Assert.Null(sender.Idle);

                    // Master switch off, then no field on: nothing.
                    settings.Telemetry.Enabled = false;
                    Assert.Equal(0, sender.Tick());
                    settings.Telemetry.Enabled = true;
                    settings.Telemetry.SendSpeed = settings.Telemetry.SendGear = settings.Telemetry.SendHeading = settings.Telemetry.SendNight = false;
                    settings.Telemetry.SendFuel = settings.Telemetry.SendRange = settings.Telemetry.SendRpm = false;
                    settings.Telemetry.SendTrackName = settings.Telemetry.SendSessionType = false;
                    Assert.Equal(0, sender.Tick());
                    Assert.Equal("No field is switched on", sender.Idle);
                    settings.Telemetry.SendSpeed = true;

                    host.TelemetrySampler.Update(ref f);
                    Assert.Equal(1, sender.Tick());
                    Assert.Equal("{\"type\":\"telemetry\",\"speedMps\":25.0,\"gameRunning\":true}", MessageCodec.Encode(carplay.Expect<TelemetryMessage>()));

                    // The game stops: one last gameRunning: false, then silence.
                    var stopped = Frame(running: false);
                    host.TelemetrySampler.Update(ref stopped);
                    Assert.Equal(1, sender.Tick());
                    Assert.Equal("{\"type\":\"telemetry\",\"gameRunning\":false}", MessageCodec.Encode(carplay.Expect<TelemetryMessage>()));
                    Assert.Equal(0, sender.Tick());
                    Assert.Equal(0, sender.Tick());

                    // The tablet without a phone and the one without the feature never got any.
                    Assert.DoesNotContain(noPhone.Drain(200), x => x is TelemetryMessage);
                    Assert.DoesNotContain(noFeature.Drain(200), x => x is TelemetryMessage);
                    Assert.DoesNotContain(carplay.Drain(200), x => x is TelemetryMessage);

                    // The phone of the second tablet connects: it is eligible from the next tick.
                    noPhone.Send(new StatusMessage { PhoneConnected = true, Screen = Screens.CarPlay });
                    Assert.True(FakeTablet.WaitFor(() => TelemetrySender.Eligible(host.Server).Count == 2));
                    host.TelemetrySampler.Update(ref f);
                    Assert.Equal(2, sender.Tick());
                    Assert.IsType<TelemetryMessage>(noPhone.Expect<TelemetryMessage>());
                }
            }
            finally
            {
                host.Stop();
            }
        }

        [Fact]
        public void ASlowSendDoesNotHoldTheNextTickOrStop()
        {
            // A tablet that never reads: once its receive window and the plugin's send buffer are full, the plugin's
            // write blocks. A big trackName makes one telemetry line large enough to fill them. The I/O now runs
            // outside tickLock, so while that first send is stuck a second Tick() and Stop() still take tickLock and
            // return at once (well under the 3 s socket send timeout). If the I/O ran under tickLock they would block.
            var host = NewHost(new RigPlaySettings().Normalize());
            try
            {
                using (var t = Pair(host, "tablet", new List<string> { Features.Telemetry }, true))
                {
                    var f = Frame();
                    f.TrackName = new string('x', 4 * 1024 * 1024); // ~4 MB line: larger than the socket buffers
                    host.TelemetrySampler.Update(ref f);
                    var sender = host.TelemetrySender;
                    var settings = host.Settings;

                    // The tablet never drains, so this Tick blocks inside the socket write (which now runs outside
                    // tickLock). The write holds no lock while it is stuck.
                    var blocked = new System.Threading.Thread(() => { try { sender.Tick(); } catch { } });
                    blocked.Start();

                    // Give the first send time to reach and block in the write.
                    System.Threading.Thread.Sleep(300);

                    // Switch the master off so a second Tick does no I/O: it only needs tickLock, then returns Quiet.
                    // While the first send is still stuck, this must take tickLock and return at once; if the I/O ran
                    // under tickLock the lock would still be held and this Join would time out.
                    settings.Telemetry.Enabled = false;
                    var secondTick = new System.Threading.Thread(() => { try { sender.Tick(); } catch { } });
                    secondTick.Start();
                    Assert.True(secondTick.Join(2000), "a second Tick() blocked behind a slow send holding tickLock");

                    // Stop() takes tickLock too: it must return promptly rather than wait for the slow send.
                    var stopper = new System.Threading.Thread(() => sender.Stop());
                    stopper.Start();
                    Assert.True(stopper.Join(2000), "Stop() blocked behind a slow send holding tickLock");

                    // The blocked send eventually unblocks or times out (the 3 s socket send timeout) and the thread exits.
                    Assert.True(blocked.Join(6000));
                }
            }
            finally
            {
                host.Stop();
            }
        }

        [Fact]
        public void EveryTickSendsOneMessageAndTheTimerTicksOnItsOwn()
        {
            // The rate is the timer's 100 ms interval (TelemetrySender.IntervalMs, spec §6.9: at most 10 per second);
            // counting real timer ticks over a second is not a test that survives a loaded CI runner, so the rate is
            // checked by driving Tick() and the timer only has to show that it runs.
            var host = NewHost(new RigPlaySettings().Normalize());
            try
            {
                using (var t = Pair(host, "tablet", new List<string> { Features.Telemetry }, true))
                {
                    var f = Frame();
                    host.TelemetrySampler.Update(ref f);
                    for (var i = 0; i < 10; i++) Assert.Equal(1, host.TelemetrySender.Tick());
                    Assert.Equal(10, t.Drain(300).OfType<TelemetryMessage>().Count());
                    Assert.Equal(100, TelemetrySender.IntervalMs);

                    var stop = false;
                    var feeder = new System.Threading.Thread(() =>
                    {
                        while (!System.Threading.Volatile.Read(ref stop))
                        {
                            var frame = Frame();
                            host.TelemetrySampler.Update(ref frame);
                            System.Threading.Thread.Sleep(16);
                        }
                    });
                    feeder.Start();
                    host.TelemetrySender.Start();
                    var seen = t.Drain(1500).OfType<TelemetryMessage>().Count();
                    host.TelemetrySender.Stop();
                    System.Threading.Volatile.Write(ref stop, true);
                    feeder.Join();
                    Assert.InRange(seen, 1, 16); // it ticks; never more often than every 100 ms
                }
            }
            finally
            {
                host.Stop();
            }
        }
    }
}

// SPDX-License-Identifier: GPL-3.0-only
// VehicleStatusAndNavTests.cs: the plugin halves of #45 (night from the in-game clock, headlights or a custom property,
// with the Auto/Day/Night override), #46 (fuelPercent, rangeKm) and #47 (status.nav and artwork on the wire, the
// RigPlay.Nav.* and RigPlay.NowPlaying.ArtworkPath properties, the artwork file).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RigPlayPlugin.Protocol;
using RigPlayPlugin.Telemetry;
using Xunit;

namespace RigPlayPlugin.Tests
{
    public sealed class VehicleStatusAndNavTests : IDisposable
    {
        private readonly string temp = Path.Combine(Path.GetTempPath(), "rigplay-artwork-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(temp, true); } catch { }
        }

        // #45 night

        private static TelemetryInput NightInput(double tod = double.NaN, int lights = -1, int custom = -1)
        {
            var f = TelemetryInput.Empty;
            f.GameRunning = true;
            f.TimeOfDaySec = tod;
            f.Headlights = lights;
            f.CustomNight = custom;
            return f;
        }

        [Theory]
        [InlineData("auto", 12 * 3600.0, -1, -1, false)]
        [InlineData("auto", 6 * 3600.0 + 3599, -1, -1, true)]
        [InlineData("auto", 7 * 3600.0, -1, -1, false)]
        [InlineData("auto", 19 * 3600.0, -1, -1, true)]
        [InlineData("auto", 23 * 3600.0, 0, -1, true)]
        [InlineData("auto", 86400.0 + 12 * 3600, -1, -1, false)]
        [InlineData("auto", double.NaN, 1, -1, true)]
        [InlineData("auto", double.NaN, 0, -1, false)]
        [InlineData("auto", 12 * 3600.0, 0, 1, true)]
        [InlineData("auto", 2 * 3600.0, 1, 0, false)]
        [InlineData("day", 2 * 3600.0, 1, 1, false)]
        [InlineData("night", 12 * 3600.0, 0, 0, true)]
        public void NightFollowsTheOverrideThenCustomThenClockThenHeadlights(string mode, double tod, int lights, int custom, bool expected)
        {
            var f = NightInput(tod, lights, custom);
            Assert.Equal(expected, VehicleStatus.Night(mode, ref f));
        }

        [Fact]
        public void NightIsUnknownInAutoWithoutAnySource()
        {
            var f = NightInput();
            Assert.Null(VehicleStatus.Night(NightModes.Auto, ref f));
            Assert.False(VehicleStatus.Night(NightModes.Day, ref f));
        }

        [Fact]
        public void BoxedSimHubValuesBecomeNumbersAndFlags()
        {
            Assert.Equal(43200.0, NightSources.ToNumber(43200f));
            Assert.Equal(2.0, NightSources.ToNumber(2));
            Assert.Equal(1.0, NightSources.ToNumber(true));
            Assert.Equal(3600.0, NightSources.ToNumber(TimeSpan.FromHours(1)));
            Assert.Equal(1.5, NightSources.ToNumber("1.5"));
            Assert.True(double.IsNaN(NightSources.ToNumber(null)));
            Assert.True(double.IsNaN(NightSources.ToNumber("dark")));
            Assert.True(double.IsNaN(NightSources.ToNumber(new object())));
            Assert.Equal(1, NightSources.ToFlag(2));
            Assert.Equal(0, NightSources.ToFlag(false));
            Assert.Equal(-1, NightSources.ToFlag(null));

            var values = new Dictionary<string, object> { [NightSources.TimeOfDayProperties[1]] = 64800.0 };
            Func<string, object> read = n => values.TryGetValue(n, out var v) ? v : null;
            Assert.Equal(64800.0, NightSources.FirstNumber(NightSources.TimeOfDayProperties, read));
            Assert.True(double.IsNaN(NightSources.FirstNumber(NightSources.HeadlightProperties, read)));
            Assert.True(double.IsNaN(NightSources.FirstNumber(NightSources.HeadlightProperties, n => throw new InvalidOperationException())));
        }

        // #46 fuel and range

        [Theory]
        [InlineData(63.24, 38.4, 100, 63.2)]
        [InlineData(double.NaN, 38.4, 100, 38.4)]
        [InlineData(0, 25, 50, 50.0)]
        [InlineData(0, 0, 100, 0.0)]
        [InlineData(150, 0, 0, 100.0)]
        [InlineData(42, 0, 0, 42.0)]
        public void FuelPercentComesFromSimHubOrTheTank(double pct, double fuel, double max, double expected)
        {
            Assert.Equal(expected, VehicleStatus.FuelPercent(pct, fuel, max));
        }

        [Fact]
        public void NoFuelDataMeansNoFuelPercent()
        {
            Assert.Null(VehicleStatus.FuelPercent(0, 0, 0));
            Assert.Null(VehicleStatus.FuelPercent(double.NaN, double.NaN, double.NaN));
        }

        [Theory]
        [InlineData(13.24, 7004, 92.7)]
        [InlineData(0, 7004, 0.0)]
        public void RangeIsRemainingLapsTimesTrackLength(double laps, double lengthM, double expected)
        {
            Assert.Equal(expected, VehicleStatus.RangeKm(laps, lengthM));
        }

        [Theory]
        [InlineData(double.NaN, 7004)]
        [InlineData(13, double.NaN)]
        [InlineData(13, 0)]
        [InlineData(-1, 7004)]
        public void RangeNeedsBothValues(double laps, double lengthM)
        {
            Assert.Null(VehicleStatus.RangeKm(laps, lengthM));
        }

        [Fact]
        public void TheSamplerSendsNightFuelAndRangeWhenSwitchedOn()
        {
            var sampler = new TelemetrySampler();
            var f = TelemetryTests.Frame();
            f.TimeOfDaySec = 21 * 3600;
            f.FuelPercent = 38.4;
            f.Fuel = 38.4;
            f.MaxFuel = 100;
            f.FuelRemainingLaps = 13.24;
            f.TrackLengthM = 7004;
            sampler.Update(ref f, 1.0);
            var settings = new TelemetrySettings();
            var m = sampler.Build(settings, 1.0);
            Assert.True(m.Night);
            Assert.Equal(38.4, m.FuelPercent);
            Assert.Equal(92.7, m.RangeKm);

            settings.NightMode = NightModes.Day;
            Assert.False(sampler.Build(settings, 1.0).Night);
            settings.SendNight = settings.SendFuel = settings.SendRange = false;
            m = sampler.Build(settings, 1.0);
            Assert.Null(m.Night);
            Assert.Null(m.FuelPercent);
            Assert.Null(m.RangeKm);
        }

        [Fact]
        public void NightSettingsAreRepaired()
        {
            var s = new TelemetrySettings { NightMode = "dusk", NightProperty = null }.Normalize();
            Assert.Equal(NightModes.Auto, s.NightMode);
            Assert.Equal("", s.NightProperty);
            Assert.Equal(" x ".Trim(), new TelemetrySettings { NightProperty = " x " }.Normalize().NightProperty);
        }

        // #47 status.nav on the wire

        private const string StatusHead = "{\"type\":\"status\",\"phoneConnected\":true,\"screen\":\"carplay\",\"nowPlaying\":null";

        [Fact]
        public void StatusNavRoundTrips()
        {
            var line = StatusHead + ",\"nav\":{\"maneuver\":\"slightRightTurn\",\"distanceM\":350,\"road\":\"B258\",\"etaEpochS\":1791044100}}";
            var status = (StatusMessage)MessageCodec.Decode(line);
            Assert.Equal("slightRightTurn", status.Nav.Maneuver);
            Assert.Equal(350, status.Nav.DistanceM);
            Assert.Equal("B258", status.Nav.Road);
            Assert.Equal(1791044100L, status.Nav.EtaEpochS);
            // Encoded exactly as the tablet does: distanceM as an integer.
            Assert.Equal(line, MessageCodec.Encode(status));

            // Only the maneuver: the optional members stay out.
            var bare = (StatusMessage)MessageCodec.Decode(StatusHead + ",\"nav\":{\"maneuver\":\"noTurn\"}}");
            Assert.Equal(StatusHead + ",\"nav\":{\"maneuver\":\"noTurn\"}}", MessageCodec.Encode(bare));
            // A decimal distance is rounded; a blank road is no road; an unknown maneuver passes through.
            var lenient = (StatusMessage)MessageCodec.Decode(StatusHead + ",\"nav\":{\"maneuver\":\"teleport\",\"distanceM\":12.6,\"road\":\"  \"}}");
            Assert.Equal("teleport", lenient.Nav.Maneuver);
            Assert.Equal(13, lenient.Nav.DistanceM);
            Assert.Null(lenient.Nav.Road);

            // No nav, nav null: no route guidance.
            Assert.Null(((StatusMessage)MessageCodec.Decode(StatusHead + "}")).Nav);
            Assert.Null(((StatusMessage)MessageCodec.Decode(StatusHead + ",\"nav\":null}")).Nav);
            Assert.DoesNotContain("nav", MessageCodec.Encode(new StatusMessage { PhoneConnected = true, Screen = Screens.CarPlay }));
            // An object without a maneuver (the tablet never sends one): still guidance, maneuver unknown.
            var empty = (StatusMessage)MessageCodec.Decode(StatusHead + ",\"nav\":{}}");
            Assert.NotNull(empty.Nav);
            Assert.Null(empty.Nav.Maneuver);
        }

        [Fact]
        public void TheManeuverTableHasAppleNamesInTypeOrder()
        {
            Assert.Equal(54, NavManeuvers.Known.Length);
            Assert.Equal("noTurn", NavManeuvers.Known[0]);
            Assert.Equal("arriveEndOfDirections", NavManeuvers.Known[27]);
            Assert.Equal("roundaboutExit1", NavManeuvers.Known[28]);
            Assert.Equal("roundaboutExit19", NavManeuvers.Known[46]);
            Assert.Equal("changeHighwayRight", NavManeuvers.Known[53]);
        }

        [Theory]
        [InlineData("\"nav\":\"left\"")]
        [InlineData("\"nav\":[1]")]
        [InlineData("\"nav\":{\"maneuver\":5,\"distanceM\":-3,\"road\":false,\"etaEpochS\":1.5}")]
        [InlineData("\"nav\":{\"maneuver\":\"\",\"distanceM\":\"far\",\"etaEpochS\":-1}")]
        public void ABadNavIsLenientAndTheStatusStillCounts(string nav)
        {
            var result = MessageCodec.TryDecode(StatusHead + "," + nav + "}");
            Assert.True(result.Ok, result.Reason);
            var status = (StatusMessage)result.Message;
            Assert.True(status.PhoneConnected);
            if (status.Nav != null)
            {
                Assert.Null(status.Nav.Maneuver);
                Assert.Null(status.Nav.DistanceM);
                Assert.Null(status.Nav.Road);
                Assert.Null(status.Nav.EtaEpochS);
            }
        }

        [Fact]
        public void TheEtaIsShownAsLocalTime()
        {
            var utc = TimeZoneInfo.Utc;
            Assert.Equal("12:30", SurfaceSnapshot.FormatEta(new DateTimeOffset(2026, 10, 3, 12, 30, 59, TimeSpan.Zero).ToUnixTimeSeconds(), utc));
            Assert.Equal("", SurfaceSnapshot.FormatEta(null, utc));
            Assert.Equal("", SurfaceSnapshot.FormatEta(-5, utc));
        }

        // #47 artwork on the wire

        [Fact]
        public void ArtworkDecodesAndEncodes()
        {
            var bytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 };
            var message = ArtworkMessage.Of(ArtworkFormats.Jpeg, bytes);
            var line = MessageCodec.Encode(message);
            Assert.Equal("{\"type\":\"artwork\",\"mime\":\"image/jpeg\",\"base64\":\"/9j/4AECAw==\"}", line);
            var decoded = Assert.IsType<ArtworkMessage>(MessageCodec.Decode(line));
            Assert.Equal(bytes, decoded.Bytes);
            Assert.Equal(ArtworkFormats.Png, ((ArtworkMessage)MessageCodec.Decode("{\"type\":\"artwork\",\"mime\":\"image/png\",\"base64\":\"iVBO\"}")).Mime);
        }

        [Theory]
        [InlineData("{\"type\":\"artwork\",\"base64\":\"AAAA\"}")]
        [InlineData("{\"type\":\"artwork\",\"mime\":\"image/jpeg\"}")]
        [InlineData("{\"type\":\"artwork\",\"mime\":null,\"base64\":null}")]
        [InlineData("{\"type\":\"artwork\",\"mime\":null,\"base64\":\"AAAA\"}")]
        [InlineData("{\"type\":\"artwork\",\"mime\":\"image/jpeg\",\"base64\":\"\"}")]
        [InlineData("{\"type\":\"artwork\",\"mime\":\"image/webp\",\"base64\":\"AAAA\"}")]
        [InlineData("{\"type\":\"artwork\",\"mime\":\"image/png\",\"base64\":\"A\"}")]
        [InlineData("{\"type\":\"artwork\",\"mime\":7,\"base64\":\"AAAA\"}")]
        public void BadArtworkIsABadMessage(string line)
        {
            var result = MessageCodec.TryDecode(line);
            Assert.Equal(DecodeFailure.Invalid, result.Failure);
            Assert.Equal("artwork", result.Type);
        }

        [Fact]
        public void TheLargestArtworkThatFitsInALineDecodes()
        {
            // 65536 bytes per line: about 49 000 bytes of image once base64 and the members are counted.
            var overhead = MessageCodec.Encode(ArtworkMessage.Of(ArtworkFormats.Jpeg, new byte[0])).Length;
            var bytes = (ProtocolDefaults.MaxLineBytes - overhead) / 4 * 3;
            var line = MessageCodec.Encode(ArtworkMessage.Of(ArtworkFormats.Jpeg, new byte[bytes]));
            Assert.True(line.Length <= ProtocolDefaults.MaxLineBytes);
            Assert.True(MessageCodec.TryDecode(line).Ok);
        }

        // #47 the artwork file

        [Fact]
        public void TheArtworkFileIsReplacedSwitchedAndCleared()
        {
            var file = new ArtworkFile(temp);
            Assert.Equal("", file.Show(null));
            var jpeg = ArtworkMessage.Of(ArtworkFormats.Jpeg, new byte[] { 1, 2, 3 });
            var path = file.Show(jpeg);
            Assert.Equal(Path.Combine(temp, "artwork.jpg"), path);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
            Assert.Same(path, file.Show(jpeg));
            Assert.Equal(1, file.Writes);

            var next = ArtworkMessage.Of(ArtworkFormats.Jpeg, new byte[] { 4, 5 });
            Assert.Equal(path, file.Show(next));
            Assert.Equal(new byte[] { 4, 5 }, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".tmp"));

            var png = ArtworkMessage.Of(ArtworkFormats.Png, new byte[] { 9 });
            var pngPath = file.Show(png);
            Assert.Equal(Path.Combine(temp, "artwork.png"), pngPath);
            Assert.False(File.Exists(path));
            Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(pngPath));

            Assert.Equal("", file.Show(null));
            Assert.False(File.Exists(pngPath));
            Assert.Empty(Directory.GetFiles(temp));
        }

        // End to end: the properties follow the primary tablet

        [Fact]
        public void NavAndArtworkPropertiesDescribeThePrimaryTablet()
        {
            var host = TelemetryTests.NewHost(new RigPlaySettings().Normalize());
            host.Artwork = new ArtworkFile(temp);
            try
            {
                using (var a = TelemetryTests.Pair(host, "tablet-a", new List<string>(), true))
                {
                    Assert.True(FakeTablet.WaitFor(() => host.PrimarySession?.TabletId == "tablet-a"));
                    Assert.False(host.Surface.NavActive);
                    Assert.Equal("", host.Surface.NavManeuver);
                    Assert.Equal(0, host.Surface.NavDistance);
                    Assert.Equal("", host.Surface.ArtworkPath);

                    var eta = DateTimeOffset.UtcNow.AddMinutes(25).ToUnixTimeSeconds();
                    a.SendLine(StatusHead + ",\"nav\":{\"maneuver\":\"leftTurn\",\"distanceM\":120,\"road\":\"A3\",\"etaEpochS\":" + eta + "}}");
                    Assert.True(FakeTablet.WaitFor(() => host.Surface.NavActive));
                    Assert.Equal("leftTurn", host.Surface.NavManeuver);
                    Assert.Equal(120.0, host.Surface.NavDistance);
                    Assert.Equal("A3", host.Surface.NavRoad);
                    Assert.Equal(SurfaceSnapshot.FormatEta(eta), host.Surface.NavEta);
                    Assert.Matches("^[0-2][0-9]:[0-5][0-9]$", host.Surface.NavEta);

                    a.Send(ArtworkMessage.Of(ArtworkFormats.Jpeg, new byte[] { 0xFF, 0xD8, 0xFF, 7 }));
                    Assert.True(FakeTablet.WaitFor(() => host.Surface.ArtworkPath != ""));
                    Assert.Equal(Path.Combine(temp, "artwork.jpg"), host.Surface.ArtworkPath);
                    Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 7 }, File.ReadAllBytes(host.Surface.ArtworkPath));

                    // A second tablet's phone connects: it is primary; it has no nav and no artwork.
                    using (var b = TelemetryTests.Pair(host, "tablet-b", new List<string>(), true))
                    {
                        Assert.True(FakeTablet.WaitFor(() => host.PrimarySession?.TabletId == "tablet-b"));
                        Assert.True(FakeTablet.WaitFor(() => host.Surface.ArtworkPath == ""));
                        Assert.False(host.Surface.NavActive);
                        b.Send(ArtworkMessage.Of(ArtworkFormats.Png, new byte[] { 0x89, 0x50 }));
                        Assert.True(FakeTablet.WaitFor(() => host.Surface.ArtworkPath.EndsWith("artwork.png")));
                    }
                    // b leaves: a's artwork and nav come back.
                    Assert.True(FakeTablet.WaitFor(() => host.Surface.ArtworkPath.EndsWith("artwork.jpg")));
                    Assert.True(host.Surface.NavActive);

                    // Guidance ends: nav goes, the artwork stays (there is no "clear").
                    a.SendLine(StatusHead + "}");
                    Assert.True(FakeTablet.WaitFor(() => !host.Surface.NavActive));
                    Assert.EndsWith("artwork.jpg", host.Surface.ArtworkPath);
                }
                // No tablet: no artwork.
                Assert.True(FakeTablet.WaitFor(() => host.Surface.ArtworkPath == ""));
                Assert.Empty(Directory.GetFiles(temp));
            }
            finally
            {
                host.Stop();
            }
        }

        [Fact]
        public void AnArtworkBeforePairingIsRefused()
        {
            var host = TelemetryTests.NewHost(new RigPlaySettings().Normalize());
            host.Artwork = new ArtworkFile(temp);
            try
            {
                using (var t = new FakeTablet(host.Server.Port))
                {
                    var hello = FakeTablet.NewHello("x");
                    t.Send(hello);
                    Assert.Equal(new[] { Features.Telemetry, Features.IdleDashboard }, t.Expect<WelcomeMessage>().Features.ToArray());
                    t.Send(ArtworkMessage.Of(ArtworkFormats.Png, new byte[] { 1 }));
                    t.ExpectError(ErrorCodes.NotPaired);
                }
            }
            finally
            {
                host.Stop();
            }
        }
    }
}

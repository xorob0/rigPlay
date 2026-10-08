// SPDX-License-Identifier: GPL-3.0-only
// DeadReckoningTests.cs: fake GPS strategy B (#43): the great-circle step (including the antimeridian and the poles),
// the integrator at 60 Hz, and every way back to the origin.
using RigPlayPlugin.Telemetry;
using Xunit;

namespace RigPlayPlugin.Tests
{
    public class DeadReckoningTests
    {
        private const double Lat0 = 50.3356;
        private const double Lon0 = 6.9475;

        // GeoMath

        [Fact]
        public void OneKilometreNorthOrEastAtTheEquator()
        {
            double lat, lon;
            GeoMath.Destination(0, 0, 0, 1000, out lat, out lon);
            Assert.Equal(1000 / GeoMath.EarthRadiusM * 180 / System.Math.PI, lat, 9);
            Assert.Equal(0, lon, 9);
            GeoMath.Destination(0, 0, 90, 1000, out lat, out lon);
            Assert.Equal(0, lat, 9);
            Assert.Equal(0.0089932, lon, 6);
            Assert.Equal(1000, GeoMath.DistanceM(0, 0, lat, lon), 3);
        }

        [Fact]
        public void TheStepMatchesTheDistanceEverywhere()
        {
            foreach (var bearing in new[] { 0.0, 45, 90, 135, 180, 225, 270, 315 })
            {
                double lat, lon;
                GeoMath.Destination(Lat0, Lon0, bearing, 1234.5, out lat, out lon);
                Assert.Equal(1234.5, GeoMath.DistanceM(Lat0, Lon0, lat, lon), 3);
            }
        }

        [Fact]
        public void CrossingTheAntimeridianWrapsTheLongitude()
        {
            double lat, lon;
            GeoMath.Destination(0, 179.9995, 90, 200, out lat, out lon);
            Assert.InRange(lon, -180, -179.99);
            Assert.Equal(200, GeoMath.DistanceM(0, 179.9995, lat, lon), 3);
            GeoMath.Destination(10, -179.9999, 270, 100, out lat, out lon);
            Assert.InRange(lon, 179.99, 180);
            Assert.True(lon < 180);
        }

        [Fact]
        public void CrossingAPoleComesDownTheOtherSide()
        {
            double lat, lon;
            // 100 m north from 50 m short of the north pole: 50 m beyond it, on the opposite meridian.
            var start = 90 - 50 / GeoMath.EarthRadiusM * 180 / System.Math.PI;
            GeoMath.Destination(start, 10, 0, 100, out lat, out lon);
            Assert.True(lat < 90 && lat > 89.999);
            Assert.Equal(-170, lon, 6);
            Assert.Equal(start, lat, 9);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(180, -180)]
        [InlineData(-180, -180)]
        [InlineData(190, -170)]
        [InlineData(-190, 170)]
        [InlineData(540, -180)]
        [InlineData(359.5, -0.5)]
        public void LongitudesWrapIntoRange(double input, double expected)
        {
            Assert.Equal(expected, GeoMath.NormalizeLon(input), 9);
        }

        // The integrator

        private static DeadReckoningStrategy New(double driftKm = 20, double stillSec = 30)
        {
            return new DeadReckoningStrategy(Lat0, Lon0, 617, driftKm * 1000, stillSec);
        }

        private static TelemetryInput Moving(double kmh)
        {
            var f = TelemetryInput.Empty;
            f.GameRunning = true;
            f.SpeedKmh = kmh;
            f.TrackName = "Spa";
            f.SessionType = "Race";
            return f;
        }

        private static void Drive(IGpsStrategy s, ref TelemetryInput f, double heading, double seconds)
        {
            var frames = (int)System.Math.Round(seconds * 60);
            for (var i = 0; i < frames; i++) s.Update(ref f, heading, 1 / 60.0);
        }

        private static double FromOrigin(IGpsStrategy s)
        {
            GpsFix fix;
            Assert.True(s.TryGetFix(out fix));
            return GeoMath.DistanceM(Lat0, Lon0, fix.Lat, fix.Lon);
        }

        [Fact]
        public void TheCarDrivesSpeedTimesTimeAlongTheHeading()
        {
            var s = New();
            GpsFix fix;
            s.TryGetFix(out fix);
            Assert.Equal(Lat0, fix.Lat);
            Assert.Equal(Lon0, fix.Lon);
            Assert.Equal(617.0, fix.Alt);

            var f = Moving(36); // 10 m/s
            Drive(s, ref f, 90, 10);
            s.TryGetFix(out fix);
            Assert.Equal(100, FromOrigin(s), 1);
            Assert.Equal(Lat0, fix.Lat, 5);
            Assert.True(fix.Lon > Lon0);

            // A U-turn brings it back.
            Drive(s, ref f, 270, 10);
            Assert.Equal(0, FromOrigin(s), 1);

            // Unknown heading drives north; a zero step does not move it.
            Drive(s, ref f, double.NaN, 5);
            s.TryGetFix(out fix);
            Assert.True(fix.Lat > Lat0);
            var before = fix.Lat;
            s.Update(ref f, 0, 0);
            s.TryGetFix(out fix);
            Assert.Equal(before, fix.Lat);
        }

        [Fact]
        public void LeavingThePitLaneResets()
        {
            var s = New();
            var f = Moving(60);
            f.InPitLane = true;
            Drive(s, ref f, 0, 5);
            Assert.True(FromOrigin(s) > 50);
            f.InPitLane = false;
            s.Update(ref f, 0, 1 / 60.0);
            Assert.True(FromOrigin(s) < 1);
            Assert.Equal("pit exit", s.LastResetReason);
        }

        [Fact]
        public void StandingStillResetsAfterTheConfiguredTimeOnly()
        {
            var s = New(stillSec: 10);
            var f = Moving(100);
            Drive(s, ref f, 45, 3);
            var driven = FromOrigin(s);
            f.SpeedKmh = 0.5; // below 0.5 m/s
            Drive(s, ref f, 45, 9);
            Assert.Equal(driven, FromOrigin(s), 6);
            Drive(s, ref f, 45, 1.1);
            Assert.True(FromOrigin(s) < 1e-6);
            Assert.StartsWith("stationary", s.LastResetReason);
            var resets = s.Resets;
            Drive(s, ref f, 45, 20);
            Assert.Equal(resets, s.Resets); // already at the origin: no repeated resets

            var never = New(stillSec: 0);
            var g = Moving(100);
            Drive(never, ref g, 0, 2);
            g.SpeedKmh = 0;
            Drive(never, ref g, 0, 120);
            Assert.True(FromOrigin(never) > 50);
        }

        [Fact]
        public void DriftingBeyondTheRadiusResets()
        {
            var s = New(driftKm: 1);
            var f = Moving(360); // 100 m/s
            Drive(s, ref f, 0, 9.9);
            Assert.InRange(FromOrigin(s), 980, 1000);
            Drive(s, ref f, 0, 0.5);
            Assert.True(FromOrigin(s) < 100);
            Assert.StartsWith("further than", s.LastResetReason);
        }

        [Fact]
        public void ASessionRestartOrANewTrackOrSessionResets()
        {
            var s = New();
            var f = Moving(100);
            Drive(s, ref f, 0, 2);
            f.SessionRestart = true;
            s.Update(ref f, 0, 1 / 60.0);
            Assert.Equal("session restart", s.LastResetReason);
            // The flag staying up is one restart, not one per frame.
            Drive(s, ref f, 0, 2);
            Assert.True(FromOrigin(s) > 50);
            f.SessionRestart = false;

            f.SessionType = "Qualify";
            s.Update(ref f, 0, 1 / 60.0);
            Assert.Equal("new session", s.LastResetReason);
            Drive(s, ref f, 0, 2);
            // An unknown name (null) is not a change.
            f.TrackName = null;
            Drive(s, ref f, 0, 1);
            Assert.True(FromOrigin(s) > 50);
            f.TrackName = "Monza";
            s.Update(ref f, 0, 1 / 60.0);
            Assert.True(FromOrigin(s) < 1);
        }

        [Fact]
        public void TheSamplerDrivesTheStrategyWithTheSimHeadingAndResetsOnGameStart()
        {
            var settings = new TelemetrySettings { GpsStrategy = GpsStrategies.DeadReckoning, OriginLat = Lat0, OriginLon = Lon0 };
            var sampler = new TelemetrySampler();
            var f = TelemetryTests.Frame(kmh: 72, yaw: 90); // 20 m/s east
            var t = 0.0;
            sampler.Update(ref f, t);
            Assert.Equal(Lat0, sampler.Build(settings, t).Lat); // the strategy exists from the first message
            for (var i = 0; i < 300; i++)
            {
                t += 1 / 60.0;
                sampler.Update(ref f, t);
            }
            var m = sampler.Build(settings, t);
            Assert.Equal(90.0, m.Heading);
            Assert.Equal(100, GeoMath.DistanceM(Lat0, Lon0, m.Lat.Value, m.Lon.Value), 0);
            Assert.True(m.Lon > Lon0);

            // A stall longer than 0.25 s is not integrated: the car does not jump.
            t += 2;
            sampler.Update(ref f, t);
            var after = sampler.Build(settings, t);
            Assert.Equal(m.Lon.Value, after.Lon.Value, 6);

            // The game stops and starts again: back at the origin.
            var stopped = TelemetryTests.Frame(running: false);
            sampler.Update(ref stopped, t + 0.1);
            sampler.Update(ref f, t + 0.2);
            var restarted = sampler.Build(settings, t + 0.2);
            // Back at the origin, plus the one 0.1 s step of the first frame (2 m).
            Assert.InRange(GeoMath.DistanceM(Lat0, Lon0, restarted.Lat.Value, restarted.Lon.Value), 1.5, 2.5);
        }

        [Fact]
        public void DeadReckoningSettingsAreRepaired()
        {
            var s = new TelemetrySettings { GpsStrategy = GpsStrategies.DeadReckoning, DriftRadiusKm = 0, StationaryResetSec = -1 }.Normalize();
            Assert.Equal(GpsStrategies.DeadReckoning, s.GpsStrategy);
            Assert.Equal(TelemetrySettings.DefaultDriftRadiusKm, s.DriftRadiusKm);
            Assert.Equal(TelemetrySettings.DefaultStationaryResetSec, s.StationaryResetSec);
            var kept = new TelemetrySettings { DriftRadiusKm = 5, StationaryResetSec = 0 }.Normalize();
            Assert.Equal(5.0, kept.DriftRadiusKm);
            Assert.Equal(0, kept.StationaryResetSec);
        }
    }
}

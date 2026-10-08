// SPDX-License-Identifier: GPL-3.0-only
// TrackGeoReferenceTests.cs: fake GPS strategy C (#44, docs/protocol.md §6.9.3, docs/TRACK_CALIBRATION.md): centreline
// interpolation, the affine world mapping, track keys, the shipped table, the lap recorder, the settings, and the
// strategy end to end through the sampler.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RigPlayPlugin.Telemetry;
using Xunit;

namespace RigPlayPlugin.Tests
{
    public class TrackGeoReferenceTests
    {
        private const double MetrePerDegLat = GeoMath.EarthRadiusM * Math.PI / 180.0;

        private static List<CentrelineSample> Ladder(params double[] pcts)
        {
            // Latitude = 50 + pct / 100, longitude fixed: easy to check by hand.
            return pcts.Select(p => new CentrelineSample(p, 50 + p / 100, 6)).ToList();
        }

        // Centreline interpolation

        [Fact]
        public void TheCentrelineInterpolatesBetweenSamples()
        {
            var map = new CentrelineMap(Ladder(0.1, 0.2, 0.4, 0.9));
            double lat, lon;
            Assert.True(map.TryLocate(0.1, out lat, out lon));
            Assert.Equal(50.001, lat, 12);
            Assert.Equal(6.0, lon, 12);
            Assert.True(map.TryLocate(0.3, out lat, out lon));
            Assert.Equal(50.003, lat, 12); // halfway between 0.2 and 0.4
            Assert.True(map.TryLocate(0.65, out lat, out lon));
            Assert.Equal(50.0065, lat, 12);
            Assert.False(map.TryLocate(double.NaN, out lat, out lon));
        }

        [Fact]
        public void TheCentrelineWrapsAcrossTheStartFinishLine()
        {
            // Last sample 0.9 (lat 50.009), first 0.1 (lat 50.001): the segment across the line spans 0.2 of the lap.
            var map = new CentrelineMap(Ladder(0.1, 0.5, 0.9));
            double lat, lon;
            map.TryLocate(0.95, out lat, out lon); // a quarter into the segment
            Assert.Equal(50.009 + (50.001 - 50.009) * 0.25, lat, 12);
            map.TryLocate(0.0, out lat, out lon); // halfway
            Assert.Equal(50.005, lat, 12);
            map.TryLocate(1.0, out lat, out lon); // 1.0 is the line again
            Assert.Equal(50.005, lat, 12);
            map.TryLocate(0.05, out lat, out lon); // three quarters
            Assert.Equal(50.009 + (50.001 - 50.009) * 0.75, lat, 12);
            map.TryLocate(1.3, out lat, out lon); // more than a lap wraps
            Assert.Equal(50.003, lat, 12);
            map.TryLocate(-0.1, out lat, out lon); // = 0.9
            Assert.Equal(50.009, lat, 12);
        }

        [Fact]
        public void TheCentrelineGivesTheDirectionOfTravel()
        {
            // Due east along the equator, then the bearing along it is 90°.
            var east = Enumerable.Range(0, 20).Select(i => new CentrelineSample(i / 20.0, 0, i * 0.001)).ToList();
            var map = new CentrelineMap(east);
            Assert.Equal(90.0, map.BearingAt(0.5), 3);
            Assert.Throws<ArgumentException>(() => new CentrelineMap(Ladder(0.5, 0.2, 0.3)));
        }

        // Affine world mapping

        [Theory]
        // x, z, refX, refZ, swap, flipX, flipZ, rotation, scale -> east, north
        [InlineData(100, 50, 0, 0, false, false, false, 0, 1, 100, 50)]
        [InlineData(0, 1, 0, 0, false, false, false, 90, 1, 1, 0)]            // north turns clockwise to east
        [InlineData(100, 50, 0, 0, false, false, false, 90, 1, 50, -100)]
        [InlineData(100, 50, 0, 0, false, false, false, 180, 1, -100, -50)]
        [InlineData(100, 50, 0, 0, false, false, false, 0, 2, 200, 100)]
        [InlineData(100, 50, 0, 0, true, false, false, 0, 1, 50, 100)]         // swap
        [InlineData(100, 50, 0, 0, false, true, false, 0, 1, -100, 50)]        // flip x
        [InlineData(100, 50, 0, 0, false, false, true, 0, 1, 100, -50)]        // flip z (Assetto Corsa)
        [InlineData(100, 50, 0, 0, true, true, true, 0, 1, -50, -100)]
        [InlineData(110, 70, 10, 20, false, false, false, 0, 1, 100, 50)]      // reference point
        [InlineData(110, 70, 10, 20, true, false, true, 90, 0.5, -50, -25)]    // all at once: (100,50)->(50,100)->(50,-100)->rot 90->(-100,-50)·0.5
        public void TheAffineMappingTurnsWorldCoordinatesIntoEastAndNorth(double x, double z, double refX, double refZ, bool swap, bool flipX, bool flipZ,
            double rotation, double scale, double east, double north)
        {
            double e, n;
            AffineMap.ToLocal(x, z, refX, refZ, swap, flipX, flipZ, rotation, scale, out e, out n);
            Assert.Equal(east, e, 9);
            Assert.Equal(north, n, 9);
        }

        [Fact]
        public void TheAffineMappingPlacesMetresAroundTheOrigin()
        {
            double lat, lon;
            AffineMap.Offset(50, 6, 0, 1000, out lat, out lon);
            Assert.Equal(50 + 1000 / MetrePerDegLat, lat, 9);
            Assert.Equal(6.0, lon, 12);
            AffineMap.Offset(50, 6, 1000, 0, out lat, out lon);
            Assert.Equal(1000, GeoMath.DistanceM(50, 6, lat, lon), 0);
            Assert.True(lon > 6);

            // The whole mapping: Assetto Corsa's default axes put world +z to the south.
            var cal = new TrackCalibration { OriginLat = 50, OriginLon = 6, RefX = 10, RefZ = 10 };
            AffineMap.Map(cal, "AssettoCorsa", 10, 1010, out lat, out lon);
            Assert.Equal(50 - 1000 / MetrePerDegLat, lat, 9);
            AffineMap.Map(cal, "RFactor2", 10, 1010, out lat, out lon);
            Assert.Equal(50 + 1000 / MetrePerDegLat, lat, 9);
            // Explicit axes override the game's.
            cal.AxesAuto = false;
            AffineMap.Map(cal, "AssettoCorsa", 10, 1010, out lat, out lon);
            Assert.Equal(50 + 1000 / MetrePerDegLat, lat, 9);
        }

        // Track keys

        [Theory]
        [InlineData("spa gp", "spa gp")]
        [InlineData("  Circuit de Spa-Francorchamps ", "circuit de spa francorchamps")]
        [InlineData("Nürburgring - GP", "nurburgring gp")]
        [InlineData("ks_nurburgring-layout_gp_a", "ks nurburgring layout gp a")]
        [InlineData("MONZA", "monza")]
        [InlineData("Le Mans 24h (2018)", "le mans 24h 2018")]
        [InlineData("", "")]
        [InlineData(null, "")]
        [InlineData("---", "")]
        public void TrackKeysAreNormalised(string name, string key)
        {
            Assert.Equal(key, TrackKeys.Normalize(name));
        }

        [Fact]
        public void TheTrackCodeWinsOverTheName()
        {
            Assert.Equal("spa gp", TrackKeys.FromNames("spa gp", "Circuit de Spa-Francorchamps"));
            Assert.Equal("circuit de spa francorchamps", TrackKeys.FromNames(" ", "Circuit de Spa-Francorchamps"));
            Assert.Equal("", TrackKeys.FromNames(null, null));
            Assert.True(TrackKeys.ContainsWords("circuit de spa francorchamps", "spa"));
            Assert.False(TrackKeys.ContainsWords("spanish gp", "spa"));
            Assert.True(TrackKeys.ContainsWords("le mans 24h", "le mans"));
        }

        // Shipped table and user override

        [Fact]
        public void TheShippedTableIsEmbeddedApproximateAndComplete()
        {
            var table = ShippedTracks.Default;
            Assert.True(table.Approximate);
            Assert.Equal(8, table.Tracks.Count);
            foreach (var t in table.Tracks)
            {
                Assert.InRange(t.Lat, -90, 90);
                Assert.InRange(t.Lon, -180, 180);
                Assert.True(t.UncertaintyM > 0, t.Name + " says how approximate it is");
                // No invented precision: four decimals at most (about 10 m).
                Assert.Equal(Math.Round(t.Lat, 4), t.Lat);
                Assert.Equal(Math.Round(t.Lon, 4), t.Lon);
            }
        }

        [Theory]
        [InlineData("spa gp", "Spa")]
        [InlineData("circuit de spa francorchamps", "Spa")]
        [InlineData("spa 2022", "Spa")]
        [InlineData("ks nurburgring layout gp a", "Nürburgring")]
        [InlineData("nurburgring gp", "Nürburgring")]
        [InlineData("silverstone international", "Silverstone")]
        [InlineData("monza full", "Monza")]
        [InlineData("lemans full", "Le Mans")]
        [InlineData("le mans 24h", "Le Mans")]
        [InlineData("suzuka grandprix", "Suzuka")]
        [InlineData("laguna seca", "Laguna Seca")]
        [InlineData("watkins glen boot", "Watkins Glen")]
        [InlineData("nurburgring nordschleife", null)]
        [InlineData("nurburgring combined", null)]
        [InlineData("spanish rally stage", null)]
        [InlineData("brands hatch", null)]
        [InlineData("", null)]
        public void TheShippedTableMatchesTrackKeys(string key, string expected)
        {
            var found = ShippedTracks.Default.Find(key);
            if (expected == null) Assert.Null(found);
            else Assert.Contains(expected, found?.Name ?? "(none)");
        }

        [Fact]
        public void TheUsersCalibrationOverridesTheShippedTable()
        {
            var table = ShippedTracks.Parse("{\"approximate\":true,\"tracks\":[{\"name\":\"Test Ring\",\"lat\":10.5,\"lon\":20.25,\"uncertaintyM\":50,\"aliases\":[\"Test Ring\"]},"
                + "{\"name\":\"broken\",\"lat\":100,\"lon\":0,\"aliases\":[\"broken\"]},{\"name\":\"no alias\",\"lat\":1,\"lon\":1}]}");
            Assert.Single(table.Tracks);
            var shipped = TrackCalibrations.Resolve(null, table, "test ring gp");
            Assert.Equal(CalibrationSources.Shipped, shipped.Source);
            Assert.Equal(10.5, shipped.OriginLat);
            Assert.Equal("test ring gp", shipped.TrackKey);

            var mine = new TrackCalibration { TrackKey = "test ring gp", OriginLat = 1, OriginLon = 2 }.Normalize();
            var resolved = TrackCalibrations.Resolve(new[] { mine }, table, "test ring gp");
            Assert.Same(mine, resolved);
            Assert.Equal(CalibrationSources.User, resolved.Source);
            Assert.Null(TrackCalibrations.Resolve(new[] { mine }, table, "elsewhere"));
            Assert.Null(TrackCalibrations.Resolve(new[] { mine }, table, ""));
        }

        // The lap recorder

        /// <summary>Feeds a lap of a 1 km-radius circle (lat/lon), <paramref name="steps"/> frames per lap, from <paramref name="from"/>.</summary>
        private static void Drive(LapRecorder r, double from, double to, int steps, double driftPerLapM = 0)
        {
            for (var i = 0; ; i++)
            {
                var progress = from + i / (double)steps;
                if (progress > to) break;
                var p = progress - Math.Floor(progress);
                var a = 2 * Math.PI * p;
                var drift = driftPerLapM * Math.Max(0, progress - Math.Ceiling(from)) / MetrePerDegLat; // from the line on
                r.Feed(p, 50 + (Math.Cos(a) - 1) * 1000 / MetrePerDegLat + drift, 6 + Math.Sin(a) * 1000 / (MetrePerDegLat * Math.Cos(50 * Math.PI / 180)));
                if (r.State == LapRecorderState.Done || r.State == LapRecorderState.Failed) break;
            }
        }

        [Fact]
        public void TheRecorderWaitsForTheLineThenSamplesEveryHalfPercent()
        {
            var r = new LapRecorder();
            r.Start();
            Drive(r, 0.4, 0.99, 2000);
            Assert.Equal(LapRecorderState.WaitingForLine, r.State);
            Assert.Equal(0, r.SampleCount);
            Drive(r, 0.99, 2.5, 2000);
            Assert.Equal(LapRecorderState.Done, r.State);
            var s = r.Result;
            Assert.InRange(s.Count, 170, 201);
            Assert.True(s[0].Pct < 0.001);
            for (var i = 1; i < s.Count; i++) Assert.True(s[i].Pct - s[i - 1].Pct >= LapRecorder.Spacing - 1e-9);
            Assert.True(s[s.Count - 1].Pct > 0.99);
            // Usable as a centreline.
            Assert.Equal(s.Count, TrackCalibration.CleanCentreline(s).Count);
            Assert.Contains("lap closed", r.Message);
        }

        [Fact]
        public void TheRecorderClosesTheLoopBySpreadingTheDrift()
        {
            var r = new LapRecorder();
            r.Start();
            Drive(r, 0.9, 2.5, 2000, driftPerLapM: 80); // dead reckoning that ends 80 m north of where it started
            Assert.Equal(LapRecorderState.Done, r.State);
            var s = r.Result;
            var first = s[0];
            var last = s[s.Count - 1];
            // Without the correction the last sample would be ~80 m off the circle; with it it sits next to the first.
            Assert.True(GeoMath.DistanceM(first.Lat, first.Lon, last.Lat, last.Lon) < 40);
            var mid = s[s.Count / 2];
            var expected = 50 + (Math.Cos(2 * Math.PI * mid.Pct) - 1) * 1000 / MetrePerDegLat;
            Assert.Equal(expected, mid.Lat, 5);
            Assert.Contains("drift", r.Message);
        }

        [Fact]
        public void StoppingKeepsALongEnoughLapAndRefusesAShortOne()
        {
            var r = new LapRecorder();
            r.Start();
            r.Stop();
            Assert.Equal(LapRecorderState.Failed, r.State);

            r.Start();
            Drive(r, 0.95, 1.5, 1000);
            r.Stop();
            Assert.Equal(LapRecorderState.Failed, r.State);
            Assert.Null(r.Result);
            Assert.Contains("%", r.Message);

            r.Start();
            Drive(r, 0.95, 1.95, 1000);
            Assert.Equal(LapRecorderState.Recording, r.State);
            r.Stop();
            Assert.Equal(LapRecorderState.Done, r.State);
            Assert.True(r.Result.Count > 170);

            // A lap cut short at the line (a shortcut, a reset) is not kept.
            r.Start();
            Drive(r, 0.95, 1.4, 1000);
            Drive(r, 1.9, 2.2, 1000);
            Assert.Equal(LapRecorderState.Failed, r.State);
        }

        // Settings

        [Fact]
        public void TrackCalibrationsRoundTripAndNormalize()
        {
            var lap = Enumerable.Range(0, 50).Select(i => new CentrelineSample(i / 50.0, 50.44 + i * 1e-5, 5.96)).ToList();
            var settings = new RigPlaySettings();
            settings.Telemetry.GpsStrategy = GpsStrategies.Track;
            settings.Telemetry.Tracks.Add(new TrackCalibration
            {
                TrackKey = "Spa GP", Name = "Spa", OriginLat = 50.44, OriginLon = 5.96, RefX = 12.5, RefZ = -3, RotationDeg = -90, Scale = 1.5,
                AxesAuto = false, SwapAxes = true, FlipZ = true, Centreline = lap,
            });
            settings.Normalize();
            var json = JsonConvert.SerializeObject(settings);
            Assert.DoesNotContain("HasCentreline", json);
            var copy = JsonConvert.DeserializeObject<RigPlaySettings>(json).Normalize();
            var t = Assert.Single(copy.Telemetry.Tracks);
            Assert.Equal("spa gp", t.TrackKey);
            Assert.Equal(CalibrationSources.Centreline, t.Source);
            Assert.Equal(270.0, t.RotationDeg);
            Assert.Equal(1.5, t.Scale);
            Assert.Equal(12.5, t.RefX);
            Assert.True(t.SwapAxes && t.FlipZ && !t.FlipX && !t.AxesAuto);
            Assert.Equal(50, t.Centreline.Count);
            Assert.Same(t, copy.Telemetry.FindTrack("spa gp"));
            Assert.Equal(GpsStrategies.Track, copy.Telemetry.GpsStrategy);
        }

        [Fact]
        public void NormalizeRepairsTrackCalibrations()
        {
            var t = new TelemetrySettings
            {
                Tracks = new List<TrackCalibration>
                {
                    null,
                    new TrackCalibration { TrackKey = "  " },
                    new TrackCalibration { TrackKey = "Monza", OriginLat = 1 },
                    new TrackCalibration
                    {
                        TrackKey = "monza", OriginLat = 95, Scale = 0, RotationDeg = double.NaN, RefX = double.PositiveInfinity, Source = CalibrationSources.Shipped,
                        Centreline = Ladder(0.1, 0.2, 0.3), // too short to use
                    },
                    new TrackCalibration
                    {
                        TrackKey = "suzuka",
                        Centreline = Ladder(0.9, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.1, 1.0, double.NaN)
                            .Concat(new[] { new CentrelineSample(0.95, 91, 0), new CentrelineSample(0.05, 50, 6), new CentrelineSample(0.15, 50, 6), null }).ToList(),
                    },
                },
            }.Normalize();
            Assert.Equal(new[] { "monza", "suzuka" }, t.Tracks.Select(c => c.TrackKey).ToArray());
            var monza = t.Tracks[0]; // the last entry for the key wins
            Assert.Equal(TelemetrySettings.DefaultOriginLat, monza.OriginLat);
            Assert.Equal(1.0, monza.Scale);
            Assert.Equal(0.0, monza.RotationDeg);
            Assert.Equal(0.0, monza.RefX);
            Assert.Empty(monza.Centreline);
            Assert.Equal(CalibrationSources.User, monza.Source);
            var suzuka = t.Tracks[1];
            // 0.1 twice, 1.0, NaN and lat 91 dropped: 0.05, 0.1 .. 0.9 and 0.15 remain, sorted.
            Assert.Equal(CalibrationSources.Centreline, suzuka.Source);
            Assert.Equal(new[] { 0.05, 0.1, 0.15, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9 }, suzuka.Centreline.Select(s => s.Pct).ToArray());
        }

        [Fact]
        public void ASchema3FileMovesToTheCurrentSchemaWithNoTracks()
        {
            var old = JsonConvert.DeserializeObject<RigPlaySettings>("{\"SchemaVersion\":3,\"Telemetry\":{\"GpsStrategy\":\"deadReckoning\"}}").Normalize();
            Assert.Equal(RigPlaySettings.CurrentSchemaVersion, old.SchemaVersion);
            Assert.Empty(old.Telemetry.Tracks);
            Assert.Equal(GpsStrategies.DeadReckoning, old.Telemetry.GpsStrategy);
            var nulls = JsonConvert.DeserializeObject<RigPlaySettings>("{\"SchemaVersion\":4,\"Telemetry\":{\"Tracks\":null}}").Normalize();
            Assert.Empty(nulls.Telemetry.Tracks);
        }

        // The strategy through the sampler

        private static TelemetryInput Frame(string code, double kmh, double yaw, double pct, double x = double.NaN, double z = double.NaN, string game = "IRacing")
        {
            var f = TelemetryTests.Frame(kmh: kmh, yaw: yaw);
            f.TrackCode = code;
            f.TrackName = code;
            f.GameName = game;
            f.TrackPct = pct;
            f.X = x;
            f.Z = z;
            return f;
        }

        [Fact]
        public void WithoutCoordinatesTheCarDrivesFromTheShippedOrigin()
        {
            // iRacing at Spa ("spa gp"): no world coordinates, no calibration -> dead reckoning from the shipped start line.
            var settings = new TelemetrySettings { GpsStrategy = GpsStrategies.Track };
            var spa = ShippedTracks.Default.Find("spa gp");
            var sampler = new TelemetrySampler();
            sampler.EnsureStrategy(settings);
            var t = 0.0;
            for (var i = 0; i < 600; i++)
            {
                var f = Frame("spa gp", 180, 90, 0.2 + i * 1e-4);
                sampler.Update(ref f, t);
                t += 1 / 60.0;
            }
            var m = sampler.Build(settings, t);
            var d = GeoMath.DistanceM(spa.Lat, spa.Lon, m.Lat.Value, m.Lon.Value);
            Assert.InRange(d, 400, 600); // 50 m/s for ~10 s, east
            Assert.Equal(90.0, m.Heading.Value, 1);
            var status = sampler.WithStrategy(s => ((TrackGeoReferenceStrategy)s).Status());
            Assert.Equal("spa gp", status.TrackKey);
            Assert.Equal(CalibrationSources.Shipped, status.Source);
            Assert.Equal(TrackModes.DeadReckoning, status.Mode);
            Assert.True(status.HasLapFraction);
            Assert.False(status.HasWorldCoordinates);

            // A track nobody knows: the page's origin.
            var unknown = new TelemetrySampler();
            var f2 = Frame("brands hatch", 0, 0, 0.5);
            unknown.Update(ref f2, 0);
            var m2 = unknown.Build(settings, 0);
            Assert.Equal(TelemetrySettings.DefaultOriginLat, m2.Lat);
        }

        [Fact]
        public void ARecordedCentrelineIsFollowedByLapFraction()
        {
            var lap = Enumerable.Range(0, 100).Select(i => new CentrelineSample(i / 100.0, 45.6 + i * 1e-4, 9.28)).ToList(); // due north
            var settings = new TelemetrySettings { GpsStrategy = GpsStrategies.Track };
            settings.Tracks.Add(new TrackCalibration { TrackKey = "monza", OriginLat = 45.6, OriginLon = 9.28, Centreline = lap }.Normalize());
            var sampler = new TelemetrySampler();
            sampler.EnsureStrategy(settings);
            var f = Frame("Monza", 200, 33, 0.255);
            sampler.Update(ref f, 0);
            var m = sampler.Build(settings, 0);
            Assert.Equal(45.6 + 25.5e-4, m.Lat.Value, 6);
            Assert.Equal(9.28, m.Lon.Value, 6);
            Assert.Equal(0.0, m.Heading.Value, 1); // along the centreline, not the sim's 33°
            Assert.Equal(TrackModes.Centreline, sampler.WithStrategy(s => ((TrackGeoReferenceStrategy)s).Mode));
        }

        [Fact]
        public void WorldCoordinatesAreMappedWithTheUsersCalibration()
        {
            var settings = new TelemetrySettings { GpsStrategy = GpsStrategies.Track };
            settings.Tracks.Add(new TrackCalibration { TrackKey = "ks nurburgring layout gp a", OriginLat = 50.3356, OriginLon = 6.9477, RefX = -100, RefZ = 200, RotationDeg = 90 }.Normalize());
            var sampler = new TelemetrySampler();
            sampler.EnsureStrategy(settings);
            // Assetto Corsa: z grows southwards; 100 m of world +z is 100 m south, turned 90° clockwise -> 100 m west.
            var t = 0.0;
            TelemetryInput f;
            for (var i = 0; i <= 10; i++)
            {
                f = Frame("ks_nurburgring-layout_gp_a", 36, 0, double.NaN, -100, 200 + i * 10, "AssettoCorsa");
                sampler.Update(ref f, t);
                t += 0.1;
            }
            var m = sampler.Build(settings, t - 0.1);
            double lat, lon;
            AffineMap.Offset(50.3356, 6.9477, -100, 0, out lat, out lon);
            Assert.Equal(lat, m.Lat.Value, 6);
            Assert.Equal(lon, m.Lon.Value, 6);
            Assert.Equal(270.0, m.Heading.Value, 0); // driving west on the map
            Assert.Equal(TrackModes.Affine, sampler.WithStrategy(s => ((TrackGeoReferenceStrategy)s).Mode));

            // A changed calibration (page edit) is picked up by the next message: the strategy is rebuilt.
            var changes = sampler.StrategyChanges;
            settings.Tracks[0].RotationDeg = 0;
            settings.MarkTracksChanged();
            sampler.Build(settings, t);
            Assert.Equal(changes + 1, sampler.StrategyChanges);
        }

        [Fact]
        public void FrozenWorldCoordinatesDoNotPinTheCar()
        {
            // SimHub's iRacing reader publishes a near-constant CarCoordinates (x ≈ 0.3, z = 0, seen on the VM): it must
            // not count as world coordinates, or the car would sit at the origin; dead reckoning drives instead.
            var settings = new TelemetrySettings { GpsStrategy = GpsStrategies.Track };
            var sampler = new TelemetrySampler();
            sampler.EnsureStrategy(settings);
            var t = 0.0;
            for (var i = 0; i < 300; i++)
            {
                var f = Frame("spa gp", 180, 0, 0.3 + i * 1e-4, 0.3 + (i % 2) * 0.1, 0);
                sampler.Update(ref f, t);
                t += 1 / 60.0;
            }
            var status = sampler.WithStrategy(s => ((TrackGeoReferenceStrategy)s).Status());
            Assert.False(status.HasWorldCoordinates);
            Assert.Equal(TrackModes.DeadReckoning, status.Mode);
            var spa = ShippedTracks.Default.Find("spa gp");
            Assert.InRange(GeoMath.DistanceM(spa.Lat, spa.Lon, status.Lat, status.Lon), 200, 300); // 50 m/s for ~5 s
        }

        [Fact]
        public void RecordingALapThroughTheStrategyYieldsACentrelineForTheTrack()
        {
            // iRacing-like: no coordinates; the car drives a circle (yaw turning with the lap) at 50 m/s, 60 s a lap.
            var settings = new TelemetrySettings { GpsStrategy = GpsStrategies.Track, StationaryResetSec = 0 };
            var sampler = new TelemetrySampler();
            sampler.EnsureStrategy(settings);
            var t = 0.0;
            var lapSec = 60.0;
            Func<double, TelemetryInput> at = time =>
            {
                var p = (time / lapSec + 0.8) % 1.0;
                return Frame("spa gp", 180, 360 * p + 1e-3, p);
            };
            for (var i = 0; i < 60; i++)
            {
                var f = at(t);
                sampler.Update(ref f, t);
                t += 1 / 60.0;
            }
            string why = null;
            Assert.True(sampler.WithStrategy(s => ((TrackGeoReferenceStrategy)s).StartRecording(out why)));
            RecordedLap lap = null;
            for (var i = 0; i < 60 * 90 && lap == null; i++)
            {
                var f = at(t);
                sampler.Update(ref f, t);
                t += 1 / 60.0;
                lap = sampler.WithStrategy(s => ((TrackGeoReferenceStrategy)s).TakeRecordedLap());
            }
            Assert.NotNull(lap);
            Assert.Equal("spa gp", lap.TrackKey);
            Assert.InRange(lap.Samples.Count, 170, 201);
            var spa = ShippedTracks.Default.Find("spa gp");
            // The lap starts on the line, which dead reckoning put back on the origin.
            Assert.True(GeoMath.DistanceM(spa.Lat, spa.Lon, lap.Samples[0].Lat, lap.Samples[0].Lon) < 5);
            // A 3 km lap: a circle of radius ~477 m, so no sample is more than ~955 m from the start.
            Assert.True(lap.Samples.All(s => GeoMath.DistanceM(spa.Lat, spa.Lon, s.Lat, s.Lon) < 1000));
            Assert.Equal(CalibrationSources.Shipped, lap.RecordedWith.Source);
            Assert.Null(sampler.WithStrategy(s => ((TrackGeoReferenceStrategy)s).TakeRecordedLap())); // once
        }
    }
}

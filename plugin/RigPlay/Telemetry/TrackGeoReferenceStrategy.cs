// SPDX-License-Identifier: GPL-3.0-only
// TrackGeoReferenceStrategy.cs: fake GPS strategy C (#44, docs/protocol.md §6.9.3): the car appears on the real circuit.
// Per track (TrackKeys: the game's TrackCode, else the track name) the user's calibration wins over the shipped table's
// approximate origin (TrackCalibrations.Resolve). Then, in this order:
//   1. centreline: the track has a recorded lap and the game publishes the lap fraction -> interpolate it;
//   2. affine: the game publishes world coordinates -> origin + rotation · scale · (x, z) (AffineMap);
//   3. otherwise dead reckoning (strategy B) from the track's origin, the heading turned by the calibration's rotation
//      and the speed times its scale, so a car without coordinates (iRacing) at least drives at the circuit.
// The "Record a lap" helper (LapRecorder) samples what 2 or 3 produce and becomes the track's centreline.
// Heading: along the centreline; for the affine mapping the direction between successive positions (1 m apart), the
// sim's heading turned by the rotation until the car has moved; for dead reckoning the turned sim heading.
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests). Update allocates only when the track changes.
using System;
using System.Collections.Generic;
using System.Linq;

namespace RigPlayPlugin.Telemetry
{
    /// <summary>How strategy C places the car right now.</summary>
    public static class TrackModes
    {
        public const string NoTrack = "waiting for a track";
        public const string Centreline = "recorded centreline";
        public const string Affine = "world coordinates";
        public const string DeadReckoning = "dead reckoning from the origin";
    }

    /// <summary>What the page shows about strategy C (a snapshot).</summary>
    public sealed class TrackStatus
    {
        public string TrackKey = "";
        public string TrackName = "";
        public string GameName = "";
        public string Source = CalibrationSources.None;
        public string Mode = TrackModes.NoTrack;
        public string ShippedName;
        public double ShippedUncertaintyM = double.NaN;
        public bool HasLapFraction;
        public bool HasWorldCoordinates;
        public double LapFraction = double.NaN;
        public double X = double.NaN;
        public double Z = double.NaN;
        public bool HasFix;
        public double Lat = double.NaN;
        public double Lon = double.NaN;

        /// <summary>The calibration in effect (a copy); never null once a track is known.</summary>
        public TrackCalibration Calibration;

        public string AxesInEffect = "";
        public LapRecorderState Recorder = LapRecorderState.Idle;
        public string RecorderMessage = "";
        public int RecorderSamples;
        public double RecorderCoverage;
    }

    /// <summary>A lap the recorder finished, for the page to save into the track's calibration.</summary>
    public sealed class RecordedLap
    {
        public string TrackKey;
        public string TrackName;
        public List<CentrelineSample> Samples;

        /// <summary>The calibration the lap was recorded with (origin, rotation...), a copy.</summary>
        public TrackCalibration RecordedWith;
        public string Message;
    }

    /// <summary>Strategy C (#44). See the file header.</summary>
    public sealed class TrackGeoReferenceStrategy : IGpsStrategy, IGpsHeadingSource
    {
        /// <summary>World coordinates are used once they have moved this far (metres, game units) in the session.</summary>
        public const double MinWorldMoveM = 5.0;

        private readonly List<TrackCalibration> user;
        private readonly ShippedTracks shipped;
        private readonly double fallbackLat;
        private readonly double fallbackLon;
        private readonly double alt;
        private readonly double driftRadiusM;
        private readonly double stationaryResetSec;
        private readonly LapRecorder recorder = new LapRecorder();

        private string lastCode;
        private string lastName;
        private string key = "";
        private string trackName = "";
        private TrackCalibration cal;
        private ShippedTrack shippedMatch;
        private CentrelineMap centreline;
        private DeadReckoningStrategy dr;
        private string mode = TrackModes.NoTrack;
        private string gameName;

        private bool pctSeen;
        private bool xzSeen;
        private bool xzFirst;
        private double firstX;
        private double firstZ;
        private double pct = double.NaN;
        private double x = double.NaN;
        private double z = double.NaN;

        private bool hasFix;
        private double lat;
        private double lon;
        private double heading = double.NaN;
        private bool anchored;
        private double anchorLat;
        private double anchorLon;
        private RecordedLap pendingLap;

        /// <param name="user">The user's calibrations; copied, so later edits need a new strategy (GpsStrategies.Key).</param>
        /// <param name="shipped">The shipped table (ShippedTracks.Default).</param>
        /// <param name="fallbackLat">The page's origin, for tracks nobody calibrated.</param>
        public TrackGeoReferenceStrategy(IEnumerable<TrackCalibration> user, ShippedTracks shipped, double fallbackLat, double fallbackLon,
            double alt, double driftRadiusM, double stationaryResetSec)
        {
            this.user = (user ?? Enumerable.Empty<TrackCalibration>()).Where(c => c != null).Select(c => c.Clone()).ToList();
            this.shipped = shipped ?? ShippedTracks.Empty;
            this.fallbackLat = fallbackLat;
            this.fallbackLon = fallbackLon;
            this.alt = alt;
            this.driftRadiusM = driftRadiusM;
            this.stationaryResetSec = stationaryResetSec;
            cal = Fallback("");
            dr = NewDeadReckoning();
        }

        /// <summary>The heading that agrees with the position; NaN to let the sampler use the sim's.</summary>
        public double HeadingDeg => heading;

        /// <summary>The current track key ("" before the first named frame).</summary>
        public string TrackKey => key;

        public string Mode => mode;

        public void Update(ref TelemetryInput input, double headingDeg, double dtSec)
        {
            gameName = input.GameName;
            if (!string.Equals(lastCode, input.TrackCode, StringComparison.Ordinal) || !string.Equals(lastName, input.TrackName, StringComparison.Ordinal))
            {
                lastCode = input.TrackCode;
                lastName = input.TrackName;
                var now = TrackKeys.FromNames(input.TrackCode, input.TrackName);
                if (now.Length > 0 && now != key) SelectTrack(now, input.TrackName);
            }

            pct = Finite(input.TrackPct) ? CentrelineMap.Wrap(input.TrackPct) : double.NaN;
            if (!double.IsNaN(pct) && pct > 1e-6) pctSeen = true;
            x = input.X;
            z = input.Z;
            var xzKnown = Finite(x) && Finite(z);
            if (xzKnown && !xzSeen && (Math.Abs(x) > 1e-6 || Math.Abs(z) > 1e-6))
            {
                // Coordinates count once they have moved MinWorldMoveM: a game that publishes zeros or a frozen point
                // (SimHub's iRacing reader) stays on dead reckoning instead of a car stuck at the origin.
                if (!xzFirst)
                {
                    xzFirst = true;
                    firstX = x;
                    firstZ = z;
                }
                else if ((x - firstX) * (x - firstX) + (z - firstZ) * (z - firstZ) >= MinWorldMoveM * MinWorldMoveM)
                {
                    xzSeen = true;
                }
            }
            var lapKnown = pctSeen && !double.IsNaN(pct);

            if (lapKnown && centreline != null && !recorder.Active)
            {
                mode = TrackModes.Centreline;
                double a, b;
                if (centreline.TryLocate(pct, out a, out b))
                {
                    SetFix(a, b);
                    heading = centreline.BearingAt(pct);
                }
            }
            else if (xzSeen && xzKnown)
            {
                mode = TrackModes.Affine;
                double a, b;
                AffineMap.Map(cal, gameName, x, z, out a, out b);
                SetFix(a, b);
                if (!anchored)
                {
                    anchored = true;
                    anchorLat = a;
                    anchorLon = b;
                }
                else if (GeoMath.DistanceM(anchorLat, anchorLon, a, b) >= HeadingTracker.MinMoveM)
                {
                    heading = AffineMap.Bearing(anchorLat, anchorLon, a, b);
                    anchorLat = a;
                    anchorLon = b;
                }
                if (double.IsNaN(heading) && !double.IsNaN(headingDeg)) heading = HeadingTracker.Normalize(headingDeg + cal.RotationDeg);
            }
            else
            {
                mode = TrackModes.DeadReckoning;
                if (lapKnown && recorder.BeginsAt(pct)) dr.Reset(); // the lap starts on the line: back on the origin
                var turned = double.IsNaN(headingDeg) ? double.NaN : HeadingTracker.Normalize(headingDeg + cal.RotationDeg);
                var scaled = input;
                if (Finite(scaled.SpeedKmh)) scaled.SpeedKmh *= cal.Scale;
                dr.Update(ref scaled, turned, dtSec);
                GpsFix f;
                dr.TryGetFix(out f);
                SetFix(f.Lat, f.Lon);
                heading = turned;
            }

            if (recorder.Active && lapKnown && hasFix)
            {
                recorder.Feed(pct, lat, lon);
                if (recorder.State == LapRecorderState.Done) TakeResult();
            }
        }

        public bool TryGetFix(out GpsFix fix)
        {
            if (hasFix) fix = new GpsFix { Lat = lat, Lon = lon, Alt = alt };
            else fix = new GpsFix { Lat = cal.OriginLat, Lon = cal.OriginLon, Alt = alt };
            return true;
        }

        public void Reset()
        {
            dr.Reset();
            hasFix = false;
            anchored = false;
            heading = double.NaN;
            pctSeen = false;
            xzSeen = false;
            xzFirst = false;
            if (recorder.Active) StopRecording();
        }

        // Recording (the page calls these through TelemetrySampler.WithStrategy).

        /// <summary>Starts "Record a lap"; false (and why) when it cannot start.</summary>
        public bool StartRecording(out string why)
        {
            if (key.Length == 0)
            {
                why = "No track yet: start a session first.";
                return false;
            }
            pendingLap = null;
            recorder.Start();
            why = "Recording starts when the car crosses the start/finish line.";
            return true;
        }

        /// <summary>Stops the recorder; a lap long enough becomes pending (<see cref="TakeRecordedLap"/>).</summary>
        public void StopRecording()
        {
            recorder.Stop();
            if (recorder.State == LapRecorderState.Done) TakeResult();
        }

        /// <summary>The lap recorded since the last call, once; null when none.</summary>
        public RecordedLap TakeRecordedLap()
        {
            var lap = pendingLap;
            pendingLap = null;
            return lap;
        }

        /// <summary>A snapshot for the page.</summary>
        public TrackStatus Status()
        {
            bool swap, flipX, flipZ;
            cal.Axes(gameName, out swap, out flipX, out flipZ);
            return new TrackStatus
            {
                TrackKey = key,
                TrackName = trackName,
                GameName = gameName ?? "",
                Source = key.Length == 0 ? CalibrationSources.None : cal.Source,
                Mode = mode,
                ShippedName = shippedMatch?.Name,
                ShippedUncertaintyM = shippedMatch?.UncertaintyM ?? double.NaN,
                HasLapFraction = pctSeen,
                HasWorldCoordinates = xzSeen,
                LapFraction = pct,
                X = x,
                Z = z,
                HasFix = hasFix,
                Lat = hasFix ? lat : cal.OriginLat,
                Lon = hasFix ? lon : cal.OriginLon,
                Calibration = cal.Clone(),
                AxesInEffect = GameAxes.Describe(swap, flipX, flipZ),
                Recorder = recorder.State,
                RecorderMessage = recorder.Message,
                RecorderSamples = recorder.SampleCount,
                RecorderCoverage = recorder.Coverage,
            };
        }

        private void TakeResult()
        {
            pendingLap = new RecordedLap
            {
                TrackKey = key,
                TrackName = trackName,
                Samples = recorder.Result,
                RecordedWith = cal.Clone(),
                Message = recorder.Message,
            };
        }

        private void SelectTrack(string trackKey, string name)
        {
            if (recorder.Active) recorder.Cancel();
            key = trackKey;
            trackName = string.IsNullOrWhiteSpace(name) ? trackKey : name.Trim();
            shippedMatch = shipped.Find(trackKey);
            var resolved = TrackCalibrations.Resolve(user, shipped, trackKey);
            cal = resolved ?? Fallback(trackKey);
            centreline = cal.HasCentreline ? new CentrelineMap(cal.Centreline) : null;
            dr = NewDeadReckoning();
            hasFix = false;
            anchored = false;
            heading = double.NaN;
            pctSeen = false;
            xzSeen = false;
            xzFirst = false;
            PluginLog.Info("Telemetry position: track '" + trackKey + "', calibration " + cal.Source
                + (shippedMatch != null && cal.Source == CalibrationSources.Shipped ? " (" + shippedMatch.Name + ", approximate)" : ""));
        }

        private TrackCalibration Fallback(string trackKey)
        {
            return new TrackCalibration { TrackKey = trackKey, Source = CalibrationSources.None, OriginLat = fallbackLat, OriginLon = fallbackLon };
        }

        private DeadReckoningStrategy NewDeadReckoning()
        {
            return new DeadReckoningStrategy(cal.OriginLat, cal.OriginLon, alt, driftRadiusM, stationaryResetSec);
        }

        private void SetFix(double a, double b)
        {
            if (!Finite(a) || !Finite(b)) return;
            lat = a;
            lon = b;
            hasFix = true;
        }

        private static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}

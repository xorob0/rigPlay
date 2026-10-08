// SPDX-License-Identifier: GPL-3.0-only
// TelemetrySampler.cs: from SimHub frames to the telemetry message (docs/protocol.md §6.9). Update() runs for every
// DataUpdate (60 Hz, SimHub's data thread): it keeps the latest frame and feeds the heading tracker and the GPS
// strategy, without allocating. Build() runs on the sender's 10 Hz timer: it applies the per-field switches of the
// "Data to CarPlay" section and maps the sim's values to the wire (m/s, P/R/N/D, compass heading, position, night,
// fuel level and range).
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests).
using System;
using System.Diagnostics;
using RigPlayPlugin.Protocol;

namespace RigPlayPlugin.Telemetry
{
    public sealed class TelemetrySampler
    {
        /// <summary>No DataUpdate for this long: the game counts as not running (SimHub stalled or the plugin stopped).</summary>
        public const double StaleAfterSec = 3.0;

        /// <summary>A longer gap between two frames is not integrated (the car does not jump after a stall).</summary>
        public const double MaxStepSec = 0.25;

        private static readonly double TickSeconds = 1.0 / Stopwatch.Frequency;

        private readonly object sync = new object();
        private readonly HeadingTracker heading = new HeadingTracker();
        private TelemetryInput last = TelemetryInput.Empty;
        private double lastAt = double.NaN;
        private bool wasRunning;
        private IGpsStrategy gps;
        private string gpsKey = GpsStrategies.Off; // no strategy yet: off

        /// <summary>Monotonic seconds, the time base of <see cref="Update(ref TelemetryInput)"/> and <see cref="Build(TelemetrySettings)"/>.</summary>
        public static double Now()
        {
            return Stopwatch.GetTimestamp() * TickSeconds;
        }

        /// <summary>How many times the GPS strategy was (re)built because its settings changed.</summary>
        public int StrategyChanges { get; private set; }

        /// <summary>Frames received since start, for the page.</summary>
        public long Frames { get; private set; }

        /// <summary>One SimHub frame, now.</summary>
        public void Update(ref TelemetryInput input)
        {
            Update(ref input, Now());
        }

        /// <summary>One SimHub frame at <paramref name="nowSec"/> (monotonic seconds).</summary>
        public void Update(ref TelemetryInput input, double nowSec)
        {
            lock (sync)
            {
                Frames++;
                var dt = double.IsNaN(lastAt) ? 0 : nowSec - lastAt;
                if (dt < 0 || dt > MaxStepSec) dt = 0;
                if (!input.GameRunning)
                {
                    // The next start is a new session: heading and position start over.
                    if (wasRunning) ResetMotionLocked();
                    wasRunning = false;
                }
                else
                {
                    if (!wasRunning) ResetMotionLocked();
                    wasRunning = true;
                    heading.Update(ref input);
                    gps?.Update(ref input, heading.Heading, dt);
                }
                last = input;
                lastAt = nowSec;
            }
        }

        /// <summary>Puts the car back at the start of its made-up journey (heading and GPS strategy).</summary>
        public void ResetMotion()
        {
            lock (sync) ResetMotionLocked();
        }

        private void ResetMotionLocked()
        {
            heading.Reset();
            gps?.Reset();
        }

        /// <summary>The telemetry message for the latest frame, now.</summary>
        public TelemetryMessage Build(TelemetrySettings settings)
        {
            return Build(settings, Now());
        }

        /// <summary>
        /// The telemetry message for the latest frame with the switches of <paramref name="settings"/> applied.
        /// gameRunning is always set; with no running game (or a frame older than <see cref="StaleAfterSec"/>) it is the
        /// only data member.
        /// </summary>
        public TelemetryMessage Build(TelemetrySettings settings, double nowSec)
        {
            settings = settings ?? new TelemetrySettings();
            TelemetryInput input;
            double headingDeg;
            GpsFix fix = default(GpsFix);
            bool hasFix;
            lock (sync)
            {
                EnsureStrategyLocked(settings);
                input = last;
                headingDeg = heading.Heading;
                hasFix = gps != null && gps.TryGetFix(out fix);
                // A strategy with its own heading (strategy C, along the circuit) keeps heading and position in agreement.
                var own = gps as IGpsHeadingSource;
                if (own != null && !double.IsNaN(own.HeadingDeg)) headingDeg = own.HeadingDeg;
                if (double.IsNaN(lastAt) || nowSec - lastAt > StaleAfterSec) input.GameRunning = false;
            }

            var m = new TelemetryMessage { GameRunning = input.GameRunning };
            if (!input.GameRunning) return m;

            if (settings.SendSpeed) m.SpeedMps = SpeedMps(input.SpeedKmh);
            if (settings.SendGear) m.Gear = MapGear(input.Gear, input.InPit || input.InPitLane);
            if (settings.SendHeading)
            {
                // A position needs a course for the phone's map; with none known it points north (strategy A of #42).
                if (!double.IsNaN(headingDeg)) m.Heading = Round(headingDeg, 1) % 360.0;
                else if (hasFix) m.Heading = 0.0;
            }
            if (hasFix)
            {
                m.Lat = Round(Clamp(fix.Lat, -90, 90), 7);
                m.Lon = Round(Clamp(fix.Lon, -180, 180), 7);
                if (!double.IsNaN(fix.Alt) && !double.IsInfinity(fix.Alt)) m.Alt = Round(fix.Alt, 1);
            }
            if (settings.SendNight) m.Night = VehicleStatus.Night(settings.NightMode, ref input);
            if (settings.SendFuel) m.FuelPercent = VehicleStatus.FuelPercent(input.FuelPercent, input.Fuel, input.MaxFuel);
            if (settings.SendRange) m.RangeKm = VehicleStatus.RangeKm(input.FuelRemainingLaps, input.TrackLengthM);
            if (settings.SendRpm && IsFinite(input.Rpm) && input.Rpm >= 0) m.Rpm = Math.Round(input.Rpm);
            if (settings.SendTrackName && !string.IsNullOrWhiteSpace(input.TrackName)) m.TrackName = input.TrackName.Trim();
            if (settings.SendSessionType && !string.IsNullOrWhiteSpace(input.SessionType)) m.SessionType = input.SessionType.Trim();
            return m;
        }

        /// <summary>
        /// Builds the GPS strategy for <paramref name="settings"/> now if it changed. The sender does this before every
        /// message; the page calls it so strategy C follows the track (and can record a lap) with no tablet connected.
        /// </summary>
        public void EnsureStrategy(TelemetrySettings settings)
        {
            if (settings == null) return;
            lock (sync) EnsureStrategyLocked(settings);
        }

        /// <summary>Runs <paramref name="read"/> on the current strategy (null when off) under the sampler's lock.</summary>
        public T WithStrategy<T>(Func<IGpsStrategy, T> read)
        {
            lock (sync) return read(gps);
        }

        private void EnsureStrategyLocked(TelemetrySettings settings)
        {
            var key = GpsStrategies.Key(settings);
            if (key == gpsKey) return;
            gps = GpsStrategies.Create(settings);
            gpsKey = key;
            StrategyChanges++;
            PluginLog.Info("Telemetry position: " + GpsStrategies.Label(settings.GpsStrategy));
        }

        /// <summary>km/h to m/s, ≥ 0, two decimals; null when unknown.</summary>
        public static double? SpeedMps(double kmh)
        {
            if (!IsFinite(kmh)) return null;
            return Round(Math.Max(0, kmh) / 3.6, 2);
        }

        /// <summary>
        /// The sim gear to the wire's P/R/N/D (spec §6.9): reverse → R; any forward gear → D; neutral in the pit lane or
        /// box → P; other neutral → N. Null when the gear is unknown.
        /// </summary>
        public static string MapGear(string gear, bool inPits)
        {
            if (gear == null) return null;
            var g = gear.Trim();
            if (g.Length == 0) return null;
            var c = char.ToUpperInvariant(g[0]);
            if (c == 'R' || g == "-1") return "R";
            if (c == 'N' || g == "0") return inPits ? "P" : "N";
            if (c == 'P') return "P";
            if (c == 'D') return "D";
            int number;
            if (int.TryParse(g, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out number))
            {
                if (number > 0) return "D";
                if (number < 0) return "R";
            }
            return null;
        }

        internal static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static double Round(double value, int decimals)
        {
            return Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        }

        private static double Clamp(double value, double min, double max)
        {
            return value < min ? min : value > max ? max : value;
        }
    }
}

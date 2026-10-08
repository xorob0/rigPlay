// SPDX-License-Identifier: GPL-3.0-only
// GpsStrategy.cs: how the telemetry message gets a position (docs/protocol.md §6.9 lat/lon/alt). Sims do not publish
// GPS coordinates, so a strategy makes one up from the settings and the car's motion; the strategy combo on the
// page lists GpsStrategies.All. HeadingTracker turns the sim's yaw (or, without yaw, the car's movement) into a
// compass heading the strategies and the heading field share.
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests).
using System;
using System.Globalization;

namespace RigPlayPlugin.Telemetry
{
    /// <summary>A WGS 84 position.</summary>
    public struct GpsFix
    {
        public double Lat;
        public double Lon;
        public double Alt;
    }

    /// <summary>
    /// Makes up a position. <see cref="Update"/> runs on SimHub's data thread for every frame (60 Hz) and must not
    /// allocate; <see cref="TryGetFix"/> runs at 10 Hz. TelemetrySampler serialises the calls.
    /// </summary>
    public interface IGpsStrategy
    {
        /// <param name="input">This frame.</param>
        /// <param name="headingDeg">Compass heading from HeadingTracker; NaN when unknown.</param>
        /// <param name="dtSec">Seconds since the previous frame (0 for the first frame after a gap).</param>
        void Update(ref TelemetryInput input, double headingDeg, double dtSec);

        /// <summary>The current position; false while there is none.</summary>
        bool TryGetFix(out GpsFix fix);

        /// <summary>Back to the start (the origin), as after a session start.</summary>
        void Reset();
    }

    /// <summary>
    /// A strategy whose position comes with its own heading (strategy C: along the circuit), which then replaces the
    /// sim's in the heading field so the two agree.
    /// </summary>
    public interface IGpsHeadingSource
    {
        /// <summary>Degrees clockwise from north, 0 ≤ h &lt; 360; NaN to keep the sim's heading.</summary>
        double HeadingDeg { get; }
    }

    /// <summary>The strategy names stored in TelemetrySettings.GpsStrategy and shown in the page's combo.</summary>
    public static class GpsStrategies
    {
        /// <summary>No position: lat/lon/alt are not sent.</summary>
        public const string Off = "off";

        /// <summary>Strategy A (#42): always the origin set on the page.</summary>
        public const string Fixed = "fixed";

        /// <summary>Strategy B (#43): dead reckoning from the origin with the sim's speed and heading.</summary>
        public const string DeadReckoning = "deadReckoning";

        /// <summary>Strategy C (#44): the car on the real circuit (track calibration, recorded centreline).</summary>
        public const string Track = "track";

        /// <summary>Every strategy, in the order the page lists them.</summary>
        public static readonly string[] All = { Off, Fixed, DeadReckoning, Track };

        public static bool IsKnown(string name)
        {
            return name != null && Array.IndexOf(All, name) >= 0;
        }

        /// <summary>What the page shows for a strategy.</summary>
        public static string Label(string name)
        {
            switch (name)
            {
                case Off: return "Off (no position)";
                case Fixed: return "Fixed position (the origin below)";
                case DeadReckoning: return "Drive around the origin (dead reckoning)";
                case Track: return "Real track (the car on the actual circuit)";
                default: return name;
            }
        }

        /// <summary>A new strategy for <paramref name="settings"/>; null for <see cref="Off"/> or an unknown name.</summary>
        public static IGpsStrategy Create(TelemetrySettings settings)
        {
            switch (settings?.GpsStrategy)
            {
                case Fixed: return new FixedOriginStrategy(settings.OriginLat, settings.OriginLon, settings.OriginAlt);
                case DeadReckoning:
                    return new DeadReckoningStrategy(settings.OriginLat, settings.OriginLon, settings.OriginAlt,
                        settings.DriftRadiusKm * 1000.0, settings.StationaryResetSec);
                case Track:
                    return new TrackGeoReferenceStrategy(settings.Tracks, ShippedTracks.Default, settings.OriginLat, settings.OriginLon,
                        settings.OriginAlt, settings.DriftRadiusKm * 1000.0, settings.StationaryResetSec);
                default: return null;
            }
        }

        /// <summary>
        /// Everything a strategy is built from, as one string: when it changes, the sampler builds a new strategy
        /// (so a changed origin moves the car back to it at once).
        /// </summary>
        public static string Key(TelemetrySettings settings)
        {
            if (settings == null || settings.GpsStrategy == Off || !IsKnown(settings.GpsStrategy)) return Off;
            return settings.GpsStrategy + "|" + settings.OriginLat.ToString("R", CultureInfo.InvariantCulture)
                + "|" + settings.OriginLon.ToString("R", CultureInfo.InvariantCulture)
                + "|" + settings.OriginAlt.ToString("R", CultureInfo.InvariantCulture)
                + "|" + settings.DriftRadiusKm.ToString("R", CultureInfo.InvariantCulture)
                + "|" + settings.StationaryResetSec.ToString(CultureInfo.InvariantCulture)
                + (settings.GpsStrategy == Track
                    ? "|" + settings.TracksRevision.ToString(CultureInfo.InvariantCulture) + "|" + (settings.Tracks?.Count ?? 0).ToString(CultureInfo.InvariantCulture)
                    : "");
        }
    }

    /// <summary>
    /// The car's compass heading. The sim's yaw (OrientationYaw, degrees) when the game publishes it; games that do not
    /// report 0 forever, so yaw counts as published once a frame shows a non-zero value. Without yaw, the direction of
    /// the last movement of at least <see cref="MinMoveM"/> in the world coordinates (X, Z; game-dependent axes).
    /// </summary>
    public sealed class HeadingTracker
    {
        public const double MinMoveM = 1.0;

        private bool yawSeen;
        private bool anchored;
        private double anchorX;
        private double anchorZ;

        /// <summary>Degrees clockwise from north, 0 ≤ h &lt; 360; NaN while unknown.</summary>
        public double Heading { get; private set; } = double.NaN;

        public void Reset()
        {
            yawSeen = false;
            anchored = false;
            Heading = double.NaN;
        }

        public void Update(ref TelemetryInput input)
        {
            var yaw = input.YawDeg;
            if (!double.IsNaN(yaw) && !double.IsInfinity(yaw) && Math.Abs(yaw) > 1e-6) yawSeen = true;
            if (yawSeen && !double.IsNaN(yaw) && !double.IsInfinity(yaw))
            {
                Heading = Normalize(yaw);
                return;
            }
            if (double.IsNaN(input.X) || double.IsNaN(input.Z) || double.IsInfinity(input.X) || double.IsInfinity(input.Z)) return;
            if (!anchored)
            {
                anchorX = input.X;
                anchorZ = input.Z;
                anchored = true;
                return;
            }
            var dx = input.X - anchorX;
            var dz = input.Z - anchorZ;
            if (dx * dx + dz * dz < MinMoveM * MinMoveM) return;
            Heading = Normalize(Math.Atan2(dx, dz) * 180.0 / Math.PI);
            anchorX = input.X;
            anchorZ = input.Z;
        }

        /// <summary>Any angle in degrees to 0 ≤ h &lt; 360.</summary>
        public static double Normalize(double degrees)
        {
            if (double.IsNaN(degrees) || double.IsInfinity(degrees)) return double.NaN;
            var h = degrees % 360.0;
            if (h < 0) h += 360.0;
            if (h >= 360.0) h -= 360.0;
            return h;
        }
    }
}

namespace RigPlayPlugin.Telemetry
{
    /// <summary>
    /// Strategy A (#42): the car sits at the origin entered on the page (home, say), while speed, gear and heading
    /// come from the sim, so Maps shows the car at home with the real speed. Heading is the sim's when known, else 0.
    /// </summary>
    public sealed class FixedOriginStrategy : IGpsStrategy
    {
        private readonly GpsFix origin;

        public FixedOriginStrategy(double lat, double lon, double alt)
        {
            origin = new GpsFix { Lat = lat, Lon = lon, Alt = alt };
        }

        public void Update(ref TelemetryInput input, double headingDeg, double dtSec) { }

        public bool TryGetFix(out GpsFix fix)
        {
            fix = origin;
            return true;
        }

        public void Reset() { }
    }

    /// <summary>Reads coordinates typed or pasted on the page.</summary>
    public static class GeoText
    {
        private static readonly char[] Separators = { ',', ' ', ';', '\t' };

        /// <summary>
        /// "50.3356, 6.9475" or "50.3356 6.9475" (as copied from a map) into a latitude and a longitude. Invariant
        /// culture: the decimal separator is a dot.
        /// </summary>
        public static bool TryParseLatLon(string text, out double lat, out double lon)
        {
            lat = lon = double.NaN;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) return false;
            return TryParse(parts[0], -90, 90, out lat) && TryParse(parts[1], -180, 180, out lon);
        }

        /// <summary>One number in [min, max].</summary>
        public static bool TryParse(string text, double min, double max, out double value)
        {
            value = double.NaN;
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
            return !double.IsNaN(value) && !double.IsInfinity(value) && value >= min && value <= max;
        }
    }
}

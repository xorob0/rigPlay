// SPDX-License-Identifier: GPL-3.0-only
// TrackCalibration.cs: how strategy C (#44, docs/protocol.md §6.9.3) puts a sim track onto the real circuit. One
// TrackCalibration per track, keyed by a normalised track key (TrackKeys): the circuit's origin (a known spot, normally
// the start/finish line), how the game's world axes turn onto east/north (rotation, scale, swap/flip), and optionally a
// recorded centreline (lap fraction -> lat/lon). The user's calibrations live in RigPlaySettings.Telemetry.Tracks; a
// small shipped table (ShippedTracks, Resources/tracks.json) gives approximate origins for a few circuits.
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RigPlayPlugin.Telemetry
{
    /// <summary>Where a track's calibration comes from, as the page shows it.</summary>
    public static class CalibrationSources
    {
        /// <summary>Nothing known about the track: the car drives around the page's origin.</summary>
        public const string None = "none";

        /// <summary>The shipped table's approximate origin.</summary>
        public const string Shipped = "shipped";

        /// <summary>The user's own origin, rotation and scale.</summary>
        public const string User = "user";

        /// <summary>The user's calibration with a recorded centreline.</summary>
        public const string Centreline = "centreline";
    }

    /// <summary>One point of a recorded lap: the lap fraction and where the car was.</summary>
    public class CentrelineSample
    {
        /// <summary>Lap fraction, 0 ≤ pct &lt; 1 (TrackPositionPercent).</summary>
        public double Pct { get; set; }

        public double Lat { get; set; }
        public double Lon { get; set; }

        public CentrelineSample() { }

        public CentrelineSample(double pct, double lat, double lon)
        {
            Pct = pct;
            Lat = lat;
            Lon = lon;
        }
    }

    /// <summary>
    /// One track's geo-reference. The affine mapping turns the game's world coordinates (x, z) into a position:
    /// (u, v) = (x − RefX, z − RefZ), swapped and flipped as the axes say, rotated clockwise by RotationDeg, times Scale,
    /// gives metres east and north of the origin. A centreline, when there is one, wins whenever the game publishes the
    /// lap fraction.
    /// </summary>
    public class TrackCalibration
    {
        public const double MinScale = 0.01;
        public const double MaxScale = 100;

        /// <summary>A centreline needs at least this many samples to be used.</summary>
        public const int MinCentrelineSamples = 10;

        /// <summary>Normalised track key (<see cref="TrackKeys.Normalize"/>).</summary>
        public string TrackKey { get; set; } = "";

        /// <summary>The track's name as SimHub showed it when the calibration was made (page only).</summary>
        public string Name { get; set; } = "";

        /// <summary><see cref="CalibrationSources"/>: recomputed by Normalize for the user's entries.</summary>
        public string Source { get; set; } = CalibrationSources.User;

        /// <summary>The origin, WGS 84 degrees: where the world point (RefX, RefZ) and a reset dead-reckoned car are.</summary>
        public double OriginLat { get; set; } = global::RigPlayPlugin.TelemetrySettings.DefaultOriginLat;

        public double OriginLon { get; set; } = global::RigPlayPlugin.TelemetrySettings.DefaultOriginLon;

        /// <summary>The game's world coordinates (metres) that map onto the origin.</summary>
        public double RefX { get; set; }

        public double RefZ { get; set; }

        /// <summary>Clockwise rotation, degrees, from the game's axes to the compass; also turns a dead-reckoned heading.</summary>
        public double RotationDeg { get; set; }

        /// <summary>Metres on the map per game unit (1 for games in metres).</summary>
        public double Scale { get; set; } = 1.0;

        /// <summary>True: use the game's default axes (<see cref="GameAxes"/>) and ignore the three flags below.</summary>
        public bool AxesAuto { get; set; } = true;

        /// <summary>Exchange x and z before flipping.</summary>
        public bool SwapAxes { get; set; }

        /// <summary>Negate the east component (after the swap).</summary>
        public bool FlipX { get; set; }

        /// <summary>Negate the north component (after the swap).</summary>
        public bool FlipZ { get; set; }

        /// <summary>The recorded lap, sorted by Pct; empty when none.</summary>
        public List<CentrelineSample> Centreline { get; set; } = new List<CentrelineSample>();

        /// <summary>True when the recorded centreline is usable.</summary>
        [Newtonsoft.Json.JsonIgnore]
        public bool HasCentreline => Centreline != null && Centreline.Count >= MinCentrelineSamples;

        /// <summary>A deep copy, so the data thread never reads an object the page is editing.</summary>
        public TrackCalibration Clone()
        {
            var copy = (TrackCalibration)MemberwiseClone();
            copy.Centreline = (Centreline ?? new List<CentrelineSample>()).Select(s => new CentrelineSample(s.Pct, s.Lat, s.Lon)).ToList();
            return copy;
        }

        /// <summary>The axes in effect for <paramref name="gameName"/>: the flags, or the game's defaults in auto.</summary>
        public void Axes(string gameName, out bool swap, out bool flipX, out bool flipZ)
        {
            if (AxesAuto)
            {
                GameAxes.Default(gameName, out swap, out flipX, out flipZ);
                return;
            }
            swap = SwapAxes;
            flipX = FlipX;
            flipZ = FlipZ;
        }

        /// <summary>
        /// Repairs a user's entry in place: a normalised key, a valid origin, a finite rotation in [0, 360), a scale in
        /// range, and a centreline of valid samples sorted by Pct (one per Pct), dropped when too short to use.
        /// </summary>
        public TrackCalibration Normalize()
        {
            TrackKey = TrackKeys.Normalize(TrackKey);
            Name = Name == null ? "" : Name.Trim();
            if (!Finite(OriginLat) || OriginLat < -90 || OriginLat > 90 || !Finite(OriginLon) || OriginLon < -180 || OriginLon > 180)
            {
                OriginLat = global::RigPlayPlugin.TelemetrySettings.DefaultOriginLat;
                OriginLon = global::RigPlayPlugin.TelemetrySettings.DefaultOriginLon;
            }
            if (!Finite(RefX)) RefX = 0;
            if (!Finite(RefZ)) RefZ = 0;
            RotationDeg = Finite(RotationDeg) ? HeadingTracker.Normalize(RotationDeg) : 0;
            if (!Finite(Scale) || Scale < MinScale || Scale > MaxScale) Scale = 1.0;
            Centreline = CleanCentreline(Centreline);
            Source = HasCentreline ? CalibrationSources.Centreline : CalibrationSources.User;
            return this;
        }

        /// <summary>Valid samples only, sorted by Pct, one per Pct; empty when fewer than <see cref="MinCentrelineSamples"/>.</summary>
        public static List<CentrelineSample> CleanCentreline(IEnumerable<CentrelineSample> samples)
        {
            var clean = (samples ?? Enumerable.Empty<CentrelineSample>())
                .Where(s => s != null && Finite(s.Pct) && s.Pct >= 0 && s.Pct < 1
                    && Finite(s.Lat) && s.Lat >= -90 && s.Lat <= 90 && Finite(s.Lon) && s.Lon >= -180 && s.Lon <= 180)
                .GroupBy(s => s.Pct)
                .Select(g => g.First())
                .OrderBy(s => s.Pct)
                .ToList();
            return clean.Count >= MinCentrelineSamples ? clean : new List<CentrelineSample>();
        }

        internal static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    /// <summary>
    /// Track keys: the game's track id (TrackCode) or else its name, lower-case, without accents, every run of
    /// characters other than a-z and 0-9 turned into one space. "Circuit de Spa-Francorchamps" -> "circuit de spa
    /// francorchamps", "ks_nurburgring-layout_gp_a" -> "ks nurburgring layout gp a".
    /// </summary>
    public static class TrackKeys
    {
        public static string Normalize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var decomposed = name.Trim().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            var space = false;
            foreach (var ch in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                var c = char.ToLowerInvariant(ch);
                if (c == 'ß') c = 's';
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                {
                    if (space && sb.Length > 0) sb.Append(' ');
                    space = false;
                    sb.Append(c);
                }
                else
                {
                    space = true;
                }
            }
            return sb.ToString();
        }

        /// <summary>The key of a frame: TrackCode when the game publishes one, else the track name; "" when neither.</summary>
        public static string FromNames(string trackCode, string trackName)
        {
            var key = Normalize(trackCode);
            return key.Length > 0 ? key : Normalize(trackName);
        }

        /// <summary>
        /// True when every word of <paramref name="alias"/> appears in <paramref name="key"/>, in order and next to each
        /// other ("spa" matches "spa gp" and "circuit de spa francorchamps", not "spanish"). Both are normalised keys.
        /// </summary>
        public static bool ContainsWords(string key, string alias)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(alias)) return false;
            var k = " " + key + " ";
            return k.IndexOf(" " + alias + " ", StringComparison.Ordinal) >= 0;
        }
    }

    /// <summary>
    /// Default world axes per game for the affine mapping (#44). Only the handedness matters (a wrong one mirrors the
    /// track); the direction of north is the calibration's rotation. Assetto Corsa: x east, z south (the convention of
    /// its track map files: pixel y grows with world z). Every other game: x east, z north until somebody verifies it;
    /// the page's axes setting overrides this.
    /// </summary>
    public static class GameAxes
    {
        public static void Default(string gameName, out bool swap, out bool flipX, out bool flipZ)
        {
            swap = false;
            flipX = false;
            flipZ = false;
            var g = TrackKeys.Normalize(gameName).Replace(" ", "");
            if (g == "assettocorsa") flipZ = true;
        }

        /// <summary>A short description of the axes for the page.</summary>
        public static string Describe(bool swap, bool flipX, bool flipZ)
        {
            var east = (flipX ? "-" : "") + (swap ? "z" : "x");
            var north = (flipZ ? "-" : "") + (swap ? "x" : "z");
            return east + " east, " + north + " north";
        }
    }
}

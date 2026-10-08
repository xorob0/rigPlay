// SPDX-License-Identifier: GPL-3.0-only
// VehicleStatus.cs: the telemetry fields that are not motion: night (#45, from the in-game time of day, the headlights,
// or the user's own SimHub property, with an Auto / Day / Night override on the page), and fuel level and range
// (#46). NightSources lists the SimHub properties the plugin tries and turns their boxed values into numbers.
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests).
using System;
using System.Globalization;

namespace RigPlayPlugin.Telemetry
{
    /// <summary>The page's night mode override (TelemetrySettings.NightMode).</summary>
    public static class NightModes
    {
        /// <summary>From the game: custom property, else time of day, else headlights; nothing when none is known.</summary>
        public const string Auto = "auto";
        public const string Day = "day";
        public const string Night = "night";

        public static readonly string[] All = { Auto, Day, Night };

        public static bool IsKnown(string mode)
        {
            return mode != null && Array.IndexOf(All, mode) >= 0;
        }

        public static string Label(string mode)
        {
            switch (mode)
            {
                case Auto: return "Auto (from the game)";
                case Day: return "Always day";
                case Night: return "Always night";
                default: return mode;
            }
        }
    }

    public static class VehicleStatus
    {
        /// <summary>In Auto, the in-game clock counts as night before 07:00 and from 19:00.</summary>
        public const double DayStartsSec = 7 * 3600;
        public const double NightStartsSec = 19 * 3600;

        /// <summary>
        /// The night field (spec §6.9.2): Day → false, Night → true; Auto → the custom property when set and readable,
        /// else the in-game time of day, else the headlights, else null (unknown: the tablet keeps its own source).
        /// </summary>
        public static bool? Night(string mode, ref TelemetryInput input)
        {
            if (mode == NightModes.Day) return false;
            if (mode == NightModes.Night) return true;
            if (input.CustomNight >= 0) return input.CustomNight == 1;
            if (TelemetrySampler.IsFinite(input.TimeOfDaySec))
            {
                var t = input.TimeOfDaySec % 86400.0;
                if (t < 0) t += 86400.0;
                return t < DayStartsSec || t >= NightStartsSec;
            }
            if (input.Headlights >= 0) return input.Headlights == 1;
            return null;
        }

        /// <summary>
        /// Fuel left in % of the tank, 0..100, one decimal (spec §6.9.2). SimHub's FuelPercent when the tank size is
        /// known; Fuel ÷ MaxFuel when FuelPercent is missing; null when the game publishes no fuel at all (all zero).
        /// </summary>
        public static double? FuelPercent(double fuelPercent, double fuel, double maxFuel)
        {
            double? value = null;
            var hasMax = TelemetrySampler.IsFinite(maxFuel) && maxFuel > 0;
            if (TelemetrySampler.IsFinite(fuelPercent) && fuelPercent > 0) value = fuelPercent;
            else if (hasMax && TelemetrySampler.IsFinite(fuel) && fuel >= 0) value = fuel / maxFuel * 100.0;
            else if (hasMax && TelemetrySampler.IsFinite(fuelPercent) && fuelPercent == 0) value = 0;
            if (value == null) return null;
            return Math.Round(Math.Min(100, Math.Max(0, value.Value)), 1, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Range on the remaining fuel in km, one decimal (spec §6.9.2): SimHub's estimate of the laps the fuel lasts ×
        /// the track length. Null when either is unknown.
        /// </summary>
        public static double? RangeKm(double remainingLaps, double trackLengthM)
        {
            if (!TelemetrySampler.IsFinite(remainingLaps) || remainingLaps < 0) return null;
            if (!TelemetrySampler.IsFinite(trackLengthM) || trackLengthM <= 0) return null;
            return Math.Round(remainingLaps * trackLengthM / 1000.0, 1, MidpointRounding.AwayFromZero);
        }
    }

    /// <summary>
    /// The SimHub properties tried for night (#45), as games publish them under DataCorePlugin.GameRawData. Read about
    /// once a second; a property the current game does not have reads as null and is skipped.
    /// </summary>
    public static class NightSources
    {
        /// <summary>In-game time of day, seconds since midnight: iRacing's SessionTimeOfDay, ACC's graphics clock.</summary>
        public static readonly string[] TimeOfDayProperties =
        {
            "DataCorePlugin.GameRawData.Telemetry.SessionTimeOfDay",
            "DataCorePlugin.GameRawData.Graphics.Clock",
        };

        /// <summary>Headlights on: ACC's light stage (0 off, 1 lights, 2 high beam), rFactor 2 / Le Mans Ultimate's mHeadlights.</summary>
        public static readonly string[] HeadlightProperties =
        {
            "DataCorePlugin.GameRawData.Graphics.LightsStage",
            "DataCorePlugin.GameRawData.CurrentPlayerTelemetry.mHeadlights",
            "DataCorePlugin.GameRawData.Telemetry.mHeadlights",
        };

        /// <summary>A boxed SimHub value as a number: numbers, bools (1/0), TimeSpans (seconds), numeric strings. NaN otherwise.</summary>
        public static double ToNumber(object value)
        {
            if (value == null) return double.NaN;
            if (value is bool b) return b ? 1 : 0;
            if (value is TimeSpan ts) return ts.TotalSeconds;
            if (value is string s)
            {
                double parsed;
                return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : double.NaN;
            }
            if (value is IConvertible c)
            {
                try
                {
                    var d = c.ToDouble(CultureInfo.InvariantCulture);
                    return double.IsInfinity(d) ? double.NaN : d;
                }
                catch (Exception)
                {
                    return double.NaN;
                }
            }
            return double.NaN;
        }

        /// <summary>A boxed value as a flag: 1 when non-zero (or true), 0 when zero, -1 when not a number.</summary>
        public static int ToFlag(object value)
        {
            var d = ToNumber(value);
            if (double.IsNaN(d)) return -1;
            return d != 0 ? 1 : 0;
        }

        /// <summary>The first property in <paramref name="names"/> that reads as a number, through <paramref name="read"/>.</summary>
        public static double FirstNumber(string[] names, Func<string, object> read)
        {
            foreach (var name in names)
            {
                object value;
                try { value = read(name); } catch (Exception) { continue; }
                var d = ToNumber(value);
                if (!double.IsNaN(d)) return d;
            }
            return double.NaN;
        }
    }
}

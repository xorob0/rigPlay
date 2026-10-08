// SPDX-License-Identifier: GPL-3.0-only
// TelemetryInput.cs: one frame of what the plugin reads from SimHub for the telemetry message (docs/protocol.md §6.9).
// RigPlay.DataUpdate fills it at 60 Hz from GameData.NewData without allocating; TelemetrySampler keeps the latest
// copy and feeds the GPS strategy with every frame. NaN (or null) means "the game does not publish it".
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests).
namespace RigPlayPlugin.Telemetry
{
    public struct TelemetryInput
    {
        /// <summary>SimHub reports a running game (GameData.GameRunning).</summary>
        public bool GameRunning;

        /// <summary>km/h; NaN when unknown.</summary>
        public double SpeedKmh;

        /// <summary>The sim gear as SimHub spells it: "R", "N", "1".."9", sometimes "P" or "D"; null when unknown.</summary>
        public string Gear;

        /// <summary>rev/min; NaN when unknown.</summary>
        public double Rpm;

        /// <summary>In the pit box (StatusDataBase.IsInPit).</summary>
        public bool InPit;

        /// <summary>Anywhere in the pit lane, box included (StatusDataBase.IsInPitLane).</summary>
        public bool InPitLane;

        /// <summary>Yaw in degrees as SimHub reports it (OrientationYaw); games without it report 0, see HeadingTracker.</summary>
        public double YawDeg;

        /// <summary>World coordinates in metres (CarCoordinates, axes game-dependent); NaN when unknown.</summary>
        public double X;
        public double Y;
        public double Z;

        /// <summary>
        /// Fraction of the lap driven, 0 ≤ p &lt; 1 (TrackPositionPercent; most games, iRacing included); NaN when unknown.
        /// Strategy C (#44) maps it onto a recorded centreline.
        /// </summary>
        public double TrackPct;

        /// <summary>SimHub saw the session restart (StatusDataBase.IsSessionRestart).</summary>
        public bool SessionRestart;

        /// <summary>Track (and layout) name; read about once a second, not per frame.</summary>
        public string TrackName;

        /// <summary>Session type ("Practice", "Race", ...); read about once a second.</summary>
        public string SessionType;

        /// <summary>The game's own track id (TrackCode, e.g. "spa gp" or "ks_nurburgring"); read about once a second.</summary>
        public string TrackCode;

        /// <summary>SimHub's game name (GameData.GameName, e.g. "IRacing", "AssettoCorsa"); read about once a second.</summary>
        public string GameName;

        // Read about once a second (#45, #46).

        /// <summary>Fuel left in % of the tank as SimHub computes it (FuelPercent); NaN when unknown.</summary>
        public double FuelPercent;

        /// <summary>Fuel left and tank capacity, in SimHub's fuel unit (Fuel, MaxFuel); NaN when unknown.</summary>
        public double Fuel;
        public double MaxFuel;

        /// <summary>SimHub's estimate of the laps the remaining fuel lasts (EstimatedFuelRemaingLaps); NaN when unknown.</summary>
        public double FuelRemainingLaps;

        /// <summary>Track length in metres (TrackLength, else ReportedTrackLength); NaN when unknown.</summary>
        public double TrackLengthM;

        /// <summary>In-game time of day, seconds since midnight; NaN when the game does not publish it.</summary>
        public double TimeOfDaySec;

        /// <summary>Headlights: 1 on, 0 off, -1 unknown.</summary>
        public int Headlights;

        /// <summary>The user's own night property (page setting): 1 night, 0 day, -1 unknown or not set.</summary>
        public int CustomNight;

        /// <summary>A frame with nothing known: the state before the first DataUpdate, or with no game.</summary>
        public static TelemetryInput Empty => new TelemetryInput
        {
            SpeedKmh = double.NaN,
            Rpm = double.NaN,
            YawDeg = double.NaN,
            X = double.NaN,
            Y = double.NaN,
            Z = double.NaN,
            TrackPct = double.NaN,
            FuelPercent = double.NaN,
            Fuel = double.NaN,
            MaxFuel = double.NaN,
            FuelRemainingLaps = double.NaN,
            TrackLengthM = double.NaN,
            TimeOfDaySec = double.NaN,
            Headlights = -1,
            CustomNight = -1,
        };
    }
}

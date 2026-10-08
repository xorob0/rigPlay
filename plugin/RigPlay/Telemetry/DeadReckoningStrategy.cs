// SPDX-License-Identifier: GPL-3.0-only
// DeadReckoningStrategy.cs: fake GPS strategy B (#43). The car starts at the origin of strategy A and drives: every
// SimHub frame moves the position by speed × time along the heading, on a great circle. It goes back to the origin on a
// session restart or a new track/session, when the car leaves the pit lane, after standing still for a while, and when
// it has drifted further than the drift radius from the origin. GeoMath holds the spherical-earth formulas.
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests).
using System;

namespace RigPlayPlugin.Telemetry
{
    /// <summary>Great-circle navigation on a spherical earth (mean radius); more than precise enough for a fake GPS.</summary>
    public static class GeoMath
    {
        /// <summary>Mean earth radius (IUGG), metres.</summary>
        public const double EarthRadiusM = 6371008.8;

        private const double Rad = Math.PI / 180.0;

        /// <summary>
        /// The point <paramref name="distanceM"/> from (lat, lon) along the initial bearing <paramref name="bearingDeg"/>
        /// (degrees clockwise from north). The longitude is wrapped to −180 ≤ lon &lt; 180, so crossing the antimeridian
        /// or a pole gives a valid position.
        /// </summary>
        public static void Destination(double lat, double lon, double bearingDeg, double distanceM, out double lat2, out double lon2)
        {
            var phi1 = lat * Rad;
            var lambda1 = lon * Rad;
            var theta = bearingDeg * Rad;
            var delta = distanceM / EarthRadiusM;
            var sinPhi1 = Math.Sin(phi1);
            var cosPhi1 = Math.Cos(phi1);
            var sinDelta = Math.Sin(delta);
            var cosDelta = Math.Cos(delta);
            var sinPhi2 = sinPhi1 * cosDelta + cosPhi1 * sinDelta * Math.Cos(theta);
            if (sinPhi2 > 1) sinPhi2 = 1;
            if (sinPhi2 < -1) sinPhi2 = -1;
            var phi2 = Math.Asin(sinPhi2);
            var y = Math.Sin(theta) * sinDelta * cosPhi1;
            var x = cosDelta - sinPhi1 * sinPhi2;
            var lambda2 = lambda1 + Math.Atan2(y, x);
            lat2 = phi2 / Rad;
            lon2 = NormalizeLon(lambda2 / Rad);
        }

        /// <summary>Great-circle distance in metres (haversine).</summary>
        public static double DistanceM(double lat1, double lon1, double lat2, double lon2)
        {
            var dPhi = (lat2 - lat1) * Rad;
            var dLambda = (lon2 - lon1) * Rad;
            var a = Math.Sin(dPhi / 2) * Math.Sin(dPhi / 2)
                + Math.Cos(lat1 * Rad) * Math.Cos(lat2 * Rad) * Math.Sin(dLambda / 2) * Math.Sin(dLambda / 2);
            if (a > 1) a = 1;
            return 2 * EarthRadiusM * Math.Asin(Math.Sqrt(a));
        }

        /// <summary>Any longitude to −180 ≤ lon &lt; 180.</summary>
        public static double NormalizeLon(double lon)
        {
            var l = (lon + 180.0) % 360.0;
            if (l < 0) l += 360.0;
            l -= 180.0;
            return l >= 180.0 ? l - 360.0 : l;
        }
    }

    /// <summary>Strategy B (#43): dead reckoning around the origin. See the file header.</summary>
    public sealed class DeadReckoningStrategy : IGpsStrategy
    {
        /// <summary>Slower than this counts as standing still.</summary>
        public const double StationaryMps = 0.5;

        private readonly double originLat;
        private readonly double originLon;
        private readonly double originAlt;
        private readonly double driftRadiusM;
        private readonly double stationaryResetSec;
        private double lat;
        private double lon;
        private bool atOrigin = true;
        private double stillFor;
        private bool lastPitLane;
        private bool lastRestart;
        private string lastTrack;
        private string lastSession;

        /// <param name="stationaryResetSec">Standing still this long returns to the origin; 0 never does.</param>
        public DeadReckoningStrategy(double originLat, double originLon, double originAlt, double driftRadiusM, double stationaryResetSec)
        {
            this.originLat = originLat;
            this.originLon = originLon;
            this.originAlt = originAlt;
            this.driftRadiusM = driftRadiusM;
            this.stationaryResetSec = stationaryResetSec;
            lat = originLat;
            lon = originLon;
        }

        /// <summary>How many times the car went back to the origin, and why the last time (page, tests).</summary>
        public int Resets { get; private set; }

        public string LastResetReason { get; private set; }

        public void Update(ref TelemetryInput input, double headingDeg, double dtSec)
        {
            if (input.SessionRestart && !lastRestart) ResetTo("session restart");
            lastRestart = input.SessionRestart;

            if (Changed(ref lastTrack, input.TrackName) | Changed(ref lastSession, input.SessionType)) ResetTo("new session");

            if (lastPitLane && !input.InPitLane) ResetTo("pit exit");
            lastPitLane = input.InPitLane;

            var speed = TelemetrySampler.IsFinite(input.SpeedKmh) ? Math.Max(0, input.SpeedKmh) / 3.6 : 0;
            if (speed < StationaryMps)
            {
                stillFor += dtSec;
                if (stationaryResetSec > 0 && stillFor >= stationaryResetSec && !atOrigin) ResetTo("stationary for " + stationaryResetSec + " s");
                return;
            }
            stillFor = 0;
            if (dtSec <= 0) return;

            var bearing = double.IsNaN(headingDeg) ? 0 : headingDeg;
            double lat2, lon2;
            GeoMath.Destination(lat, lon, bearing, speed * dtSec, out lat2, out lon2);
            lat = lat2;
            lon = lon2;
            atOrigin = false;
            if (GeoMath.DistanceM(originLat, originLon, lat, lon) > driftRadiusM) ResetTo("further than " + driftRadiusM / 1000 + " km from the origin");
        }

        public bool TryGetFix(out GpsFix fix)
        {
            fix = new GpsFix { Lat = lat, Lon = lon, Alt = originAlt };
            return true;
        }

        public void Reset()
        {
            ResetTo("session start");
            lastPitLane = false;
            lastRestart = false;
            lastTrack = null;
            lastSession = null;
        }

        private void ResetTo(string reason)
        {
            lat = originLat;
            lon = originLon;
            atOrigin = true;
            stillFor = 0;
            Resets++;
            LastResetReason = reason;
        }

        /// <summary>A known name that differs from the last known one. Unknown (null or empty) changes nothing.</summary>
        private static bool Changed(ref string last, string now)
        {
            if (string.IsNullOrEmpty(now)) return false;
            if (last == null || ReferenceEquals(last, now))
            {
                last = now;
                return false;
            }
            var changed = !string.Equals(last, now, StringComparison.Ordinal);
            last = now;
            return changed;
        }
    }
}

// SPDX-License-Identifier: GPL-3.0-only
// TrackMapping.cs: the two mappings of strategy C (#44, docs/protocol.md §6.9.3) and the lap recorder that builds a
// centreline. CentrelineMap interpolates a recorded lap by lap fraction (wrapping from the last sample back to the
// first across the start/finish line); AffineMap turns the game's world coordinates into a position around the origin;
// LapRecorder samples a lap every 0.5 % and closes the loop.
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests). Update-path methods do not allocate.
using System;
using System.Collections.Generic;

namespace RigPlayPlugin.Telemetry
{
    /// <summary>A recorded lap, interpolated linearly by lap fraction.</summary>
    public sealed class CentrelineMap
    {
        private readonly double[] pct;
        private readonly double[] lat;
        private readonly double[] lon;

        /// <param name="samples">Valid samples (TrackCalibration.CleanCentreline): sorted by Pct, unique, 0 ≤ Pct &lt; 1.</param>
        public CentrelineMap(IList<CentrelineSample> samples)
        {
            var n = samples?.Count ?? 0;
            if (n < 2) throw new ArgumentException("a centreline needs at least two samples", nameof(samples));
            pct = new double[n];
            lat = new double[n];
            lon = new double[n];
            for (var i = 0; i < n; i++)
            {
                pct[i] = samples[i].Pct;
                lat[i] = samples[i].Lat;
                lon[i] = samples[i].Lon;
                if (i > 0 && !(pct[i] > pct[i - 1])) throw new ArgumentException("centreline samples must be sorted by Pct", nameof(samples));
            }
        }

        public int Count => pct.Length;

        /// <summary>Any lap fraction to 0 ≤ p &lt; 1 (1.0 is the line again, 0).</summary>
        public static double Wrap(double p)
        {
            var w = p - Math.Floor(p);
            return w >= 1.0 ? 0.0 : w;
        }

        /// <summary>
        /// The position at lap fraction <paramref name="p"/>: linear between the two samples around it; before the
        /// first and after the last sample, between the last and the first (the segment across the line).
        /// </summary>
        public bool TryLocate(double p, out double outLat, out double outLon)
        {
            outLat = outLon = double.NaN;
            if (double.IsNaN(p) || double.IsInfinity(p)) return false;
            p = Wrap(p);
            var n = pct.Length;
            int a, b;
            double t;
            if (p < pct[0] || p >= pct[n - 1])
            {
                a = n - 1;
                b = 0;
                var span = 1.0 - pct[a] + pct[0];
                var into = p >= pct[a] ? p - pct[a] : 1.0 - pct[a] + p;
                t = span > 0 ? into / span : 0;
            }
            else
            {
                // Largest a with pct[a] <= p.
                int lo = 0, hi = n - 1;
                while (hi - lo > 1)
                {
                    var mid = (lo + hi) >> 1;
                    if (pct[mid] <= p) lo = mid; else hi = mid;
                }
                a = lo;
                b = hi;
                t = (p - pct[a]) / (pct[b] - pct[a]);
            }
            outLat = lat[a] + (lat[b] - lat[a]) * t;
            outLon = lon[a] + (GeoMath.NormalizeLon(lon[b] - lon[a])) * t;
            outLon = GeoMath.NormalizeLon(outLon);
            return true;
        }

        /// <summary>The direction of travel at <paramref name="p"/>: the bearing from p − δ to p + δ; NaN when unknown.</summary>
        public double BearingAt(double p, double delta = 0.0025)
        {
            double lat1, lon1, lat2, lon2;
            if (!TryLocate(p - delta, out lat1, out lon1) || !TryLocate(p + delta, out lat2, out lon2)) return double.NaN;
            return AffineMap.Bearing(lat1, lon1, lat2, lon2);
        }
    }

    /// <summary>
    /// World coordinates to a position (#44): (u, v) = (x − refX, z − refZ), swapped then flipped, rotated clockwise by
    /// the rotation, times the scale, gives metres east (e) and north (n) of the origin:
    /// e = s·(u·cos θ + v·sin θ), n = s·(−u·sin θ + v·cos θ). Metres become degrees on a local tangent plane
    /// (equirectangular around the origin), which is centimetre-exact over a circuit.
    /// </summary>
    public static class AffineMap
    {
        private const double Rad = Math.PI / 180.0;

        public static void ToLocal(double x, double z, double refX, double refZ, bool swap, bool flipX, bool flipZ,
            double rotationDeg, double scale, out double east, out double north)
        {
            var u = x - refX;
            var v = z - refZ;
            if (swap)
            {
                var tmp = u;
                u = v;
                v = tmp;
            }
            if (flipX) u = -u;
            if (flipZ) v = -v;
            var th = rotationDeg * Rad;
            var c = Math.Cos(th);
            var s = Math.Sin(th);
            east = scale * (u * c + v * s);
            north = scale * (-u * s + v * c);
        }

        /// <summary>The point <paramref name="east"/>, <paramref name="north"/> metres from the origin.</summary>
        public static void Offset(double originLat, double originLon, double east, double north, out double lat, out double lon)
        {
            lat = originLat + north / GeoMath.EarthRadiusM / Rad;
            var cos = Math.Cos(originLat * Rad);
            lon = GeoMath.NormalizeLon(originLon + (Math.Abs(cos) < 1e-9 ? 0 : east / (GeoMath.EarthRadiusM * cos) / Rad));
            if (lat > 90) lat = 90;
            if (lat < -90) lat = -90;
        }

        /// <summary>The whole mapping for <paramref name="cal"/> and <paramref name="gameName"/>'s axes.</summary>
        public static void Map(TrackCalibration cal, string gameName, double x, double z, out double lat, out double lon)
        {
            bool swap, flipX, flipZ;
            cal.Axes(gameName, out swap, out flipX, out flipZ);
            double e, n;
            ToLocal(x, z, cal.RefX, cal.RefZ, swap, flipX, flipZ, cal.RotationDeg, cal.Scale, out e, out n);
            Offset(cal.OriginLat, cal.OriginLon, e, n, out lat, out lon);
        }

        /// <summary>Initial great-circle bearing from point 1 to point 2, degrees clockwise from north, 0 ≤ b &lt; 360.</summary>
        public static double Bearing(double lat1, double lon1, double lat2, double lon2)
        {
            var phi1 = lat1 * Rad;
            var phi2 = lat2 * Rad;
            var dl = (lon2 - lon1) * Rad;
            var y = Math.Sin(dl) * Math.Cos(phi2);
            var x = Math.Cos(phi1) * Math.Sin(phi2) - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dl);
            if (Math.Abs(x) < 1e-15 && Math.Abs(y) < 1e-15) return double.NaN;
            return HeadingTracker.Normalize(Math.Atan2(y, x) / Rad);
        }
    }

    /// <summary>The recorder's state, for the page.</summary>
    public enum LapRecorderState
    {
        Idle,

        /// <summary>Started; recording begins when the car next crosses the start/finish line.</summary>
        WaitingForLine,

        Recording,

        /// <summary>A lap was recorded (or stopped with enough of it); <see cref="LapRecorder.Result"/> holds it.</summary>
        Done,

        /// <summary>Stopped before enough of the lap was recorded; <see cref="LapRecorder.Message"/> says why.</summary>
        Failed,
    }

    /// <summary>
    /// "Record a lap" (#44): after <see cref="Start"/>, waits for the start/finish line (lap fraction wrapping from
    /// above 0.75 to below 0.25), then keeps one sample every <see cref="Spacing"/> of the lap until the next crossing.
    /// At that crossing the loop is closed: the drift between where the car is and the first sample is spread linearly
    /// over the lap (dead reckoning drifts; a mapping that does not drift gets a near-zero correction). <see cref="Stop"/>
    /// ends early, keeping the lap if at least <see cref="MinCoverage"/> of it was recorded (the missing part is
    /// interpolated straight across the line). A jump of more than <see cref="MaxStep"/> of the lap between two frames
    /// (a reset, a tow to the pits, a shortcut) fails the recording.
    /// </summary>
    public sealed class LapRecorder
    {
        public const double Spacing = 0.005;
        public const double MinCoverage = 0.9;

        /// <summary>A bigger step between two frames is a jump (a reset, a tow, a shortcut), not driving.</summary>
        public const double MaxStep = 0.1;
        public const int MaxSamples = 1024;

        private readonly List<CentrelineSample> samples = new List<CentrelineSample>(256);
        private double lastPct = double.NaN;
        private double prevPct = double.NaN;

        public LapRecorderState State { get; private set; } = LapRecorderState.Idle;

        /// <summary>The recorded lap once <see cref="State"/> is Done: valid, sorted, rounded samples.</summary>
        public List<CentrelineSample> Result { get; private set; }

        /// <summary>Why the recording failed, or a summary of the result.</summary>
        public string Message { get; private set; } = "";

        public int SampleCount => samples.Count;

        /// <summary>The part of the lap recorded so far, 0..1.</summary>
        public double Coverage => samples.Count == 0 ? 0 : lastPct - samples[0].Pct;

        public bool Active => State == LapRecorderState.WaitingForLine || State == LapRecorderState.Recording;

        public void Start()
        {
            samples.Clear();
            Result = null;
            Message = "";
            lastPct = double.NaN;
            prevPct = double.NaN;
            State = LapRecorderState.WaitingForLine;
        }

        /// <summary>Back to idle, nothing kept.</summary>
        public void Cancel()
        {
            samples.Clear();
            Result = null;
            Message = "";
            State = LapRecorderState.Idle;
        }

        /// <summary>True when the lap fraction went from near 1 to near 0 between two frames (the line, forwards).</summary>
        public static bool CrossedLine(double previous, double now)
        {
            return !double.IsNaN(previous) && !double.IsNaN(now) && previous > 0.75 && now < 0.25;
        }

        /// <summary>
        /// Before the position update of this frame: true when recording starts now (the car just crossed the line), so
        /// the caller can put a dead-reckoned car back on the origin first.
        /// </summary>
        public bool BeginsAt(double pct)
        {
            if (State != LapRecorderState.WaitingForLine || double.IsNaN(pct)) return false;
            return CrossedLine(prevPct, CentrelineMap.Wrap(pct));
        }

        /// <summary>One frame: the lap fraction and the position the strategy made for it.</summary>
        public void Feed(double pct, double lat, double lon)
        {
            if (double.IsNaN(pct) || double.IsInfinity(pct) || double.IsNaN(lat) || double.IsNaN(lon)) return;
            pct = CentrelineMap.Wrap(pct);
            var crossed = CrossedLine(prevPct, pct);
            prevPct = pct;
            switch (State)
            {
                case LapRecorderState.WaitingForLine:
                    if (!crossed) return;
                    State = LapRecorderState.Recording;
                    Add(pct, lat, lon);
                    return;
                case LapRecorderState.Recording:
                    if (crossed)
                    {
                        Close(lat, lon);
                        return;
                    }
                    if (pct - lastPct > MaxStep)
                    {
                        Fail("the lap position jumped from " + Math.Round(lastPct * 100) + " % to " + Math.Round(pct * 100) + " % (a reset or a shortcut)");
                        return;
                    }
                    // Backwards (a spin, reversing) adds nothing; forwards, one sample per Spacing.
                    if (pct - lastPct >= Spacing && samples.Count < MaxSamples) Add(pct, lat, lon);
                    return;
            }
        }

        /// <summary>Ends the recording now (the page's Stop): Done when enough of the lap was recorded, else Failed.</summary>
        public void Stop()
        {
            if (State == LapRecorderState.WaitingForLine)
            {
                Fail("stopped before the car crossed the start/finish line");
                return;
            }
            if (State != LapRecorderState.Recording) return;
            if (Coverage < MinCoverage)
            {
                Fail("stopped after " + Math.Round(Coverage * 100) + " % of the lap; at least " + MinCoverage * 100 + " % is needed");
                return;
            }
            Finish(0, 0, "stopped after " + Math.Round(Coverage * 100) + " % of the lap");
        }

        private void Add(double pct, double lat, double lon)
        {
            samples.Add(new CentrelineSample(pct, lat, lon));
            lastPct = pct;
        }

        /// <summary>The car is on the line again at (lat, lon): spread the gap to the first sample over the lap.</summary>
        private void Close(double lat, double lon)
        {
            if (Coverage < MinCoverage)
            {
                Fail("the lap was not driven in full (" + Math.Round(Coverage * 100) + " % recorded)");
                return;
            }
            var first = samples[0];
            Finish(lat - first.Lat, GeoMath.NormalizeLon(lon - first.Lon), "lap closed");
        }

        /// <summary>
        /// Sample i moves by −(dLat, dLon) × (pct_i − pct_0) / (1 − pct_0): the first sample stays, a sample just before
        /// the line moves by almost the whole gap, so the last sample meets the first.
        /// </summary>
        private void Finish(double dLat, double dLon, string what)
        {
            var p0 = samples[0].Pct;
            var span = 1.0 - p0;
            var result = new List<CentrelineSample>(samples.Count);
            double drift = 0;
            foreach (var s in samples)
            {
                var k = span > 0 ? (s.Pct - p0) / span : 0;
                var lat = s.Lat - dLat * k;
                var lon = GeoMath.NormalizeLon(s.Lon - dLon * k);
                result.Add(new CentrelineSample(Math.Round(s.Pct, 5), Math.Round(lat, 7), Math.Round(lon, 7)));
            }
            if (samples.Count > 0) drift = GeoMath.DistanceM(0, 0, dLat, dLon * Math.Cos(samples[0].Lat * Math.PI / 180));
            Result = TrackCalibration.CleanCentreline(result);
            if (Result.Count < TrackCalibration.MinCentrelineSamples)
            {
                Fail("too few samples (" + result.Count + ")");
                return;
            }
            State = LapRecorderState.Done;
            Message = what + ": " + Result.Count + " samples" + (drift > 0.5 ? ", " + Math.Round(drift) + " m of drift spread over the lap" : "");
        }

        private void Fail(string why)
        {
            samples.Clear();
            Result = null;
            Message = why;
            State = LapRecorderState.Failed;
        }
    }
}

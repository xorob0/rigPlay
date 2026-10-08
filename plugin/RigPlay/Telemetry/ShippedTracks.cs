// SPDX-License-Identifier: GPL-3.0-only
// ShippedTracks.cs: the small table of circuit origins shipped with the plugin (Resources/tracks.json, embedded as
// "RigPlay.Tracks.json"), for strategy C (#44). Every entry is the start/finish line of a real circuit, approximate (the
// file says so and docs/TRACK_CALIBRATION.md explains how much); a user's calibration for the same track always wins.
// A track key matches an entry when one of the entry's aliases appears in it as whole words and none of its excluded
// words does; the longest matching alias wins ("nurburgring nordschleife" beats "nurburgring").
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests, which embeds the same resource).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RigPlayPlugin.Telemetry
{
    /// <summary>One circuit of the shipped table.</summary>
    public sealed class ShippedTrack
    {
        public string Name;
        public double Lat;
        public double Lon;

        /// <summary>How far off the coordinates may be, metres (the table's own estimate).</summary>
        public double UncertaintyM;

        /// <summary>Normalised aliases; a track key containing one of them (as words) is this circuit.</summary>
        public string[] Aliases = new string[0];

        /// <summary>Normalised words that rule the circuit out (e.g. "nordschleife" for the GP circuit).</summary>
        public string[] Exclude = new string[0];
    }

    public sealed class ShippedTracks
    {
        /// <summary>The resource name of Resources/tracks.json in RigPlay.dll (and in the test assembly).</summary>
        public const string ResourceName = "RigPlay.Tracks.json";

        private static readonly object DefaultSync = new object();
        private static ShippedTracks defaultTable;

        public ShippedTracks(IEnumerable<ShippedTrack> tracks, bool approximate)
        {
            Tracks = (tracks ?? Enumerable.Empty<ShippedTrack>()).ToList();
            Approximate = approximate;
        }

        public IReadOnlyList<ShippedTrack> Tracks { get; }

        /// <summary>The table's "approximate" flag (true for the shipped file).</summary>
        public bool Approximate { get; }

        /// <summary>An empty table, when the resource is missing or broken.</summary>
        public static readonly ShippedTracks Empty = new ShippedTracks(null, true);

        /// <summary>The embedded table, read once; empty (and logged) when it cannot be read.</summary>
        public static ShippedTracks Default
        {
            get
            {
                lock (DefaultSync)
                {
                    if (defaultTable != null) return defaultTable;
                    try
                    {
                        using (var stream = typeof(ShippedTracks).Assembly.GetManifestResourceStream(ResourceName))
                        {
                            if (stream == null) throw new InvalidOperationException("resource " + ResourceName + " not found");
                            using (var reader = new StreamReader(stream))
                                defaultTable = Parse(reader.ReadToEnd());
                        }
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Error("The shipped track table could not be read", ex);
                        defaultTable = Empty;
                    }
                    return defaultTable;
                }
            }
        }

        /// <summary>
        /// Reads the table: {"approximate": true, "tracks": [{"name", "lat", "lon", "uncertaintyM", "aliases": [...],
        /// "exclude": [...]}]}. Entries without a name, valid coordinates or an alias are skipped.
        /// </summary>
        public static ShippedTracks Parse(string json)
        {
            var root = JObject.Parse(json);
            var approximate = root.Value<bool?>("approximate") ?? true;
            var list = new List<ShippedTrack>();
            var tracks = root["tracks"] as JArray;
            if (tracks != null)
            {
                foreach (var t in tracks.OfType<JObject>())
                {
                    var lat = t.Value<double?>("lat");
                    var lon = t.Value<double?>("lon");
                    var name = t.Value<string>("name");
                    if (string.IsNullOrWhiteSpace(name) || !lat.HasValue || !lon.HasValue) continue;
                    if (!TrackCalibration.Finite(lat.Value) || Math.Abs(lat.Value) > 90 || !TrackCalibration.Finite(lon.Value) || Math.Abs(lon.Value) > 180) continue;
                    var aliases = Words(t["aliases"]);
                    if (aliases.Length == 0) continue;
                    list.Add(new ShippedTrack
                    {
                        Name = name.Trim(),
                        Lat = lat.Value,
                        Lon = lon.Value,
                        UncertaintyM = t.Value<double?>("uncertaintyM") ?? double.NaN,
                        Aliases = aliases,
                        Exclude = Words(t["exclude"]),
                    });
                }
            }
            return new ShippedTracks(list, approximate);
        }

        private static string[] Words(JToken token)
        {
            var array = token as JArray;
            if (array == null) return new string[0];
            return array.Select(a => TrackKeys.Normalize(a.Type == JTokenType.String ? (string)a : null))
                .Where(a => a.Length > 0).Distinct().ToArray();
        }

        /// <summary>The circuit for a normalised track key; null when none matches.</summary>
        public ShippedTrack Find(string trackKey)
        {
            if (string.IsNullOrEmpty(trackKey)) return null;
            ShippedTrack best = null;
            var bestLength = 0;
            foreach (var t in Tracks)
            {
                if (t.Exclude.Any(x => TrackKeys.ContainsWords(trackKey, x))) continue;
                foreach (var alias in t.Aliases)
                {
                    if (alias.Length > bestLength && TrackKeys.ContainsWords(trackKey, alias))
                    {
                        best = t;
                        bestLength = alias.Length;
                    }
                }
            }
            return best;
        }

        /// <summary>The shipped entry as a calibration (origin only: no rotation, scale 1, game axes).</summary>
        public static TrackCalibration ToCalibration(ShippedTrack track, string trackKey)
        {
            return new TrackCalibration
            {
                TrackKey = trackKey ?? "",
                Name = track.Name,
                Source = CalibrationSources.Shipped,
                OriginLat = track.Lat,
                OriginLon = track.Lon,
            };
        }
    }

    /// <summary>Which calibration applies to a track: the user's, else the shipped table's, else none.</summary>
    public static class TrackCalibrations
    {
        /// <summary>
        /// The user's calibration for <paramref name="trackKey"/> (exact key), else the shipped table's match as a
        /// calibration, else null. The result is the object from the list or a new one; callers that keep it should
        /// clone the user's entry.
        /// </summary>
        public static TrackCalibration Resolve(IEnumerable<TrackCalibration> user, ShippedTracks shipped, string trackKey)
        {
            if (string.IsNullOrEmpty(trackKey)) return null;
            var mine = user?.FirstOrDefault(c => c != null && string.Equals(c.TrackKey, trackKey, StringComparison.Ordinal));
            if (mine != null) return mine;
            var table = shipped?.Find(trackKey);
            return table == null ? null : ShippedTracks.ToCalibration(table, trackKey);
        }
    }
}

// SPDX-License-Identifier: GPL-3.0-only
// TrackSection.cs: the rows of the "Data to CarPlay" section for fake GPS strategy C (#44, docs/TRACK_CALIBRATION.md):
// the current track and where its calibration comes from (none / shipped / user / centreline), the track's origin,
// rotation, scale and axes, "Set origin to here", and "Record a lap". TelemetrySection shows them when the strategy is
// "Real track". Edits replace Settings.Telemetry.Tracks with a new list (the sender thread may be reading the old one),
// save, and bump the revision so the sampler rebuilds strategy C within 100 ms.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RigPlayPlugin.Telemetry;

namespace RigPlayPlugin
{
    internal sealed class TrackSection
    {
        private readonly RigPlay plugin;
        private readonly TextBlock trackText = Ui.Text("");
        private readonly TextBlock detailText = MakeCaption();
        private readonly TextBlock recorderText = MakeCaption();
        private TextBox origin;
        private TextBox rotation;
        private TextBox scale;
        private ComboBox axes;
        private bool fillingAxes;
        private string shownKey;
        private TrackStatus status;
        private string savedMessage;

        public TrackSection(RigPlay plugin)
        {
            this.plugin = plugin;
        }

        private TelemetrySettings Settings => plugin.Settings.Telemetry;

        private TelemetrySampler Sampler => plugin.Host?.TelemetrySampler;

        private static TextBlock MakeCaption()
        {
            var block = Ui.Caption("");
            block.MaxWidth = 620;
            return block;
        }

        private sealed class AxesChoice
        {
            public bool Auto;
            public bool Swap;
            public bool FlipX;
            public bool FlipZ;
            public string Label;

            public override string ToString()
            {
                return Label;
            }
        }

        public List<FrameworkElement> Rows()
        {
            var track = Ui.Row("Track", Ui.VStack(4, trackText, detailText));
            var rows = new List<FrameworkElement> { track };

            origin = PageKit.CommitTextBox("", 230, CommitOrigin);
            origin.ToolTip = "The real coordinates of the track's origin (normally the start/finish line), \"lat, lon\" as copied from a map.";
            var here = PageKit.SecondaryButton("Set origin to here", SetOriginHere);
            here.ToolTip = "Stop the car on a spot you know (the start/finish line), press this, then paste that spot's real \"lat, lon\" into the box.";
            var forget = PageKit.SecondaryButton("Forget this track", Forget);
            forget.ToolTip = "Delete your calibration of this track (origin, rotation, recorded lap); the shipped table applies again.";
            rows.Add(Ui.Row("Track origin (lat, lon)", Ui.VStack(4,
                Ui.HStack(8, origin, here, forget),
                Ui.Caption("Overrides the shipped table for this track. With world coordinates, the car's spot at \"Set origin to here\" maps onto it."))));

            rotation = PageKit.CommitTextBox("", 70, box => CommitNumber(box, -360, 360, "rotation", (c, v) => c.RotationDeg = v, c => c.RotationDeg));
            scale = PageKit.CommitTextBox("", 70, box => CommitNumber(box, TrackCalibration.MinScale, TrackCalibration.MaxScale, "scale", (c, v) => c.Scale = v, c => c.Scale));
            rotation.ToolTip = "Degrees clockwise that turn the game's track onto the real one (also turns a dead-reckoned heading).";
            scale.ToolTip = "Metres on the map per game unit; 1 for games in metres.";
            axes = new ComboBox { Width = 230 };
            axes.SelectionChanged += (s, e) => PageKit.Safe(CommitAxes);
            rows.Add(Ui.Row("Rotation °, scale, axes", Ui.VStack(4,
                Ui.HStack(8, rotation, scale, axes),
                Ui.Caption("Turn the track until the main straight points the right way; pick the other axes if it comes out mirrored."))));

            var start = PageKit.SecondaryButton("Start recording", StartRecording);
            var stop = PageKit.SecondaryButton("Stop", StopRecording);
            start.ToolTip = "Records the next full lap (from the start/finish line to the next crossing) as this track's centreline.";
            rows.Add(Ui.Row("Record a lap", Ui.VStack(4,
                Ui.HStack(8, start, stop),
                recorderText)));

            // Last, so the first refresh finds every control.
            PageKit.Live(track, 500, Refresh);
            return rows;
        }

        private void Refresh()
        {
            var sampler = Sampler;
            if (sampler == null)
            {
                trackText.Text = "Not running";
                return;
            }
            sampler.EnsureStrategy(Settings);
            var lap = sampler.WithStrategy(s => (s as TrackGeoReferenceStrategy)?.TakeRecordedLap());
            if (lap != null) SaveLap(lap);
            status = sampler.WithStrategy(s => (s as TrackGeoReferenceStrategy)?.Status());
            if (status == null)
            {
                trackText.Text = "Waiting for the strategy";
                return;
            }
            if (status.TrackKey.Length == 0)
            {
                trackText.Text = "No track yet: start a session in the game.";
                detailText.Text = "Until then the car sits at the origin above.";
            }
            else
            {
                trackText.Text = "\"" + status.TrackKey + "\", calibration: " + SourceText(status);
                detailText.Text = "Placed by: " + status.Mode
                    + " · lap position: " + (status.HasLapFraction ? "yes" : "not seen")
                    + " · world coordinates: " + (status.HasWorldCoordinates ? "yes" : "not seen")
                    + (double.IsNaN(status.X) || double.IsNaN(status.Z) ? "" : " (x " + status.X.ToString("0.#", CultureInfo.InvariantCulture) + ", z " + status.Z.ToString("0.#", CultureInfo.InvariantCulture) + ")")
                    + " · now " + Format(status.Lat) + ", " + Format(status.Lon);
            }
            recorderText.Text = status.Recorder == LapRecorderState.Idle && savedMessage != null ? savedMessage : RecorderText(status);
            if (status.TrackKey != shownKey) ShowCalibration();
        }

        private static string SourceText(TrackStatus s)
        {
            switch (s.Source)
            {
                case CalibrationSources.Shipped:
                    return "shipped (" + s.ShippedName + ", approximate"
                        + (double.IsNaN(s.ShippedUncertaintyM) ? "" : ", ±" + s.ShippedUncertaintyM.ToString("0", CultureInfo.InvariantCulture) + " m") + ")";
                case CalibrationSources.User: return "yours";
                case CalibrationSources.Centreline: return "yours, with a recorded lap (" + s.Calibration.Centreline.Count + " points)";
                default: return "none (the origin above)";
            }
        }

        private static string RecorderText(TrackStatus s)
        {
            switch (s.Recorder)
            {
                case LapRecorderState.WaitingForLine: return "Waiting for the car to cross the start/finish line…";
                case LapRecorderState.Recording:
                    return "Recording: " + Math.Round(s.RecorderCoverage * 100) + " % of the lap, " + s.RecorderSamples + " points. Drive to the line to finish.";
                case LapRecorderState.Done: return "Recorded (" + s.RecorderMessage + ").";
                case LapRecorderState.Failed: return "Not recorded: " + s.RecorderMessage + ".";
                default:
                    if (!s.HasLapFraction && s.TrackKey.Length > 0) return "This game has not published a lap position yet; recording needs one.";
                    return "Drive one clean lap: the recording starts and ends on the start/finish line.";
            }
        }

        /// <summary>Puts the calibration in effect into the boxes (on a new track, or after a change).</summary>
        private void ShowCalibration()
        {
            shownKey = status?.TrackKey;
            var cal = status?.Calibration;
            var enabled = cal != null && !string.IsNullOrEmpty(shownKey);
            origin.IsEnabled = rotation.IsEnabled = scale.IsEnabled = axes.IsEnabled = enabled;
            if (cal == null) return;
            origin.Text = Format(cal.OriginLat) + ", " + Format(cal.OriginLon);
            rotation.Text = Format(cal.RotationDeg);
            scale.Text = Format(cal.Scale);

            fillingAxes = true;
            try
            {
                bool ds, dx, dz;
                GameAxes.Default(status.GameName, out ds, out dx, out dz);
                var choices = new List<AxesChoice> { new AxesChoice { Auto = true, Label = "Game default (" + GameAxes.Describe(ds, dx, dz) + ")" } };
                foreach (var swap in new[] { false, true })
                    foreach (var fx in new[] { false, true })
                        foreach (var fz in new[] { false, true })
                            choices.Add(new AxesChoice { Swap = swap, FlipX = fx, FlipZ = fz, Label = GameAxes.Describe(swap, fx, fz) });
                axes.ItemsSource = choices;
                axes.SelectedItem = cal.AxesAuto
                    ? choices[0]
                    : choices.FirstOrDefault(c => !c.Auto && c.Swap == cal.SwapAxes && c.FlipX == cal.FlipX && c.FlipZ == cal.FlipZ) ?? choices[0];
            }
            finally
            {
                fillingAxes = false;
            }
        }

        private bool HaveTrack()
        {
            if (status != null && status.TrackKey.Length > 0 && status.Calibration != null) return true;
            Log.Info("Track calibration: no track yet (Data to CarPlay)");
            return false;
        }

        /// <summary>
        /// Edits the user's calibration of the current track (made from the one in effect when there is none): a copy is
        /// changed and put into a new list, which replaces Settings.Tracks.
        /// </summary>
        private void Edit(string what, Action<TrackCalibration> change)
        {
            if (!HaveTrack()) return;
            var key = status.TrackKey;
            var existing = Settings.FindTrack(key);
            var cal = existing != null ? existing.Clone() : status.Calibration.Clone();
            cal.TrackKey = key;
            if (string.IsNullOrEmpty(cal.Name)) cal.Name = status.TrackName;
            change(cal);
            cal.Normalize();
            var list = (Settings.Tracks ?? new List<TrackCalibration>()).Where(t => t != null && t.TrackKey != key).ToList();
            list.Add(cal);
            Settings.Tracks = list;
            Settings.MarkTracksChanged();
            plugin.SaveSettings();
            Log.Info("Track calibration of '" + key + "': " + what + " (Data to CarPlay)");
            shownKey = null; // show the new values on the next refresh
            Refresh();
        }

        private void CommitOrigin(TextBox box)
        {
            double lat, lon;
            if (!HaveTrack()) return;
            if (!GeoText.TryParseLatLon(box.Text, out lat, out lon))
            {
                box.Text = Format(status.Calibration.OriginLat) + ", " + Format(status.Calibration.OriginLon);
                return;
            }
            if (lat == status.Calibration.OriginLat && lon == status.Calibration.OriginLon) return; // unchanged (focus left the box)
            Edit("origin set to " + Format(lat) + ", " + Format(lon), c =>
            {
                c.OriginLat = lat;
                c.OriginLon = lon;
            });
        }

        private void CommitNumber(TextBox box, double min, double max, string what, Action<TrackCalibration, double> set, Func<TrackCalibration, double> get)
        {
            if (!HaveTrack()) return;
            double value;
            if (!GeoText.TryParse(box.Text, min, max, out value) || value == get(status.Calibration))
            {
                box.Text = Format(get(status.Calibration));
                return;
            }
            Edit(what + " set to " + Format(value), c => set(c, value));
        }

        private void CommitAxes()
        {
            if (fillingAxes || status == null) return;
            var choice = axes.SelectedItem as AxesChoice;
            if (choice == null || !HaveTrack()) return;
            var cal = status.Calibration;
            if (choice.Auto == cal.AxesAuto && (choice.Auto || (choice.Swap == cal.SwapAxes && choice.FlipX == cal.FlipX && choice.FlipZ == cal.FlipZ))) return;
            Edit("axes set to " + choice.Label, c =>
            {
                c.AxesAuto = choice.Auto;
                c.SwapAxes = choice.Swap;
                c.FlipX = choice.FlipX;
                c.FlipZ = choice.FlipZ;
            });
        }

        /// <summary>
        /// The car's position now becomes the origin (nothing moves); with world coordinates the car's spot becomes the
        /// reference point. Pasting the spot's real coordinates afterwards puts the track in place.
        /// </summary>
        private void SetOriginHere()
        {
            if (!HaveTrack()) return;
            var s = status;
            if (!s.HasFix)
            {
                Log.Info("Track calibration: no position yet (Data to CarPlay)");
                return;
            }
            Edit("origin set to here (" + Format(s.Lat) + ", " + Format(s.Lon) + (s.HasWorldCoordinates ? ", world " + Format(s.X) + ", " + Format(s.Z) : "") + ")", c =>
            {
                c.OriginLat = s.Lat;
                c.OriginLon = s.Lon;
                if (s.HasWorldCoordinates && !double.IsNaN(s.X) && !double.IsNaN(s.Z))
                {
                    c.RefX = s.X;
                    c.RefZ = s.Z;
                }
            });
        }

        private void Forget()
        {
            if (!HaveTrack()) return;
            var key = status.TrackKey;
            if (Settings.FindTrack(key) == null) return;
            Settings.Tracks = Settings.Tracks.Where(t => t != null && t.TrackKey != key).ToList();
            Settings.MarkTracksChanged();
            plugin.SaveSettings();
            Log.Info("Track calibration of '" + key + "' deleted (Data to CarPlay)");
            savedMessage = null;
            shownKey = null;
            Refresh();
        }

        private void StartRecording()
        {
            var sampler = Sampler;
            if (sampler == null) return;
            sampler.EnsureStrategy(Settings);
            string why = null;
            var started = sampler.WithStrategy(s =>
            {
                var c = s as TrackGeoReferenceStrategy;
                if (c == null)
                {
                    why = "the strategy is not Real track";
                    return false;
                }
                return c.StartRecording(out why);
            });
            if (started) savedMessage = null;
            Log.Info("Track recording " + (started ? "started" : "not started") + ": " + why + " (Data to CarPlay)");
            Refresh();
        }

        private void StopRecording()
        {
            Sampler?.WithStrategy(s =>
            {
                (s as TrackGeoReferenceStrategy)?.StopRecording();
                return true;
            });
            Refresh();
        }

        /// <summary>A finished lap becomes the centreline of the track it was recorded on.</summary>
        private void SaveLap(RecordedLap lap)
        {
            if (lap?.Samples == null || string.IsNullOrEmpty(lap.TrackKey)) return;
            var existing = Settings.FindTrack(lap.TrackKey);
            var cal = existing != null ? existing.Clone() : lap.RecordedWith.Clone();
            cal.TrackKey = lap.TrackKey;
            if (string.IsNullOrEmpty(cal.Name)) cal.Name = lap.TrackName;
            cal.Centreline = lap.Samples;
            cal.Normalize();
            var list = (Settings.Tracks ?? new List<TrackCalibration>()).Where(t => t != null && t.TrackKey != lap.TrackKey).ToList();
            list.Add(cal);
            Settings.Tracks = list;
            Settings.MarkTracksChanged();
            plugin.SaveSettings();
            Log.Info("Track calibration of '" + lap.TrackKey + "': recorded lap saved, " + lap.Message + " (Data to CarPlay)");
            savedMessage = "Saved as the centreline of \"" + lap.TrackKey + "\" (" + lap.Message + ").";
            shownKey = null;
        }

        private static string Format(double value)
        {
            return double.IsNaN(value) ? "?" : value.ToString("0.#######", CultureInfo.InvariantCulture);
        }
    }
}

// SPDX-License-Identifier: GPL-3.0-only
// TelemetrySection.cs: the "Data to CarPlay" section of the rigPlay page (#40, docs/protocol.md §6.9): the master
// switch, one switch per telemetry field, the fake-GPS strategy and its settings, and a live line saying what is sent
// and to whom. A change is saved at once and used by the next message (within 100 ms).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using RigPlayPlugin.Telemetry;

namespace RigPlayPlugin
{
    internal sealed class TelemetrySection
    {
        private readonly RigPlay plugin;
        private readonly TextBlock statusText = Ui.Text("");
        private readonly TextBlock lastText = MakeLast();
        private readonly StackPanel strategyRows = new StackPanel { Orientation = Orientation.Vertical };
        private ComboBox strategy;

        private static TextBlock MakeLast()
        {
            var block = Ui.Caption("");
            block.MaxWidth = 560;
            return block;
        }

        public TelemetrySection(RigPlay plugin)
        {
            this.plugin = plugin;
        }

        private TelemetrySettings Settings => plugin.Settings.Telemetry;

        private sealed class Choice
        {
            public string Name;
            public string Label;

            public override string ToString()
            {
                return Label;
            }
        }

        public FrameworkElement Build()
        {
            var master = Ui.Toggle(Settings.Enabled, on => Change("Telemetry " + (on ? "on" : "off"), () => Settings.Enabled = on));

            var fields = new UniformGrid { Columns = 3, HorizontalAlignment = HorizontalAlignment.Left };
            AddField(fields, "Speed", Settings.SendSpeed, v => Settings.SendSpeed = v);
            AddField(fields, "Gear (P/R/N/D)", Settings.SendGear, v => Settings.SendGear = v);
            AddField(fields, "Heading", Settings.SendHeading, v => Settings.SendHeading = v);
            AddField(fields, "Night mode", Settings.SendNight, v => Settings.SendNight = v);
            AddField(fields, "Fuel level", Settings.SendFuel, v => Settings.SendFuel = v);
            AddField(fields, "Range", Settings.SendRange, v => Settings.SendRange = v);
            AddField(fields, "RPM", Settings.SendRpm, v => Settings.SendRpm = v);
            AddField(fields, "Track name", Settings.SendTrackName, v => Settings.SendTrackName = v);
            AddField(fields, "Session type", Settings.SendSessionType, v => Settings.SendSessionType = v);

            strategy = new ComboBox { Width = 320 };
            var choices = GpsStrategies.All.Select(n => new Choice { Name = n, Label = GpsStrategies.Label(n) }).ToList();
            strategy.ItemsSource = choices;
            strategy.SelectedItem = choices.FirstOrDefault(c => c.Name == Settings.GpsStrategy) ?? choices[0];
            strategy.SelectionChanged += (s, e) => PageKit.Safe(() =>
            {
                var choice = strategy.SelectedItem as Choice;
                if (choice == null || choice.Name == Settings.GpsStrategy) return;
                Change("Telemetry position set to " + choice.Name, () => Settings.GpsStrategy = choice.Name);
                FillStrategyRows();
            });
            FillStrategyRows();

            var night = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
            var nightChoices = NightModes.All.Select(n => new Choice { Name = n, Label = NightModes.Label(n) }).ToList();
            night.ItemsSource = nightChoices;
            night.SelectedItem = nightChoices.FirstOrDefault(c => c.Name == Settings.NightMode) ?? nightChoices[0];
            night.SelectionChanged += (s, e) => PageKit.Safe(() =>
            {
                var choice = night.SelectedItem as Choice;
                if (choice == null || choice.Name == Settings.NightMode) return;
                Change("Telemetry night mode set to " + choice.Name, () => Settings.NightMode = choice.Name);
            });
            var nightProperty = PageKit.CommitTextBox(Settings.NightProperty, 320, box =>
            {
                var value = (box.Text ?? "").Trim();
                if (value == Settings.NightProperty) return;
                Change("Telemetry night property set to '" + value + "'", () => Settings.NightProperty = value);
            });
            nightProperty.ToolTip = "Optional: a SimHub property that is true or non-zero at night, e.g. a game's headlight flag. "
                + "In Auto it wins over the in-game clock and the headlights.";

            var section = Ui.Section("Data to CarPlay",
                "SimHub data sent to the tablet while a game runs and an iPhone is connected (at most 10 times a second), so CarPlay "
                + "shows the car's speed and gear, a made-up GPS position for Maps, and night mode.",
                Ui.Row("Send SimHub data", master),
                Ui.Row("Fields", fields),
                Ui.Row("Position (fake GPS)", strategy),
                strategyRows,
                Ui.Row("Night mode", Ui.VStack(4,
                    night,
                    Ui.Caption("Auto: night from 19:00 to 07:00 in-game time, else headlights on (games that publish them)."))),
                Ui.Row("Night property (optional)", nightProperty),
                Ui.Row("Now", Ui.VStack(4, statusText, lastText)));
            PageKit.Live(section, 500, Refresh);
            return section;
        }

        private void AddField(UniformGrid grid, string label, bool value, Action<bool> set)
        {
            var toggle = Ui.Toggle(value, on => Change("Telemetry field " + label + " " + (on ? "on" : "off"), () => set(on)));
            // SHToggleButton draws a little past its layout box, so the label keeps a wider gap.
            var text = Ui.Text(label);
            text.Margin = new Thickness(14, 0, 0, 0);
            var cell = Ui.HStack(8, toggle, text);
            cell.Margin = new Thickness(0, 0, 24, 6);
            grid.Children.Add(cell);
        }

        /// <summary>The settings rows of the selected strategy (origin, reset rules); none for Off.</summary>
        private void FillStrategyRows()
        {
            strategyRows.Children.Clear();
            foreach (var row in StrategyRows(Settings.GpsStrategy))
            {
                row.Margin = new Thickness(0, 0, 0, 10);
                strategyRows.Children.Add(row);
            }
            strategyRows.Visibility = strategyRows.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private IEnumerable<FrameworkElement> StrategyRows(string name)
        {
            if (name == GpsStrategies.Off) yield break;
            yield return OriginRow();
            if (name == GpsStrategies.DeadReckoning || name == GpsStrategies.Track) yield return DeadReckoningRow();
            if (name != GpsStrategies.Track) yield break;
            // Strategy C (#44): the page origin and the reset rules apply to tracks without calibration and to dead reckoning.
            foreach (var row in new TrackSection(plugin).Rows()) yield return row;
        }

        /// <summary>When dead reckoning puts the car back on the origin (#43), and a button to do it now.</summary>
        private FrameworkElement DeadReckoningRow()
        {
            var radius = PageKit.CommitTextBox(Format(Settings.DriftRadiusKm), 60, box =>
            {
                double value;
                if (GeoText.TryParse(box.Text, TelemetrySettings.MinDriftRadiusKm, TelemetrySettings.MaxDriftRadiusKm, out value) && value != Settings.DriftRadiusKm)
                    Change("Telemetry drift radius set to " + Format(value) + " km", () => Settings.DriftRadiusKm = value);
                box.Text = Format(Settings.DriftRadiusKm);
            });
            var still = PageKit.CommitTextBox(Settings.StationaryResetSec.ToString(), 60, box =>
            {
                int value;
                if (int.TryParse((box.Text ?? "").Trim(), out value) && value >= 0 && value <= TelemetrySettings.MaxStationaryResetSec && value != Settings.StationaryResetSec)
                    Change("Telemetry stationary reset set to " + value + " s", () => Settings.StationaryResetSec = value);
                box.Text = Settings.StationaryResetSec.ToString();
            });
            radius.ToolTip = "Kilometres from the origin before the car is put back on it.";
            still.ToolTip = "Seconds standing still before the car is put back on the origin; 0 never.";
            var now = PageKit.SecondaryButton("Back to the origin now", () =>
            {
                plugin.Host?.TelemetrySampler.ResetMotion();
                Log.Info("Telemetry position reset to the origin from the page");
            });
            return Ui.Row("Back to the origin", Ui.VStack(4,
                Ui.HStack(8, Ui.Text("beyond"), radius, Ui.Text("km, or after"), still, Ui.Text("s standing still"), now),
                Ui.Caption("Also on a session restart, a new track or session, and when leaving the pit lane.")));
        }

        /// <summary>
        /// Latitude, longitude and altitude of the origin (#42). Pasting "lat, lon" as copied from a map into the latitude
        /// box fills both. A value out of range is refused and the box shows the stored value again.
        /// </summary>
        private FrameworkElement OriginRow()
        {
            TextBox lat = null, lon = null;
            lat = PageKit.CommitTextBox(Format(Settings.OriginLat), 110, box =>
            {
                double a, b;
                if (GeoText.TryParseLatLon(box.Text, out a, out b))
                {
                    if (a != Settings.OriginLat || b != Settings.OriginLon)
                        Change("Telemetry origin set to " + Format(a) + ", " + Format(b), () => { Settings.OriginLat = a; Settings.OriginLon = b; });
                    box.Text = Format(Settings.OriginLat);
                    lon.Text = Format(Settings.OriginLon);
                    return;
                }
                CommitNumber(box, -90, 90, () => Settings.OriginLat, v => Settings.OriginLat = v, "latitude");
            });
            lon = PageKit.CommitTextBox(Format(Settings.OriginLon), 110, box =>
                CommitNumber(box, -180, 180, () => Settings.OriginLon, v => Settings.OriginLon = v, "longitude"));
            var alt = PageKit.CommitTextBox(Format(Settings.OriginAlt), 70, box =>
                CommitNumber(box, -1000, 10000, () => Settings.OriginAlt, v => Settings.OriginAlt = v, "altitude"));
            lat.ToolTip = "Latitude in degrees (dot as decimal separator). Paste \"lat, lon\" from a map here to fill both.";
            lon.ToolTip = "Longitude in degrees.";
            alt.ToolTip = "Altitude in metres above sea level.";
            return Ui.Row("Origin (lat, lon, alt m)", Ui.VStack(4,
                Ui.HStack(8, lat, lon, alt),
                Ui.Caption("Where the car is placed on the map. Paste \"lat, lon\" from a map into the first box.")));
        }

        private void CommitNumber(TextBox box, double min, double max, Func<double> get, Action<double> set, string what)
        {
            double value;
            if (!GeoText.TryParse(box.Text, min, max, out value))
            {
                box.Text = Format(get());
                return;
            }
            if (value != get()) Change("Telemetry origin " + what + " set to " + Format(value), () => set(value));
            box.Text = Format(get());
        }

        private static string Format(double value)
        {
            return value.ToString("0.#######", System.Globalization.CultureInfo.InvariantCulture);
        }

        private void Change(string what, Action apply)
        {
            apply();
            plugin.SaveSettings();
            Log.Info(what + " (Data to CarPlay)");
            Refresh();
        }

        private void Refresh()
        {
            var sender = plugin.Host?.TelemetrySender;
            if (sender == null)
            {
                statusText.Text = "Not running";
                lastText.Text = "";
                return;
            }
            statusText.Text = sender.Idle == null
                ? "Sending to " + sender.Targets + " tablet" + (sender.Targets == 1 ? "" : "s") + ", " + sender.MessagesSent + " messages so far"
                : "Not sending: " + sender.Idle;
            var last = sender.LastMessage;
            lastText.Text = last == null ? "" : "Last: " + (last.Length > 240 ? last.Substring(0, 240) + "…" : last);
        }
    }
}

// SPDX-License-Identifier: GPL-3.0-only
// MicSection.cs: the "Microphone" section of the rigPlay page (#34): the "Microphone to the phone" switch, the input
// device picker (friendly names, refresh), the boost (automatic switch and the dB slider it is capped at) and what
// the microphone is doing (which tablet, packets/s, level meter with the boost in effect, last event), refreshed
// every 250 ms while the page is visible. Built in code with the Ui helpers like the other sections.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace RigPlayPlugin.Audio
{
    public sealed class MicSection
    {
        private readonly RigPlay plugin;
        private readonly ComboBox devices = new ComboBox { MinWidth = 320, MaxWidth = 420 };
        private readonly TextBlock stateText = Ui.Text("");
        private readonly TextBlock levelText = Ui.Text("");
        private readonly TextBlock eventText = Ui.Text("");
        private readonly TextBlock boostText = Ui.Text("");
        private bool populating;

        public MicSection(RigPlay plugin)
        {
            this.plugin = plugin;
        }

        private RigPlaySettings Settings => plugin.Settings;

        public FrameworkElement Build()
        {
            var toggle = Ui.Toggle(Settings.MicEnabled, on =>
            {
                Settings.MicEnabled = on;
                plugin.SaveSettings();
                Log.Info("Microphone to the phone switched " + (on ? "on" : "off") + " from the settings page");
                plugin.Mic?.SettingsChanged();
                Refresh();
            });
            devices.SelectionChanged += OnDeviceSelected;
            var refresh = PageKit.SecondaryButton("Refresh", () =>
            {
                PopulateDevices();
                plugin.Mic?.SettingsChanged(); // a device may have appeared: state.mic.enabled follows
            });
            PopulateDevices();
            levelText.FontFamily = new System.Windows.Media.FontFamily("Consolas");

            var boost = new Slider
            {
                Minimum = MicGainControl.MinBoostDb,
                Maximum = MicGainControl.MaxBoostDb,
                Value = Settings.MicBoostDb,
                Width = 200,
                SmallChange = 1,
                LargeChange = 6,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
            };
            boostText.MinWidth = 150;
            UpdateBoostText();
            boost.ValueChanged += (s, e) =>
            {
                var v = (int)Math.Round(e.NewValue);
                if (v == Settings.MicBoostDb) return;
                Settings.MicBoostDb = v; // the sender reads it on every buffer: applies at once
                UpdateBoostText();
                plugin.SaveSettings();
            };
            var auto = Ui.Toggle(Settings.MicAutoBoost, on =>
            {
                Settings.MicAutoBoost = on;
                UpdateBoostText();
                plugin.SaveSettings();
                Log.Info("Microphone boost set to " + (on ? "automatic, up to " : "") + Settings.MicBoostDb + " dB from the settings page");
            });

            var section = Ui.Section("Microphone",
                "Sends this PC's microphone to the phone for Siri and calls, when the tablet's setting \"Microphone\" is \"PC via SimHub\". "
                + "There is no echo cancellation: use headphones or a headset, or callers may hear themselves through the PC speakers. "
                + "Automatic boost brings your voice to a level Siri hears well; the slider is the most it may add, or the fixed boost with Automatic off.",
                Ui.Row("Microphone to the phone", toggle),
                Ui.Row("Input device", Ui.HStack(8, devices, refresh)),
                Ui.Row("Automatic boost", auto),
                Ui.Row("Boost", Ui.HStack(12, boost, boostText)),
                Ui.Row("State", stateText),
                Ui.Row("Level", levelText),
                Ui.Row("Last event", eventText));
            PageKit.Live(section, MicSender.TickMs, Refresh);
            return section;
        }

        private void UpdateBoostText()
        {
            boostText.Text = Settings.MicAutoBoost ? "up to +" + Settings.MicBoostDb + " dB" : "+" + Settings.MicBoostDb + " dB fixed";
        }

        private void Refresh()
        {
            var mic = plugin.Mic;
            if (mic == null)
            {
                stateText.Text = "Not started (the tablet server is not running)";
                levelText.Text = "—";
                eventText.Text = "None";
                return;
            }
            var stats = mic.Stats;
            stateText.Text = stats.StateText + (stats.Enabled && !stats.Started && !mic.Available ? " · no input device found, tablets use their own microphone" : "");
            levelText.Text = stats.LevelText;
            eventText.Text = string.IsNullOrEmpty(stats.LastEvent) ? "None" : stats.LastEvent;
        }

        private void PopulateDevices()
        {
            populating = true;
            try
            {
                devices.Items.Clear();
                var list = ListDevicesSafe();
                string defaultName = null;
                foreach (var d in list)
                {
                    if (d.IsDefault) defaultName = d.Name;
                }
                devices.Items.Add(new ComboBoxItem { Content = defaultName == null ? "Windows default" : "Windows default (" + defaultName + ")", Tag = "" });
                var selected = 0;
                foreach (var d in list)
                {
                    devices.Items.Add(new ComboBoxItem { Content = d.Name, Tag = d.Id });
                    if (string.Equals(d.Id, Settings.MicDeviceId, StringComparison.OrdinalIgnoreCase)) selected = devices.Items.Count - 1;
                }
                if (selected == 0 && !string.IsNullOrEmpty(Settings.MicDeviceId))
                {
                    // The saved device is unplugged or disabled: keep it selected so it is used again when it returns.
                    devices.Items.Add(new ComboBoxItem { Content = "Saved device, not connected (Windows default is used)", Tag = Settings.MicDeviceId });
                    selected = devices.Items.Count - 1;
                }
                devices.ToolTip = list.Count == 0 ? "No recording device found on this PC" : null;
                devices.SelectedIndex = selected;
            }
            catch (Exception ex)
            {
                Log.Warn("Could not fill the recording device list: " + ex.Message);
            }
            finally
            {
                populating = false;
            }
        }

        private void OnDeviceSelected(object sender, SelectionChangedEventArgs e)
        {
            if (populating) return;
            var item = devices.SelectedItem as ComboBoxItem;
            if (item == null) return;
            var id = item.Tag as string ?? "";
            if (string.Equals(id, Settings.MicDeviceId, StringComparison.Ordinal)) return;
            Settings.MicDeviceId = id;
            plugin.SaveSettings();
            Log.Info("Microphone input device set to " + (id.Length == 0 ? "the Windows default" : "\"" + item.Content + "\"") + " from the settings page");
            PageKit.Safe(() => plugin.Mic?.SettingsChanged());
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static List<AudioDeviceInfo> ListDevicesSafe()
        {
            try
            {
                return MicCapture.ListDevices();
            }
            catch (Exception ex)
            {
                Log.Warn("Recording devices cannot be listed: " + ex.Message);
                return new List<AudioDeviceInfo>();
            }
        }
    }
}

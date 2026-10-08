// SPDX-License-Identifier: GPL-3.0-only
// DashboardSection.cs: the Dashboards section of the rigPlay page (spec §11): which installed dashboard the SimHub
// button in CarPlay shows, which one shows while no iPhone is connected, and whether SimHub's web dash server
// answers (with the setting to turn on when it does not). A change is saved and pushed to tablets at once.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RigPlayPlugin.Dashboards;

namespace RigPlayPlugin
{
    internal sealed class DashboardSection
    {
        private const string WebDashSetting = "Settings → Web dash server";

        private readonly RigPlay plugin;
        private readonly StackPanel serverValue = new StackPanel { Orientation = Orientation.Horizontal };
        private ComboBox selected;
        private ComboBox idle;
        private string serverKey;

        public DashboardSection(RigPlay plugin)
        {
            this.plugin = plugin;
        }

        private RigPlaySettings Settings => plugin.Settings;

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
            selected = new ComboBox { Width = 320 };
            idle = new ComboBox { Width = 320 };
            Fill();
            selected.SelectionChanged += (s, e) => PageKit.Safe(() => Changed(selected, "SimHub button", v => Settings.SelectedDashboard = v, () => Settings.SelectedDashboard));
            idle.SelectionChanged += (s, e) => PageKit.Safe(() => Changed(idle, "the idle screen (no phone connected)", v => Settings.IdleDashboard = v, () => Settings.IdleDashboard));

            var refresh = PageKit.SecondaryButton("Refresh list", () =>
            {
                plugin.Host?.RefreshDashboards();
                Fill();
            });

            var port = PageKit.CommitTextBox(Settings.WebDashPort > 0 ? Settings.WebDashPort.ToString() : "", 90, box =>
            {
                var text = (box.Text ?? "").Trim();
                int value;
                if (text.Length == 0) value = 0;
                else if (!int.TryParse(text, out value) || value < 1 || value > ProtocolDefaults.MaxPort)
                {
                    box.Text = Settings.WebDashPort > 0 ? Settings.WebDashPort.ToString() : "";
                    return;
                }
                if (value == Settings.WebDashPort) return;
                Settings.WebDashPort = value;
                Log.Info("Web dash server port set to " + (value == 0 ? "automatic" : value.ToString()));
                plugin.Host?.DashboardSettingsChanged();
            });
            port.ToolTip = "Leave empty to use SimHub's own setting (8888 unless changed in SimHub).";

            var section = Ui.Section("Dashboards",
                "The SimHub dashboards the tablet shows: one behind the SimHub button in CarPlay, one while no iPhone is connected. Tablets load them from SimHub's web dash server on this PC.",
                Ui.Row("Dashboard shown by the SimHub button", Ui.HStack(10, selected, refresh)),
                Ui.Row("Idle dashboard (no phone connected)", idle),
                Ui.Row("Web dash server", serverValue),
                Ui.Row("Web dash server port", Ui.HStack(10, port, Ui.Caption("empty: automatic"))));
            PageKit.Live(section, 1000, Refresh);
            return section;
        }

        private void Fill()
        {
            var host = plugin.Host;
            var dashboards = host?.Dashboards ?? new List<DashboardInfo>();
            FillOne(selected, dashboards, "(None)", Settings.SelectedDashboard);
            FillOne(idle, dashboards, "(None: the tablet's home screen)", Settings.IdleDashboard);
        }

        private static void FillOne(ComboBox combo, List<DashboardInfo> dashboards, string none, string current)
        {
            var items = new List<Choice> { new Choice { Name = "", Label = none } };
            items.AddRange(dashboards.Select(d => new Choice { Name = d.Name, Label = d.Display }));
            // A stored choice that is no longer installed stays visible, so it is not silently dropped.
            if (!string.IsNullOrEmpty(current) && items.All(i => i.Name != current))
                items.Add(new Choice { Name = current, Label = current + " (not installed)" });
            combo.Tag = "filling";
            combo.ItemsSource = items;
            combo.SelectedItem = items.First(i => i.Name == (current ?? ""));
            combo.Tag = null;
        }

        private void Changed(ComboBox combo, string what, Action<string> set, Func<string> get)
        {
            if (combo.Tag != null) return;
            var choice = combo.SelectedItem as Choice;
            if (choice == null || choice.Name == get()) return;
            set(choice.Name);
            Log.Info("Dashboard for " + what + " set to '" + choice.Name + "'");
            plugin.Host?.DashboardSettingsChanged();
        }

        private void Refresh()
        {
            var host = plugin.Host;
            var probe = host?.Probe.Current;
            var port = host?.EffectiveWebDashPort ?? ProtocolDefaults.WebDashPort;
            var key = probe == null ? "unknown|" + port : probe.Reachable + "|" + probe.Port;
            if (key == serverKey) return;
            serverKey = key;
            serverValue.Children.Clear();
            string color, text;
            if (host == null) { color = Theme.StatusIdle; text = "Not running"; }
            else if (probe == null) { color = Theme.StatusIdle; text = "Checking port " + port + "…"; }
            else if (probe.Reachable) { color = Theme.StatusOk; text = "Reachable on port " + probe.Port; }
            else { color = Theme.StatusWarn; text = "Not reachable on port " + probe.Port + ". In SimHub, turn on " + WebDashSetting + "."; }
            var dot = Ui.Dot(color);
            dot.Margin = new Thickness(0, 0, 8, 0);
            serverValue.Children.Add(dot);
            var block = Ui.Text(text);
            block.MaxWidth = 520;
            serverValue.Children.Add(block);
        }
    }
}

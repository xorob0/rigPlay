// SPDX-License-Identifier: GPL-3.0-only
// StatusSection.cs: the Status section of the rigPlay page: whether the control server listens and the beacon
// runs (with the reason when not, spec §2 and §14.2 "local failures"), the PC name and control port tablets see,
// and the connected tablets. Refreshes itself twice a second while on screen.
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RigPlayPlugin.Net;

namespace RigPlayPlugin
{
    internal sealed class StatusSection
    {
        private readonly RigPlay plugin;
        private readonly StackPanel serverValue = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly StackPanel beaconValue = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly StackPanel tablets = new StackPanel();
        private string serverKey;
        private string beaconKey;
        private string tabletsKey;

        public StatusSection(RigPlay plugin)
        {
            this.plugin = plugin;
        }

        private RigPlaySettings Settings => plugin.Settings;

        public FrameworkElement Build()
        {
            var name = PageKit.CommitTextBox(Settings.HostName, 220, box =>
            {
                var value = (box.Text ?? "").Trim();
                if (value == Settings.HostName) return;
                Settings.HostName = value;
                plugin.SaveSettings();
                Log.Info("PC name for tablets set to '" + (value.Length == 0 ? Environment.MachineName + "' (computer name)" : value + "'"));
            });
            name.ToolTip = "Leave empty to use the computer name (" + Environment.MachineName + ").";

            var port = PageKit.CommitTextBox(Settings.ControlPort.ToString(), 90, box =>
            {
                int value;
                if (!int.TryParse(box.Text, out value) || value < ProtocolDefaults.MinPort || value > ProtocolDefaults.MaxPort
                    || value == ProtocolDefaults.DiscoveryPort || value == Settings.AudioPort)
                {
                    box.Text = Settings.ControlPort.ToString();
                    return;
                }
                if (value == Settings.ControlPort) return;
                Settings.ControlPort = value;
                plugin.SaveSettings();
                Log.Info("Control port changed to " + value + "; restarting the control server");
                plugin.Host?.RestartServer();
            });

            var section = Ui.Section("Status",
                "Whether rigPlay is listening for tablets, and on which ports. SimHub's installer already allows SimHub through Windows Firewall; if another firewall asks, allow it on private networks only.",
                Ui.Row("Tablet server", serverValue),
                Ui.Row("Discovery", beaconValue),
                Ui.Row("PC name on tablets", name),
                Ui.Row("Control port (TCP)", port),
                Ui.Row("Discovery port (UDP)", ProtocolDefaults.DiscoveryPort + " (fixed)"),
                Ui.Row("Connected tablets", tablets));
            PageKit.Live(section, 500, Refresh);
            return section;
        }

        private void Refresh()
        {
            var host = plugin.Host;
            var server = host?.Server?.Status;
            var key = server == null ? "none" : server.Listening + "|" + server.Port + "|" + server.Error;
            if (key != serverKey)
            {
                serverKey = key;
                serverValue.Children.Clear();
                if (server == null) Fill(serverValue, Theme.StatusIdle, "Not running");
                else if (server.Listening) Fill(serverValue, Theme.StatusOk, "Listening on TCP " + server.Port);
                else Fill(serverValue, Theme.StatusWarn, server.Error ?? "Stopped");
            }

            var beacon = host?.Beacon?.Status;
            key = beacon == null ? "none" : beacon.Running + "|" + beacon.Error + "|" + string.Join(",", beacon.Targets.Select(t => t.Address));
            if (key != beaconKey)
            {
                beaconKey = key;
                beaconValue.Children.Clear();
                if (beacon == null || !beacon.Running) Fill(beaconValue, beacon?.Error == null ? Theme.StatusIdle : Theme.StatusWarn, beacon?.Error ?? "Not broadcasting");
                else if (beacon.Error != null) Fill(beaconValue, Theme.StatusWarn, beacon.Error);
                else Fill(beaconValue, Theme.StatusOk, "Broadcasting every second to " + string.Join(", ", beacon.Targets.Select(t => t.Address.ToString()).Distinct()));
            }

            var sessions = host?.Server?.Sessions ?? new System.Collections.Generic.List<ClientSession>();
            var primary = host?.PrimarySessionId ?? 0;
            key = string.Join(";", sessions.Select(s => s.Id + "|" + s.State + "|" + s.TabletName + "|" + (s.LastStatus?.PhoneConnected ?? false) + "|" + (s.Id == primary)));
            if (key != tabletsKey)
            {
                tabletsKey = key;
                tablets.Children.Clear();
                if (sessions.Count == 0) tablets.Children.Add(Ui.Text("None"));
                foreach (var s in sessions) tablets.Children.Add(Ui.Text(Describe(s, s.Id == primary)));
            }
        }

        private static string Describe(ClientSession s, bool primary)
        {
            var name = s.TabletName ?? "Unknown tablet";
            string state;
            switch (s.State)
            {
                case SessionState.AwaitingHello: state = "connecting"; break;
                case SessionState.Unpaired: state = "not paired"; break;
                case SessionState.Paired:
                    state = s.LastStatus != null && s.LastStatus.PhoneConnected
                        ? "paired, iPhone connected" + (string.IsNullOrEmpty(s.LastStatus.PhoneName) ? "" : " (" + s.LastStatus.PhoneName + ")")
                        : "paired";
                    break;
                default: state = "closed"; break;
            }
            return name + " · " + s.Remote.Address + " · " + state + (primary ? " · primary" : "");
        }

        private static void Fill(StackPanel panel, string color, string text)
        {
            var dot = Ui.Dot(color);
            dot.Margin = new Thickness(0, 0, 8, 0);
            panel.Children.Add(dot);
            var block = Ui.Text(text);
            block.MaxWidth = 560;
            panel.Children.Add(block);
        }
    }
}

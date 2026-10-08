// SPDX-License-Identifier: GPL-3.0-only
// PairingSection.cs: the Pairing section of the rigPlay page (spec §8). While a tablet asks to pair it shows
// "Tablet <name> wants to connect" with the PIN in large digits, a countdown and a Deny button; below, the paired
// tablets with a Forget button each. Forgetting closes the tablet's live session.
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RigPlayPlugin.Net;
using RigPlayPlugin.Pairing;

namespace RigPlayPlugin
{
    internal sealed class PairingSection
    {
        private readonly RigPlay plugin;
        private readonly StackPanel requests = new StackPanel();
        private readonly StackPanel paired = new StackPanel();
        private string requestsKey;
        private string pairedKey;

        public PairingSection(RigPlay plugin)
        {
            this.plugin = plugin;
        }

        public FrameworkElement Build()
        {
            var section = Ui.Section("Pairing",
                "Pair a tablet once: on the tablet, pick this PC and tap Pair, then type the PIN shown here. After that it connects on its own.",
                requests,
                Ui.Row("Paired tablets", paired));
            PageKit.Live(section, 250, Refresh);
            return section;
        }

        private void Refresh()
        {
            var host = plugin.Host;
            var pairing = host?.Pairing;

            var pending = pairing?.Pending ?? new System.Collections.Generic.List<PendingPairing>();
            var key = string.Join(";", pending.Select(p => p.TabletId + "|" + p.Pin + "|" + p.TabletName));
            if (key != requestsKey)
            {
                requestsKey = key;
                requests.Children.Clear();
                if (pending.Count == 0) requests.Children.Add(Ui.Caption("No tablet is asking to pair right now."));
                foreach (var p in pending) requests.Children.Add(BuildRequest(p));
            }
            // Countdowns tick without rebuilding the buttons.
            foreach (var block in requests.Children.OfType<FrameworkElement>().Select(e => e.Tag).OfType<Countdown>())
            {
                block.Text.Text = "Expires in " + FormatSeconds(pairing == null ? 0 : pairing.SecondsLeft(block.Pending));
            }

            var tablets = pairing?.PairedTablets ?? new System.Collections.Generic.List<PairedTablet>();
            var connected = host?.Server?.PairedSessions.Select(s => s.TabletId).ToList() ?? new System.Collections.Generic.List<string>();
            key = string.Join(";", tablets.Select(t => t.Id + "|" + t.Name + "|" + t.PairedAt.Ticks + "|" + connected.Contains(t.Id)));
            if (key != pairedKey)
            {
                pairedKey = key;
                paired.Children.Clear();
                if (tablets.Count == 0) paired.Children.Add(Ui.Text("None yet"));
                foreach (var t in tablets) paired.Children.Add(BuildPaired(t, connected.Contains(t.Id)));
            }
        }

        private sealed class Countdown
        {
            public PendingPairing Pending;
            public TextBlock Text;
        }

        private FrameworkElement BuildRequest(PendingPairing p)
        {
            var title = Ui.Text("Tablet " + p.TabletName + " wants to connect", Theme.SizeSection, FontWeights.SemiBold);
            var pin = new TextBlock
            {
                Text = p.Pin.Substring(0, 3) + " " + p.Pin.Substring(3),
                FontSize = 44,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Consolas, Courier New"),
                Foreground = Ui.Brush(Theme.Accent),
                Margin = new Thickness(0, 4, 0, 4),
            };
            var countdown = Ui.Caption("");
            var tabletId = p.TabletId;
            var deny = PageKit.SecondaryButton("Deny", () => plugin.Host?.DenyPairing(tabletId));
            var panel = Ui.VStack(6,
                title,
                Ui.Caption("Type this PIN on the tablet:"),
                pin,
                Ui.HStack(16, countdown, deny));
            panel.Margin = new Thickness(0, 4, 0, 8);
            panel.Tag = new Countdown { Pending = p, Text = countdown };
            return panel;
        }

        private FrameworkElement BuildPaired(PairedTablet t, bool connected)
        {
            var id = t.Id;
            var name = t.Name;
            var forget = PageKit.SecondaryButton("Forget", () =>
            {
                var answer = MessageBox.Show("Forget " + name + "? It will have to pair again with a new PIN.", "rigPlay",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.OK) plugin.Host?.ForgetTablet(id);
            });
            var dot = Ui.Dot(connected ? Theme.StatusOk : Theme.StatusIdle);
            var label = Ui.Text(name + " · paired " + t.PairedAt.ToLocalTime().ToString("g") + (connected ? " · connected" : ""));
            var row = Ui.HStack(10, dot, label, forget);
            row.Margin = new Thickness(0, 0, 0, 6);
            return row;
        }

        private static string FormatSeconds(int seconds)
        {
            return (seconds / 60) + ":" + (seconds % 60).ToString("00");
        }
    }
}

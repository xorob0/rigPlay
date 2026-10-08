// SPDX-License-Identifier: GPL-3.0-only
// PageKit.cs: building blocks for the live sections of the rigPlay page (status, pairing, dashboards), on top of
// Widgets.cs: a secondary button, a text box that commits on Enter or focus loss, and a refresh timer that runs only
// while the section is on screen.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SimHub.Plugins.Styles;

namespace RigPlayPlugin
{
    internal static class PageKit
    {
        /// <summary>SimHub's secondary button, or a plain button.</summary>
        public static Button SecondaryButton(string text, Action click)
        {
            Button button;
            try
            {
                button = new SHButtonSecondary();
            }
            catch (Exception)
            {
                button = new Button();
                Ui.TryStyle(button, typeof(SHButtonSecondary));
            }
            button.Content = text;
            button.HorizontalAlignment = HorizontalAlignment.Left;
            button.VerticalAlignment = VerticalAlignment.Center;
            button.Click += (s, e) =>
            {
                try { click(); } catch (Exception ex) { Log.Error("Button '" + text + "' failed", ex); }
            };
            return button;
        }

        /// <summary>A text box that calls <paramref name="commit"/> on Enter and when it loses focus.</summary>
        public static TextBox CommitTextBox(string value, double width, Action<TextBox> commit)
        {
            var box = new TextBox { Text = value ?? "", Width = width, VerticalAlignment = VerticalAlignment.Center };
            box.LostFocus += (s, e) => Safe(() => commit(box));
            box.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) Safe(() => commit(box));
            };
            return box;
        }

        /// <summary>Calls <paramref name="refresh"/> now and every <paramref name="ms"/> ms while the element is loaded.</summary>
        public static void Live(FrameworkElement element, int ms, Action refresh)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += (s, e) => Safe(refresh);
            element.Loaded += (s, e) =>
            {
                Safe(refresh);
                timer.Start();
            };
            element.Unloaded += (s, e) => timer.Stop();
            Safe(refresh);
        }

        public static void Safe(Action action)
        {
            try { action(); } catch (Exception ex) { Log.Error("The rigPlay page failed to update", ex); }
        }
    }
}

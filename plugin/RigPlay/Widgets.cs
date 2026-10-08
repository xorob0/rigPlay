// SPDX-License-Identifier: GPL-3.0-only
// Widgets.cs: small factories for the page's building blocks. Each one prefers SimHub's own control or style
// (SHSection, SHSectionTitle, SHButtonPrimary, SHToggleButton, found at runtime) so the page looks native, and
// falls back to a plain WPF control when SimHub's is unavailable, so the page never fails to build.
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using SimHub.Plugins.Styles;

namespace RigPlayPlugin
{
    internal static class Ui
    {
        private static readonly Dictionary<string, SolidColorBrush> Brushes = new Dictionary<string, SolidColorBrush>();

        /// <summary>A frozen brush for a #RRGGBB or #AARRGGBB value, cached per colour.</summary>
        public static SolidColorBrush Brush(string hex)
        {
            lock (Brushes)
            {
                SolidColorBrush brush;
                if (Brushes.TryGetValue(hex, out brush)) return brush;
                brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                brush.Freeze();
                Brushes[hex] = brush;
                return brush;
            }
        }

        // Text

        public static TextBlock Text(string text, double size = Theme.SizeBody, FontWeight? weight = null)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = size,
                FontWeight = weight ?? FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        /// <summary>A one-line explanation under a title: the inherited colour, dimmed.</summary>
        public static TextBlock Caption(string text)
        {
            var block = Text(text);
            block.Opacity = Theme.CaptionOpacity;
            return block;
        }

        // Layout

        public static StackPanel VStack(double gap, params UIElement[] children)
        {
            var panel = new StackPanel { Orientation = Orientation.Vertical };
            for (var i = 0; i < children.Length; i++)
            {
                var element = children[i] as FrameworkElement;
                if (element != null && i < children.Length - 1)
                {
                    var m = element.Margin;
                    element.Margin = new Thickness(m.Left, m.Top, m.Right, m.Bottom + gap);
                }
                panel.Children.Add(children[i]);
            }
            return panel;
        }

        public static StackPanel HStack(double gap, params UIElement[] children)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            for (var i = 0; i < children.Length; i++)
            {
                var element = children[i] as FrameworkElement;
                if (element != null && i < children.Length - 1)
                {
                    var m = element.Margin;
                    element.Margin = new Thickness(m.Left, m.Top, m.Right + gap, m.Bottom);
                }
                panel.Children.Add(children[i]);
            }
            return panel;
        }

        /// <summary>A label on the left and a value or control on the right.</summary>
        /// <remarks>The value column is Auto, not Star: SHToggleButton scales its template to the space it is
        /// given, so in a Star column it grows to the width of the page.</remarks>
        public static Grid Row(string label, FrameworkElement value)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var left = Text(label);
            Grid.SetColumn(left, 0);
            value.VerticalAlignment = VerticalAlignment.Center;
            value.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetColumn(value, 1);
            grid.Children.Add(left);
            grid.Children.Add(value);
            return grid;
        }

        public static Grid Row(string label, string value)
        {
            return Row(label, Text(value));
        }

        /// <summary>
        /// A titled section: SimHub's SHSection when it can be created, otherwise a section title (SHSectionTitle,
        /// or a bold TextBlock) over the content with a rule on top. The explanation is the first line inside.
        /// </summary>
        public static FrameworkElement Section(string title, string explanation, params UIElement[] rows)
        {
            var children = new List<UIElement> { Caption(explanation) };
            children.AddRange(rows);
            var body = VStack(10, children.ToArray());

            try
            {
                return new SHSection { Title = title, Content = body, Margin = new Thickness(0, 0, 0, 8) };
            }
            catch (Exception ex)
            {
                Log.Warn("SHSection is unavailable; using a plain section: " + ex.Message);
            }

            TextBlock heading;
            try
            {
                heading = new SHSectionTitle { Text = title };
            }
            catch (Exception)
            {
                heading = Text(title, Theme.SizeSection, FontWeights.SemiBold);
            }
            heading.Margin = new Thickness(0, 0, 0, 8);
            return new Border
            {
                BorderBrush = Brush(Theme.Rule),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 16, 0, 16),
                Child = VStack(0, heading, body),
            };
        }

        // Controls

        /// <summary>SimHub's primary button; a plain button carrying SimHub's style, if any, otherwise.</summary>
        public static Button PrimaryButton(string text, RoutedEventHandler click = null)
        {
            Button button;
            try
            {
                button = new SHButtonPrimary();
            }
            catch (Exception ex)
            {
                Log.Warn("SHButtonPrimary is unavailable; using a plain button: " + ex.Message);
                button = new Button();
                TryStyle(button, typeof(SHButtonPrimary));
            }
            button.Content = text;
            button.HorizontalAlignment = HorizontalAlignment.Left;
            if (click != null) button.Click += click;
            return button;
        }

        /// <summary>SimHub's switch, so it looks like every other toggle in SimHub.</summary>
        public static ToggleButton Toggle(bool isOn, Action<bool> changed)
        {
            ToggleButton toggle;
            try
            {
                toggle = new SHToggleButton();
            }
            catch (Exception ex)
            {
                Log.Warn("SHToggleButton is unavailable; using a plain toggle: " + ex.Message);
                toggle = new ToggleButton();
                TryStyle(toggle, typeof(SHToggleButton));
            }
            toggle.IsChecked = isOn;
            toggle.HorizontalAlignment = HorizontalAlignment.Left;
            toggle.Checked += (sender, args) => changed(true);
            toggle.Unchecked += (sender, args) => changed(false);
            return toggle;
        }

        /// <summary>The 8 px round status dot.</summary>
        public static Ellipse Dot(string hex)
        {
            return new Ellipse { Width = 8, Height = 8, Fill = Brush(hex), VerticalAlignment = VerticalAlignment.Center };
        }

        // SimHub integration

        /// <summary>Applies a style from SimHub's application resources when it exists; false otherwise.</summary>
        public static bool TryStyle(FrameworkElement element, object key)
        {
            try
            {
                var style = Application.Current?.TryFindResource(key) as Style;
                if (style == null) return false;
                element.Style = style;
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("Style " + key + " could not be applied: " + ex.Message);
                return false;
            }
        }
    }
}

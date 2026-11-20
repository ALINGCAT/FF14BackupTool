using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FF14BackupTool.Dialogs
{
    public static class SimpleDialogs
    {
        public static string Prompt(Window owner, string title, string label, string initialValue)
        {
            var window = CreateWindow(owner, title, 460, 190);
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock
            {
                Text = label,
                Margin = new Thickness(0, 0, 0, 8),
                Foreground = new SolidColorBrush(Color.FromRgb(55, 65, 81))
            });
            var textBox = new TextBox
            {
                Text = initialValue ?? string.Empty,
                Height = 32,
                Padding = new Thickness(8, 5, 8, 5),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(textBox);

            string result = null;
            var buttons = CreateButtonRow(window, () => { result = textBox.Text; }, () => { });
            panel.Children.Add(buttons);
            window.Content = panel;
            window.Loaded += (s, e) => { textBox.Focus(); textBox.SelectAll(); };
            window.ShowDialog();
            return result;
        }

        public static bool PromptTwo(Window owner, string title, string label1, string initial1,
            string label2, string initial2, out string value1, out string value2)
        {
            string result1 = null;
            string result2 = null;
            var window = CreateWindow(owner, title, 500, 260);
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock
            {
                Text = label1,
                Margin = new Thickness(0, 0, 0, 6),
                Foreground = new SolidColorBrush(Color.FromRgb(55, 65, 81))
            });
            var box1 = new TextBox
            {
                Text = initial1 ?? string.Empty,
                Height = 32,
                Padding = new Thickness(8, 5, 8, 5),
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12)
            };
            panel.Children.Add(box1);
            panel.Children.Add(new TextBlock
            {
                Text = label2,
                Margin = new Thickness(0, 0, 0, 6),
                Foreground = new SolidColorBrush(Color.FromRgb(55, 65, 81))
            });
            var box2 = new TextBox
            {
                Text = initial2 ?? string.Empty,
                Height = 32,
                Padding = new Thickness(8, 5, 8, 5),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(box2);

            bool confirmed = false;
            var buttons = CreateButtonRow(window, () =>
            {
                result1 = box1.Text;
                result2 = box2.Text;
                confirmed = true;
            }, () => { });
            panel.Children.Add(buttons);
            window.Content = panel;
            window.Loaded += (s, e) => { box1.Focus(); };
            window.ShowDialog();
            value1 = result1;
            value2 = result2;
            return confirmed;
        }

        public static T Choose<T>(Window owner, string title, string message, IEnumerable<T> choices) where T : class
        {
            var list = choices == null ? new List<T>() : choices.ToList();
            if (list.Count == 0) return null;
            if (list.Count == 1) return list[0];

            var window = CreateWindow(owner, title, 560, 330);
            var panel = new DockPanel { Margin = new Thickness(18) };
            var text = new TextBlock
            {
                Text = message,
                Margin = new Thickness(0, 0, 0, 8),
                Foreground = new SolidColorBrush(Color.FromRgb(55, 65, 81))
            };
            DockPanel.SetDock(text, Dock.Top);
            panel.Children.Add(text);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };
            DockPanel.SetDock(buttons, Dock.Bottom);
            panel.Children.Add(buttons);

            var listBox = new ListBox { Margin = new Thickness(0, 0, 0, 10) };
            foreach (var item in list) listBox.Items.Add(item);
            listBox.SelectedIndex = 0;
            panel.Children.Add(listBox);

            T selected = null;
            var cancel = new Button
            {
                Content = "取消",
                Width = 88,
                Height = 32,
                Background = new SolidColorBrush(Color.FromRgb(235, 239, 245)),
                Foreground = new SolidColorBrush(Color.FromRgb(55, 65, 81)),
                BorderThickness = new Thickness(0)
            };
            var ok = new Button
            {
                Content = "确定",
                Width = 88,
                Height = 32,
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(47, 111, 237)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            ok.Click += (s, e) =>
            {
                selected = listBox.SelectedItem as T;
                window.DialogResult = true;
            };
            cancel.Click += (s, e) => { window.DialogResult = false; };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            window.Content = panel;
            window.ShowDialog();
            return selected;
        }

        private static Window CreateWindow(Window owner, string title, double width, double height)
        {
            return new Window
            {
                Owner = owner,
                Title = title,
                Width = width,
                Height = height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.FromRgb(246, 248, 251)),
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                ShowInTaskbar = false
            };
        }

        private static StackPanel CreateButtonRow(Window window, Action onOk, Action onCancel)
        {
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };
            var ok = new Button
            {
                Content = "确定",
                Width = 88,
                Height = 32,
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(47, 111, 237)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            var cancel = new Button
            {
                Content = "取消",
                Width = 88,
                Height = 32,
                Background = new SolidColorBrush(Color.FromRgb(235, 239, 245)),
                Foreground = new SolidColorBrush(Color.FromRgb(55, 65, 81)),
                BorderThickness = new Thickness(0)
            };
            ok.Click += (s, e) =>
            {
                onOk();
                window.DialogResult = true;
            };
            cancel.Click += (s, e) =>
            {
                onCancel();
                window.DialogResult = false;
            };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            return buttons;
        }
    }
}

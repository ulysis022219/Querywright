using System.Collections.Generic;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Querywright.Core;

namespace Querywright.Ssms
{
    internal sealed class CompletionPicker : Window
    {
        private readonly ListBox list;
        internal CompletionItem Selected => list.SelectedItem as CompletionItem;

        internal CompletionPicker(IReadOnlyList<CompletionItem> items)
        {
            Title = "Querywright: offline schema suggestions";
            DialogParts.Style(this);
            Width = 560; Height = 480; MinWidth = 400; MinHeight = 380;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var panel = new DockPanel { Margin = new Thickness(12) };
            panel.Children.Add(DialogParts.Header("Schema suggestions", "Find a table or column in your configured schema snapshot."));
            var top = new StackPanel();
            var search = new TextBox { Margin = new Thickness(0, 4, 0, 8) };
            top.Children.Add(new Label { Content = "_Search schema suggestions:", Target = search });
            top.Children.Add(search);
            DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
            var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var accept = new Button { Content = "_Insert", IsDefault = true, MinWidth = 80, Margin = new Thickness(4) };
            accept.ToolTip = "Insert the selected suggestion (Enter).";
            var cancel = new Button { Content = "_Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(4) };
            accept.Click += (sender, args) => { if (Selected != null) DialogResult = true; };
            buttons.Children.Add(accept); buttons.Children.Add(cancel);
            panel.Children.Add(DialogParts.Footer(buttons));
            list = new ListBox { ItemsSource = items, DisplayMemberPath = "Name", SelectedIndex = 0 };
            var empty = new TextBlock { Text = items.Count == 0 ? "No schema suggestions. Configure a schema snapshot in Querywright options." : "No matches. Try a shorter table or column name.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
            void Filter()
            {
                var matches = items.Where(item => item.Name.IndexOf(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                list.ItemsSource = matches; list.SelectedIndex = matches.Count > 0 ? 0 : -1;
                accept.IsEnabled = matches.Count > 0;
                empty.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            search.TextChanged += (sender, args) => Filter();
            search.PreviewKeyDown += (sender, args) => { if (args.Key == Key.Down && Selected != null) { Keyboard.Focus(list); args.Handled = true; } };
            System.Windows.Automation.AutomationProperties.SetName(list, "SQL suggestions");
            list.MouseDoubleClick += (sender, args) => { if (Selected != null) DialogResult = true; };
            var body = new Grid(); body.Children.Add(list); body.Children.Add(empty);
            panel.Children.Add(body); Content = panel;
            Filter();
            Loaded += (sender, args) => Keyboard.Focus(search);
        }
    }
}

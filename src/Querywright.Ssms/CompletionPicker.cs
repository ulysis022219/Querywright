using System.Collections.Generic;
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
            Width = 520; Height = 380; MinWidth = 300; MinHeight = 220;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var panel = new DockPanel { Margin = new Thickness(12) };
            var label = new Label { Content = "Select column or table from configured schema snapshot:" };
            DockPanel.SetDock(label, Dock.Top); panel.Children.Add(label);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var accept = new Button { Content = "_Insert", IsDefault = true, MinWidth = 80, Margin = new Thickness(4) };
            var cancel = new Button { Content = "_Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(4) };
            accept.Click += (sender, args) => { if (Selected != null) DialogResult = true; };
            buttons.Children.Add(accept); buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
            list = new ListBox { ItemsSource = items, DisplayMemberPath = "Name", SelectedIndex = 0 };
            System.Windows.Automation.AutomationProperties.SetName(list, "SQL suggestions");
            list.MouseDoubleClick += (sender, args) => { if (Selected != null) DialogResult = true; };
            panel.Children.Add(list); Content = panel;
            Loaded += (sender, args) => Keyboard.Focus(list);
        }
    }
}

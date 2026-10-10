using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Querywright.Ssms
{
    internal sealed class AiReplaceDialog : Window
    {
        internal AiReplaceDialog(string oldText, string newText)
        {
            Title = "Querywright: review AI replacement";
            DialogParts.Style(this);
            Width = 900; Height = 600; MinWidth = 480; MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var root = new DockPanel { Margin = new Thickness(12) };
            root.Children.Add(DialogParts.Header("Review replacement", "Current text on the left; AI replacement on the right. Apply replaces only this selection or script. Ctrl+Z undoes it."));
            root.Children.Add(DialogParts.Footer(DialogParts.Buttons(this,
                ("_Apply", false, false, () => DialogResult = true), ("_Cancel", true, true, null))));
            var panes = new Grid();
            panes.ColumnDefinitions.Add(new ColumnDefinition()); panes.ColumnDefinitions.Add(new ColumnDefinition());
            void Pane(string title, string text, int column)
            {
                var pane = new DockPanel { Margin = new Thickness(column == 0 ? 0 : 6, 0, column == 0 ? 6 : 0, 0) };
                var label = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
                DockPanel.SetDock(label, Dock.Top); pane.Children.Add(label);
                var box = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Consolas, Courier New"), FontSize = 14,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
                AutomationProperties.SetName(box, title);
                pane.Children.Add(box); Grid.SetColumn(pane, column); panes.Children.Add(pane);
            }
            // ponytail: side-by-side text, use SSMS diff service if line-level change highlighting is needed.
            Pane("Current text", oldText, 0); Pane("AI replacement", newText, 1);
            root.Children.Add(panes); Content = root;
        }
    }
}

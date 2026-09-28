using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Querywright.Core;

namespace Querywright.Ssms
{
    internal sealed class RenameVariableDialog : Window
    {
        internal RenameResult Result { get; private set; }

        internal RenameVariableDialog(string sql, int position)
        {
            Title = "Querywright: rename local variable";
            Width = 1000; Height = 650; MinWidth = 600; MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var root = new DockPanel { Margin = new Thickness(12) };
            var controls = new StackPanel();
            var name = new TextBox { Text = "@newName", Margin = new Thickness(0, 4, 0, 8) };
            controls.Children.Add(new Label { Content = "_New variable name:", Target = name });
            controls.Children.Add(name);
            var status = new TextBlock { Text = "Preview changes before applying. SQL will not be executed.", TextWrapping = TextWrapping.Wrap };
            controls.Children.Add(status);
            DockPanel.SetDock(controls, Dock.Top); root.Children.Add(controls);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var preview = new Button { Content = "_Preview", MinWidth = 90, Margin = new Thickness(4), IsDefault = true };
            var apply = new Button { Content = "_Apply", MinWidth = 90, Margin = new Thickness(4), IsEnabled = false };
            var cancel = new Button { Content = "_Cancel", MinWidth = 90, Margin = new Thickness(4), IsCancel = true };
            buttons.Children.Add(preview); buttons.Children.Add(apply); buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
            var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
            TextBox Pane(string title, string text, int column)
            {
                var panel = new DockPanel { Margin = new Thickness(4) };
                var label = new Label { Content = title };
                DockPanel.SetDock(label, Dock.Top); panel.Children.Add(label);
                var box = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    FontFamily = new System.Windows.Media.FontFamily("Consolas") };
                System.Windows.Automation.AutomationProperties.SetName(box, title);
                panel.Children.Add(box); Grid.SetColumn(panel, column); grid.Children.Add(panel);
                return box;
            }
            Pane("Original SQL", sql, 0);
            var proposed = Pane("Proposed SQL", "", 1);
            name.TextChanged += (sender, args) => { Result = null; apply.IsEnabled = false; proposed.Clear(); };
            async Task PreviewAsync()
            {
                string requested = name.Text;
                preview.IsEnabled = false; apply.IsEnabled = false; Result = null;
                try
                {
                    var result = await Task.Run(() => SqlRefactoring.RenameLocalVariable(sql, position, requested));
                    if (!IsLoaded || requested != name.Text) return;
                    Result = result; proposed.Text = result.Text;
                    status.Text = $"{result.Changes} references: {result.OldName} to {requested}. Review both panes, then Apply.";
                    apply.IsEnabled = result.Text != sql;
                }
                catch (Exception error) when (!(error is OutOfMemoryException))
                {
                    if (IsLoaded) { proposed.Clear(); status.Text = error.Message; }
                }
                finally { if (IsLoaded) preview.IsEnabled = true; }
            }
            preview.Click += (sender, args) => { _ = PreviewAsync(); };
            apply.Click += (sender, args) => { if (Result != null) DialogResult = true; };
            root.Children.Add(grid); Content = root;
            Loaded += (sender, args) => { Keyboard.Focus(name); name.SelectAll(); };
        }
    }
}

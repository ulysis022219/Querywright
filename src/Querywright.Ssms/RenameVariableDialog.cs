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

        /// <summary>Preview-then-apply rename; <paramref name="rename"/> runs off the UI thread with the requested name.</summary>
        internal RenameVariableDialog(string sql, string what, string initialName, Func<string, RenameResult> rename)
        {
            Title = "Querywright: rename " + what;
            DialogParts.Style(this);
            Width = 1000; Height = 700; MinWidth = 640; MinHeight = 500;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var root = new DockPanel { Margin = new Thickness(12) };
            root.Children.Add(DialogParts.Header("Rename " + what, "Compare changes before applying them. SQL will not be executed."));
            var controls = new StackPanel();
            var name = new TextBox { Text = initialName, Margin = new Thickness(0, 4, 0, 8) };
            controls.Children.Add(new Label { Content = "_New " + what + " name:", Target = name });
            controls.Children.Add(name);
            var status = DialogParts.Status();
            status.Text = "Enter a new name, then preview changes.";
            controls.Children.Add(status);
            var progress = new ProgressBar { IsIndeterminate = true, Height = 3, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
            System.Windows.Automation.AutomationProperties.SetName(progress, "Preparing rename preview");
            controls.Children.Add(progress);
            DockPanel.SetDock(controls, Dock.Top); root.Children.Add(controls);
            var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var preview = new Button { Content = "_Preview", MinWidth = 90, Margin = new Thickness(4), IsDefault = true };
            var apply = new Button { Content = "_Apply", MinWidth = 90, Margin = new Thickness(4), IsEnabled = false };
            apply.ToolTip = "Apply only the changes shown in Proposed SQL.";
            preview.ToolTip = "Check the new name and generate a preview (Enter).";
            var cancel = new Button { Content = "_Cancel", MinWidth = 90, Margin = new Thickness(4), IsCancel = true };
            buttons.Children.Add(preview); buttons.Children.Add(apply); buttons.Children.Add(cancel);
            root.Children.Add(DialogParts.Footer(buttons));
            var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 180 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 180 });
            var splitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
            System.Windows.Automation.AutomationProperties.SetName(splitter, "Resize original and proposed SQL panes");
            Grid.SetColumn(splitter, 1); grid.Children.Add(splitter);
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
            var proposed = Pane("Proposed SQL", "", 2);
            name.TextChanged += (sender, args) => { Result = null; apply.IsEnabled = false; proposed.Clear(); status.Text = "Name changed. Preview again before applying."; };
            async Task PreviewAsync()
            {
                string requested = name.Text;
                if (string.IsNullOrWhiteSpace(requested)) { status.Text = "Enter a name before previewing."; name.Focus(); return; }
                preview.IsEnabled = false; apply.IsEnabled = false; Result = null;
                preview.Content = "Preparing...";
                progress.Visibility = Visibility.Visible;
                status.Text = "Preparing preview...";
                try
                {
                    var result = await Task.Run(() => rename(requested));
                    if (!IsLoaded || requested != name.Text) return;
                    Result = result; proposed.Text = result.Text;
                    status.Text = $"{result.Changes} references: {result.OldName} to {requested}. Review both panes, then Apply.";
                    apply.IsEnabled = result.Text != sql;
                }
                catch (Exception error) when (!(error is OutOfMemoryException))
                {
                    if (IsLoaded) { proposed.Clear(); status.Text = error.Message; }
                }
                finally { if (IsLoaded) { preview.IsEnabled = true; preview.Content = "_Preview"; progress.Visibility = Visibility.Collapsed; } }
            }
            preview.Click += (sender, args) => { _ = PreviewAsync(); };
            apply.Click += (sender, args) => { if (Result != null) DialogResult = true; };
            root.Children.Add(grid); Content = root;
            Loaded += (sender, args) => { Keyboard.Focus(name); name.SelectAll(); };
        }
    }
}

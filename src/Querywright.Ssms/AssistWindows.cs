#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Querywright.Ssms
{
    internal static class DialogParts
    {
        internal static StackPanel Buttons(Window window, params (string Text, bool IsDefault, bool IsCancel, Action? Click)[] items)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            foreach (var item in items)
            {
                var button = new Button { Content = item.Text, MinWidth = 90, Margin = new Thickness(4), IsDefault = item.IsDefault, IsCancel = item.IsCancel };
                if (item.Click != null) { var click = item.Click; button.Click += (s, e) => click(); }
                panel.Children.Add(button);
            }
            DockPanel.SetDock(panel, Dock.Bottom);
            return panel;
        }
    }

    /// <summary>Checkbox list of columns, all checked unless <c>checkAll</c> is false.</summary>
    internal sealed class ColumnPickerDialog : Window
    {
        internal List<string> Selected { get; } = new List<string>();

        internal ColumnPickerDialog(IReadOnlyList<string> columns, bool checkAll = true)
        {
            Title = "Querywright: pick columns";
            Width = 420; Height = 520; MinWidth = 300; MinHeight = 300;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            // TextBlock content so an underscore in a column name is not read as an access key.
            var boxes = columns.Select(c => new CheckBox { Content = new TextBlock { Text = c }, IsChecked = checkAll, Margin = new Thickness(2) }).ToList();
            var root = new DockPanel { Margin = new Thickness(12) };
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            var all = new Button { Content = "Select _all", Margin = new Thickness(0, 0, 4, 8), Padding = new Thickness(8, 2, 8, 2) };
            var none = new Button { Content = "Select _none", Margin = new Thickness(0, 0, 4, 8), Padding = new Thickness(8, 2, 8, 2) };
            all.Click += (s, e) => boxes.ForEach(b => b.IsChecked = true);
            none.Click += (s, e) => boxes.ForEach(b => b.IsChecked = false);
            top.Children.Add(all); top.Children.Add(none);
            DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
            root.Children.Add(DialogParts.Buttons(this,
                ("_OK", true, false, () =>
                {
                    Selected.Clear();
                    Selected.AddRange(boxes.Where(b => b.IsChecked == true).Select(b => ((TextBlock)b.Content).Text));
                    DialogResult = true;
                }),
                ("_Cancel", false, true, null)));
            var list = new StackPanel();
            boxes.ForEach(b => list.Children.Add(b));
            root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;
            Loaded += (s, e) => { if (boxes.Count > 0) Keyboard.Focus(boxes[0]); };
        }
    }

    /// <summary>Single text input.</summary>
    internal sealed class PromptDialog : Window
    {
        internal string Value { get; private set; } = "";

        internal PromptDialog(string title, string label, string initial)
        {
            Title = title;
            Width = 480; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var root = new DockPanel { Margin = new Thickness(12) };
            var box = new TextBox { Text = initial, Margin = new Thickness(0, 4, 0, 4) };
            var error = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
            var body = new StackPanel();
            body.Children.Add(new Label { Content = label, Target = box });
            body.Children.Add(box);
            body.Children.Add(error);
            DockPanel.SetDock(body, Dock.Top); root.Children.Add(body);
            root.Children.Add(DialogParts.Buttons(this,
                ("_OK", true, false, () =>
                {
                    if (string.IsNullOrWhiteSpace(box.Text)) { error.Text = "Enter a name."; return; }
                    Value = box.Text.Trim(); DialogResult = true;
                }),
                ("_Cancel", false, true, null)));
            Content = root;
            Loaded += (s, e) => { Keyboard.Focus(box); box.SelectAll(); };
        }
    }

    /// <summary>Local, opt-out store of SQL window text so closed tabs can be reopened. Nothing leaves the machine.</summary>
    internal static class TabHistory
    {
        internal static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Querywright", "TabHistory");
        private const int MaxFiles = 200, MaxChars = 2 * 1024 * 1024;

        internal static void Save(Guid id, string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaxChars) return; // ponytail: huge scripts are skipped, not truncated
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(Path.Combine(Folder, id.ToString("N") + ".sql"), text, Encoding.UTF8);
                foreach (var old in new DirectoryInfo(Folder).GetFiles("*.sql").OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxFiles))
                    old.Delete();
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                // History is best effort; never interrupt editing. Log the failure kind only, never the query text.
                Microsoft.VisualStudio.Shell.ActivityLog.TryLogWarning("Querywright", "Tab history save failed: " + error.GetType().Name);
            }
        }
    }

    /// <summary>Search, preview, reopen or delete saved tabs.</summary>
    internal sealed class TabHistoryDialog : Window
    {
        internal string? Text { get; private set; }

        private sealed class Entry
        {
            internal FileInfo File = null!;
            internal string Body = "";
            public override string ToString()
            {
                string first = Body.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
                if (first.Length > 80) first = first.Substring(0, 80) + "...";
                return File.LastWriteTime.ToString("yyyy-MM-dd HH:mm") + "   " + first;
            }
        }

        internal TabHistoryDialog(string folder)
        {
            Title = "Querywright: tab history";
            Width = 1000; Height = 650; MinWidth = 600; MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var entries = new List<Entry>();
            if (Directory.Exists(folder))
                foreach (var file in new DirectoryInfo(folder).GetFiles("*.sql").OrderByDescending(f => f.LastWriteTimeUtc))
                {
                    try { entries.Add(new Entry { File = file, Body = System.IO.File.ReadAllText(file.FullName) }); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            var root = new DockPanel { Margin = new Thickness(12) };
            var search = new TextBox { Margin = new Thickness(0, 4, 0, 8) };
            var top = new StackPanel();
            top.Children.Add(new Label { Content = "_Search:", Target = search });
            top.Children.Add(search);
            DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
            var list = new ListBox { Margin = new Thickness(0, 0, 8, 0) };
            var preview = new TextBox { IsReadOnly = true, AcceptsReturn = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            System.Windows.Automation.AutomationProperties.SetName(list, "Saved tabs");
            System.Windows.Automation.AutomationProperties.SetName(preview, "Preview");
            void Filter()
            {
                string term = search.Text.Trim();
                list.ItemsSource = entries.Where(e => term.Length == 0 || e.Body.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (list.Items.Count > 0) list.SelectedIndex = 0;
            }
            search.TextChanged += (s, e) => Filter();
            list.SelectionChanged += (s, e) => preview.Text = (list.SelectedItem as Entry)?.Body ?? "";
            void Open() { if (list.SelectedItem is Entry entry) { Text = entry.Body; DialogResult = true; } }
            list.MouseDoubleClick += (s, e) => Open();
            root.Children.Add(DialogParts.Buttons(this,
                ("_Open in new window", true, false, Open),
                ("_Delete", false, false, () =>
                {
                    if (!(list.SelectedItem is Entry entry)) return;
                    try { entry.File.Delete(); } catch (IOException) { return; } catch (UnauthorizedAccessException) { return; }
                    entries.Remove(entry); Filter();
                }),
                ("_Cancel", false, true, null)));
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            Grid.SetColumn(preview, 1);
            grid.Children.Add(list); grid.Children.Add(preview);
            root.Children.Add(grid);
            Content = root;
            Filter();
            Loaded += (s, e) => Keyboard.Focus(search);
        }
    }
}

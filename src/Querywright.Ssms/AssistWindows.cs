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

        /// <summary>A user-given name for a saved tab lives next to it; the .sql write time is left alone.</summary>
        internal static string TitlePath(string sqlPath) => Path.ChangeExtension(sqlPath, ".title");

        internal static void Save(Guid id, string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaxChars) return; // ponytail: huge scripts are skipped, not truncated
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(Path.Combine(Folder, id.ToString("N") + ".sql"), text, Encoding.UTF8);
                foreach (var old in new DirectoryInfo(Folder).GetFiles("*.sql").OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxFiles))
                {
                    old.Delete();
                    File.Delete(TitlePath(old.FullName));
                }
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
            internal string? Name;
            public override string ToString()
            {
                if (!string.IsNullOrEmpty(Name)) return File.LastWriteTime.ToString("yyyy-MM-dd HH:mm") + "   [" + Name + "]";
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
                    try
                    {
                        string titlePath = TabHistory.TitlePath(file.FullName);
                        entries.Add(new Entry { File = file, Body = System.IO.File.ReadAllText(file.FullName),
                            Name = System.IO.File.Exists(titlePath) ? System.IO.File.ReadAllText(titlePath).Trim() : null });
                    }
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
                list.ItemsSource = entries.Where(e => term.Length == 0 || e.Body.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0
                    || (e.Name ?? "").IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (list.Items.Count > 0) list.SelectedIndex = 0;
            }
            search.TextChanged += (s, e) => Filter();
            list.SelectionChanged += (s, e) => preview.Text = (list.SelectedItem as Entry)?.Body ?? "";
            void Open() { if (list.SelectedItem is Entry entry) { Text = entry.Body; DialogResult = true; } }
            list.MouseDoubleClick += (s, e) => Open();
            root.Children.Add(DialogParts.Buttons(this,
                ("_Open in new window", true, false, Open),
                ("_Rename...", false, false, () =>
                {
                    if (!(list.SelectedItem is Entry entry)) return;
                    var prompt = new PromptDialog("Querywright: rename saved tab", "_Name:", entry.Name ?? entry.ToString().Substring(19)) { Owner = this };
                    if (prompt.ShowDialog() != true) return;
                    string name = System.Text.RegularExpressions.Regex.Replace(prompt.Value, @"\s+", " ").Trim();
                    if (name.Length > 200) name = name.Substring(0, 200);
                    try { System.IO.File.WriteAllText(TabHistory.TitlePath(entry.File.FullName), name, Encoding.UTF8); }
                    catch (IOException) { return; } catch (UnauthorizedAccessException) { return; }
                    entry.Name = name; int index = list.SelectedIndex; Filter(); list.SelectedIndex = Math.Min(index, list.Items.Count - 1);
                }),
                ("_Delete", false, false, () =>
                {
                    if (!(list.SelectedItem is Entry entry)) return;
                    try { entry.File.Delete(); System.IO.File.Delete(TabHistory.TitlePath(entry.File.FullName)); } catch (IOException) { return; } catch (UnauthorizedAccessException) { return; }
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

    /// <summary>Formatting style editor: property grid with a live preview. Edits a copy; the caller saves on OK.</summary>
    internal sealed class FormattingStyleDialog : System.Windows.Forms.Form
    {
        private const string Sample = "select c.CustomerId, c.Name, count(*) as Orders from dbo.Customer c join dbo.[Order] o on o.CustomerId = c.CustomerId\r\n" +
            "where c.Active = 1 and o.Placed >= '20240101' group by c.CustomerId, c.Name having count(*) > 1 order by Orders desc\r\n" +
            "insert into dbo.Audit (Id, Note) values (1, N'x')";
        internal Querywright.Core.FormattingStyle Style { get; }

        internal FormattingStyleDialog(Querywright.Core.FormattingStyle current, string settingsPath)
        {
            // ponytail: copy through XML so the dialog never mutates the caller's settings until OK.
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(Querywright.Core.FormattingStyle));
            using (var buffer = new MemoryStream())
            {
                serializer.Serialize(buffer, current);
                buffer.Position = 0;
                Style = (Querywright.Core.FormattingStyle)serializer.Deserialize(buffer);
            }
            Text = "Querywright: formatting style (" + settingsPath + ")";
            Width = 1000; Height = 600;
            StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            var grid = new System.Windows.Forms.PropertyGrid { SelectedObject = Style, Dock = System.Windows.Forms.DockStyle.Left, Width = 380, ToolbarVisible = false };
            var preview = new System.Windows.Forms.TextBox
            {
                Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = System.Windows.Forms.ScrollBars.Both,
                Dock = System.Windows.Forms.DockStyle.Fill, Font = new System.Drawing.Font("Consolas", 10f)
            };
            var buttons = new System.Windows.Forms.FlowLayoutPanel { Dock = System.Windows.Forms.DockStyle.Bottom, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft, Height = 40 };
            var cancel = new System.Windows.Forms.Button { Text = "Cancel", DialogResult = System.Windows.Forms.DialogResult.Cancel, Width = 90 };
            var ok = new System.Windows.Forms.Button { Text = "Save", DialogResult = System.Windows.Forms.DialogResult.OK, Width = 90 };
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
            AcceptButton = ok; CancelButton = cancel;
            Controls.Add(preview); Controls.Add(grid); Controls.Add(buttons);
            void Render()
            {
                if (Style.IndentSize < 1 || Style.IndentSize > 16) { preview.Text = "Indent size must be between 1 and 16."; ok.Enabled = false; return; }
                ok.Enabled = true;
                try { preview.Text = Querywright.Core.SqlFormatting.Format(Sample, Style); }
                catch (Exception error) when (!(error is OutOfMemoryException)) { preview.Text = error.Message; }
            }
            grid.PropertyValueChanged += (s, e) => Render();
            Render();
        }
    }

    /// <summary>Hover popup: Script and Summary tabs for one database object. Read-only; Copy puts the script on the clipboard.</summary>
    internal sealed class ObjectInfoWindow : Window
    {
        internal ObjectInfoWindow(string title, string script, IEnumerable<(string Name, string Type, string Nullability)> summary, bool parameters)
        {
            Title = title;
            Width = 760; Height = 560; MinWidth = 360; MinHeight = 240;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            var root = new DockPanel { Margin = new Thickness(12) };
            root.Children.Add(DialogParts.Buttons(this,
                ("_Copy", false, false, () => Clipboard.SetText(script)),
                ("Close", true, true, () => Close())));
            var text = new TextBox
            {
                Text = script, IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 13,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            var view = new GridView();
            string[] headers = parameters ? new[] { "Parameter", "Data Type", "Direction" } : new[] { "Column Name", "Data Type", "Nullability" };
            string[] paths = { "Item1", "Item2", "Item3" };
            for (int i = 0; i < 3; i++)
                view.Columns.Add(new GridViewColumn { Header = headers[i], DisplayMemberBinding = new System.Windows.Data.Binding(paths[i]), Width = i == 0 ? 260 : 180 });
            var list = new ListView { View = view, ItemsSource = summary.Select(r => Tuple.Create(r.Name, r.Type, r.Nullability)).ToList() };
            var tabs = new TabControl();
            tabs.Items.Add(new TabItem { Header = "Script", Content = text });
            tabs.Items.Add(new TabItem { Header = "Summary", Content = list });
            root.Children.Add(tabs);
            Content = root;
        }
    }
}

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

        /// <summary>With <paramref name="choices"/>, an editable drop-down instead of a text box.</summary>
        internal PromptDialog(string title, string label, string initial, IReadOnlyList<string>? choices = null)
        {
            Title = title;
            Width = 480; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var root = new DockPanel { Margin = new Thickness(12) };
            Control box = choices == null
                ? new TextBox { Text = initial, Margin = new Thickness(0, 4, 0, 4) }
                : new ComboBox { ItemsSource = choices, IsEditable = true, Text = initial, Margin = new Thickness(0, 4, 0, 4) };
            string Text() => (box as TextBox)?.Text ?? ((ComboBox)box).Text;
            var error = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
            var body = new StackPanel();
            body.Children.Add(new Label { Content = label, Target = box });
            body.Children.Add(box);
            body.Children.Add(error);
            DockPanel.SetDock(body, Dock.Top); root.Children.Add(body);
            root.Children.Add(DialogParts.Buttons(this,
                ("_OK", true, false, () =>
                {
                    if (string.IsNullOrWhiteSpace(Text())) { error.Text = "Enter a name."; return; }
                    Value = Text().Trim(); DialogResult = true;
                }),
                ("_Cancel", false, true, null)));
            Content = root;
            Loaded += (s, e) => { Keyboard.Focus(box); (box as TextBox)?.SelectAll(); };
        }
    }

    /// <summary>
    /// Local, opt-out store of SQL window text so closed tabs can be reopened. One folder per window holding a timestamped
    /// version per edit pause, execution and close. Nothing leaves the machine.
    /// </summary>
    internal static class TabHistory
    {
        internal static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Querywright", "TabHistory");
        private const int MaxTabs = 200, MaxVersions = 100, MaxChars = 2 * 1024 * 1024;
        internal const string Info = "tab.txt", Title = "title.txt", Favorite = "favorite.txt";

        /// <summary>A user-given name for a saved tab (pre-1.0.2 single-file tabs keep theirs next to the .sql).</summary>
        internal static string TitlePath(string sqlPath) => Path.ChangeExtension(sqlPath, ".title");
        internal static string FavoritePath(string sqlPath) => Path.ChangeExtension(sqlPath, ".favorite");

        /// <summary>Adds a version. Version files are named by UTC ticks; an "x" suffix marks text sent to Execute.</summary>
        internal static void Save(Guid id, string name, string connection, string text, bool executed)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaxChars) return; // ponytail: huge scripts are skipped, not truncated
            try
            {
                var tab = Directory.CreateDirectory(Path.Combine(Folder, id.ToString("N")));
                File.WriteAllText(Path.Combine(tab.FullName, Info), name + "\n" + connection, Encoding.UTF8);
                File.WriteAllText(Path.Combine(tab.FullName, DateTime.UtcNow.Ticks.ToString("D19") + (executed ? "x" : "") + ".sql"), text, Encoding.UTF8);
                foreach (var old in tab.GetFiles("*.sql").OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(MaxVersions)) old.Delete();
                var root = new DirectoryInfo(Folder);
                // Favorites are never pruned and do not count toward the limit.
                foreach (var old in root.GetDirectories().Where(d => !File.Exists(Path.Combine(d.FullName, Favorite)))
                    .OrderByDescending(d => d.LastWriteTimeUtc).Skip(MaxTabs)) old.Delete(true);
                foreach (var old in root.GetFiles("*.sql").Where(f => !File.Exists(FavoritePath(f.FullName)))
                    .OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxTabs))
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

    /// <summary>Search saved tabs, pick a timestamped version, preview it, reopen it; rename or delete a tab.</summary>
    internal sealed class TabHistoryDialog : Window
    {
        internal string? Text { get; private set; }

        private sealed class Version
        {
            internal FileInfo File = null!;
            internal DateTime Time;
            internal bool Executed;
            public override string ToString() => Time.ToString("yyyy-MM-dd HH:mm:ss") + (Executed ? "   \u25B6 executed" : "   edited");
        }

        private sealed class Tab
        {
            internal DirectoryInfo? Folder;
            internal FileInfo? Legacy; // pre-1.0.2: one file, one version
            internal string Caption = "", Connection = "", Latest = "";
            internal string? Name;
            internal bool Favorite;
            internal List<Version> Versions = new List<Version>();
            internal string TitleFile => Folder != null ? Path.Combine(Folder.FullName, TabHistory.Title) : TabHistory.TitlePath(Legacy!.FullName);
            internal string FavoriteFile => Folder != null ? Path.Combine(Folder.FullName, TabHistory.Favorite) : TabHistory.FavoritePath(Legacy!.FullName);
            public override string ToString()
            {
                string first = Latest.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
                if (first.Length > 60) first = first.Substring(0, 60) + "...";
                string label = !string.IsNullOrEmpty(Name) ? "[" + Name + "]" : Caption.Length > 0 ? Caption + "   " + first : first;
                return Versions[0].Time.ToString("yyyy-MM-dd HH:mm") + "   " + label + (Connection.Length > 0 ? "   (" + Connection + ")" : "");
            }
        }

        private static string? ReadOrNull(string path) => File.Exists(path) ? File.ReadAllText(path).Trim() : null;

        private static Tab? Load(DirectoryInfo folder)
        {
            var versions = folder.GetFiles("*.sql").OrderByDescending(f => f.Name, StringComparer.Ordinal).Select(f =>
            {
                string stamp = Path.GetFileNameWithoutExtension(f.Name);
                bool executed = stamp.EndsWith("x");
                return new Version { File = f, Executed = executed,
                    Time = long.TryParse(executed ? stamp.Substring(0, stamp.Length - 1) : stamp, out long ticks) && ticks > 0 && ticks < DateTime.MaxValue.Ticks
                        ? new DateTime(ticks, DateTimeKind.Utc).ToLocalTime() : f.LastWriteTime };
            }).ToList();
            if (versions.Count == 0) return null;
            string[] info = (ReadOrNull(Path.Combine(folder.FullName, TabHistory.Info)) ?? "").Split('\n');
            return new Tab { Folder = folder, Versions = versions, Latest = File.ReadAllText(versions[0].File.FullName),
                Caption = info[0].Trim(), Connection = info.Length > 1 ? info[1].Trim() : "", Name = ReadOrNull(Path.Combine(folder.FullName, TabHistory.Title)),
                Favorite = File.Exists(Path.Combine(folder.FullName, TabHistory.Favorite)) };
        }

        internal TabHistoryDialog(string folder)
        {
            Title = "Querywright: tab history";
            Width = 1100; Height = 700; MinWidth = 700; MinHeight = 450;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var tabs = new List<Tab>();
            if (Directory.Exists(folder))
            {
                var root = new DirectoryInfo(folder);
                foreach (var directory in root.GetDirectories())
                    try { if (Load(directory) is Tab tab) tabs.Add(tab); }
                    catch (IOException) { } catch (UnauthorizedAccessException) { }
                foreach (var file in root.GetFiles("*.sql"))
                    try
                    {
                        string body = File.ReadAllText(file.FullName);
                        tabs.Add(new Tab { Legacy = file, Latest = body, Name = ReadOrNull(TabHistory.TitlePath(file.FullName)),
                            Favorite = File.Exists(TabHistory.FavoritePath(file.FullName)), Versions = { new Version { File = file, Time = file.LastWriteTime } } });
                    }
                    catch (IOException) { } catch (UnauthorizedAccessException) { }
                tabs.Sort((a, b) => b.Versions[0].Time.CompareTo(a.Versions[0].Time));
            }
            var layout = new DockPanel { Margin = new Thickness(12) };
            var search = new TextBox { Margin = new Thickness(0, 4, 0, 8) };
            var top = new StackPanel();
            var views = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var historyView = new RadioButton { Content = "_History", IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
            var favoritesView = new RadioButton { Content = "F_avorites" };
            views.Children.Add(historyView); views.Children.Add(favoritesView);
            top.Children.Add(views);
            top.Children.Add(new Label { Content = "_Search:", Target = search });
            top.Children.Add(search);
            DockPanel.SetDock(top, Dock.Top); layout.Children.Add(top);
            var list = new ListBox();
            var versionList = new ListBox();
            var versionsLabel = new Label { Content = "_History:", Target = versionList, Padding = new Thickness(0, 8, 0, 4) };
            var preview = new TextBox { IsReadOnly = true, AcceptsReturn = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(8, 0, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetName(list, "Saved tabs");
            System.Windows.Automation.AutomationProperties.SetName(versionList, "Versions of the selected tab");
            System.Windows.Automation.AutomationProperties.SetName(preview, "Preview");
            Tab? Selected() => (list.SelectedItem as ListBoxItem)?.Tag as Tab;
            // History groups by the latest version's day; favorites are their own view, not repeated in history.
            string Bucket(DateTime time)
            {
                var today = DateTime.Today;
                return time >= today ? "Today" : time >= today.AddDays(-1) ? "Yesterday" : time >= today.AddDays(-7) ? "Last week"
                    : time >= today.AddMonths(-1) ? "Last month" : "Older";
            }
            void Filter(Tab? keep = null)
            {
                // ponytail: search covers each tab's name and latest text, not every old version.
                string term = search.Text.Trim();
                bool favorites = favoritesView.IsChecked == true;
                favoritesView.Content = "F_avorites (" + tabs.Count(t => t.Favorite) + ")";
                list.Items.Clear();
                string? group = null;
                foreach (var tab in tabs.Where(t => t.Favorite == favorites && (term.Length == 0 || t.Latest.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0
                    || (t.Name ?? "").IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 || t.Caption.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)))
                {
                    string bucket = Bucket(tab.Versions[0].Time);
                    if (!favorites && bucket != group)
                    {
                        group = bucket;
                        list.Items.Add(new ListBoxItem { Content = bucket, IsEnabled = false, Focusable = false, FontWeight = FontWeights.Bold, Margin = new Thickness(0, list.Items.Count == 0 ? 0 : 8, 0, 2) });
                    }
                    var item = new ListBoxItem { Content = tab.ToString(), Tag = tab };
                    list.Items.Add(item);
                    if (tab == keep) list.SelectedItem = item;
                }
                if (Selected() == null) list.SelectedItem = list.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is Tab);
                if (list.Items.Count == 0) list.Items.Add(new ListBoxItem { Content = favorites ? "No favorites yet. Select a tab in History and click Add to favorites." : "No saved tabs.", IsEnabled = false, Focusable = false });
            }
            search.TextChanged += (s, e) => Filter();
            historyView.Checked += (s, e) => Filter();
            favoritesView.Checked += (s, e) => Filter();
            list.SelectionChanged += (s, e) =>
            {
                var tab = Selected();
                versionsLabel.Content = "_History" + (tab == null ? "" : " for " + (tab.Name ?? (tab.Caption.Length > 0 ? tab.Caption : "this tab")).Replace("_", "__")) + ":";
                versionList.ItemsSource = tab?.Versions;
                if (tab != null) versionList.SelectedIndex = 0;
            };
            versionList.SelectionChanged += (s, e) =>
            {
                try { preview.Text = versionList.SelectedItem is Version version ? File.ReadAllText(version.File.FullName) : ""; }
                catch (IOException) { preview.Text = ""; } catch (UnauthorizedAccessException) { preview.Text = ""; }
            };
            void Open() { if (versionList.SelectedItem is Version) { Text = preview.Text; DialogResult = true; } }
            list.MouseDoubleClick += (s, e) => Open();
            versionList.MouseDoubleClick += (s, e) => Open();
            Button? favoriteButton = null;
            list.SelectionChanged += (s, e) => { if (favoriteButton != null) favoriteButton.Content = Selected()?.Favorite == true ? "Remove from _favorites" : "Add to _favorites"; };
            var buttons = DialogParts.Buttons(this,
                ("_Open in new window", true, false, Open),
                ("Add to _favorites", false, false, () =>
                {
                    if (!(Selected() is Tab tab)) return;
                    try
                    {
                        if (tab.Favorite) File.Delete(tab.FavoriteFile);
                        else File.WriteAllText(tab.FavoriteFile, "", Encoding.UTF8);
                    }
                    catch (IOException) { return; } catch (UnauthorizedAccessException) { return; }
                    tab.Favorite = !tab.Favorite; Filter();
                }),
                ("_Rename...", false, false, () =>
                {
                    if (!(Selected() is Tab tab)) return;
                    var prompt = new PromptDialog("Querywright: rename saved tab", "_Name:", tab.Name ?? tab.Caption) { Owner = this };
                    if (prompt.ShowDialog() != true) return;
                    string name = System.Text.RegularExpressions.Regex.Replace(prompt.Value, @"\s+", " ").Trim();
                    if (name.Length > 200) name = name.Substring(0, 200);
                    try { File.WriteAllText(tab.TitleFile, name, Encoding.UTF8); }
                    catch (IOException) { return; } catch (UnauthorizedAccessException) { return; }
                    tab.Name = name; Filter(tab);
                }),
                ("_Delete tab", false, false, () =>
                {
                    if (!(Selected() is Tab tab)) return;
                    try
                    {
                        if (tab.Folder != null) tab.Folder.Delete(true);
                        else { tab.Legacy!.Delete(); File.Delete(TabHistory.TitlePath(tab.Legacy.FullName)); File.Delete(TabHistory.FavoritePath(tab.Legacy.FullName)); }
                    }
                    catch (IOException) { return; } catch (UnauthorizedAccessException) { return; }
                    tabs.Remove(tab); Filter();
                }),
                ("_Cancel", false, true, null));
            favoriteButton = (Button)buttons.Children[1];
            layout.Children.Add(buttons);
            var left = new DockPanel();
            DockPanel.SetDock(versionList, Dock.Bottom); versionList.Height = 220;
            DockPanel.SetDock(versionsLabel, Dock.Bottom);
            left.Children.Add(versionList); left.Children.Add(versionsLabel); left.Children.Add(list);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            Grid.SetColumn(preview, 1);
            grid.Children.Add(left); grid.Children.Add(preview);
            layout.Children.Add(grid);
            Content = layout;
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

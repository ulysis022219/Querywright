#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Automation;

namespace Querywright.Ssms
{
    internal static class DialogParts
    {
        internal static void Style(Window window)
        {
            window.ShowInTaskbar = false;
            window.UseLayoutRounding = true;
            window.FontFamily = SystemFonts.MessageFontFamily;
            window.FontSize = Math.Max(13, SystemFonts.MessageFontSize);
            window.SetResourceReference(Control.BackgroundProperty, SystemColors.ControlBrushKey);
            window.SetResourceReference(Control.ForegroundProperty, SystemColors.ControlTextBrushKey);
            if (Application.Current != null) HostTheme(window);
            void ControlStyle(Type type, params Setter[] setters)
            {
                var style = new Style(type, window.TryFindResource(type) as Style);
                foreach (var setter in setters) style.Setters.Add(setter);
                if (type == typeof(Button))
                {
                    var primary = new MultiTrigger();
                    primary.Conditions.Add(new Condition(Button.IsDefaultProperty, true));
                    primary.Conditions.Add(new Condition(Button.IsCancelProperty, false));
                    primary.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
                    primary.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(2)));
                    style.Triggers.Add(primary);
                }
                window.Resources[type] = style;
            }
            ControlStyle(typeof(Button), new Setter(Control.PaddingProperty, new Thickness(14, 6, 14, 6)),
                new Setter(FrameworkElement.MinHeightProperty, 32.0));
            ControlStyle(typeof(TextBox), new Setter(Control.PaddingProperty, new Thickness(8, 6, 8, 6)));
            ControlStyle(typeof(ComboBox), new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
            ControlStyle(typeof(ListBoxItem), new Setter(Control.PaddingProperty, new Thickness(6, 5, 6, 5)),
                new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            ControlStyle(typeof(CheckBox), new Setter(Control.PaddingProperty, new Thickness(4, 2, 4, 2)));
            ControlStyle(typeof(TabItem), new Setter(Control.PaddingProperty, new Thickness(14, 7, 14, 7)));
        }

        // Keep standalone dialog checks independent of the SSMS-only SDK assemblies.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void HostTheme(Window window)
        {
            if (window.TryFindResource(Microsoft.VisualStudio.Shell.VsResourceKeys.ThemedDialogDefaultStylesKey) is ResourceDictionary theme)
                window.Resources.MergedDictionaries.Add(theme);
            if (window.TryFindResource(Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowBackgroundBrushKey) is Brush)
                window.SetResourceReference(Control.BackgroundProperty, Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowBackgroundBrushKey);
            if (window.TryFindResource(Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowTextBrushKey) is Brush)
                window.SetResourceReference(Control.ForegroundProperty, Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowTextBrushKey);
        }

        internal static void Style(System.Windows.Forms.Form form)
        {
            if (Application.Current != null) HostTheme(form);
        }

        /// <summary>Styles the form and shows it modal to the SSMS main window (screen-centered when standalone).</summary>
        internal static System.Windows.Forms.DialogResult ShowModal(System.Windows.Forms.Form form)
        {
            Style(form);
            var owner = Application.Current != null ? HostOwner() : IntPtr.Zero;
            if (owner == IntPtr.Zero) return form.ShowDialog();
            form.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            return form.ShowDialog(new Owner(owner));
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static IntPtr HostOwner()
        {
            var shell = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(Microsoft.VisualStudio.Shell.Interop.SVsUIShell)) as Microsoft.VisualStudio.Shell.Interop.IVsUIShell;
            return shell != null && shell.GetDialogOwnerHwnd(out IntPtr owner) == 0 ? owner : IntPtr.Zero;
        }

        private sealed class Owner : System.Windows.Forms.IWin32Window
        {
            internal Owner(IntPtr handle) => Handle = handle;
            public IntPtr Handle { get; }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void HostTheme(System.Windows.Forms.Form form)
        {
            var background = Application.Current.TryFindResource(Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowBackgroundBrushKey) as SolidColorBrush;
            var foreground = Application.Current.TryFindResource(Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowTextBrushKey) as SolidColorBrush;
            if (background == null || foreground == null) return;
            var back = System.Drawing.Color.FromArgb(background.Color.R, background.Color.G, background.Color.B);
            var fore = System.Drawing.Color.FromArgb(foreground.Color.R, foreground.Color.G, foreground.Color.B);
            void Paint(System.Windows.Forms.Control control)
            {
                control.BackColor = back; control.ForeColor = fore;
                if (control is System.Windows.Forms.Button button)
                {
                    button.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
                    button.FlatAppearance.BorderColor = fore;
                    button.FlatAppearance.BorderSize = button == form.AcceptButton ? 2 : 1;
                }
                if (control is System.Windows.Forms.PropertyGrid property)
                {
                    property.ViewBackColor = property.HelpBackColor = property.CategorySplitterColor = back;
                    property.ViewForeColor = property.HelpForeColor = property.CategoryForeColor = fore;
                    property.LineColor = System.Drawing.SystemColors.ControlDark;
                }
                if (control is System.Windows.Forms.DataGridView grid)
                {
                    grid.BackgroundColor = back; grid.EnableHeadersVisualStyles = false;
                    grid.DefaultCellStyle.BackColor = grid.ColumnHeadersDefaultCellStyle.BackColor = back;
                    grid.DefaultCellStyle.ForeColor = grid.ColumnHeadersDefaultCellStyle.ForeColor = fore;
                    grid.DefaultCellStyle.SelectionBackColor = System.Drawing.SystemColors.Highlight;
                    grid.DefaultCellStyle.SelectionForeColor = System.Drawing.SystemColors.HighlightText;
                }
                foreach (System.Windows.Forms.Control child in control.Controls) Paint(child);
            }
            // These dialogs are modal: read the active host theme each time they open.
            Paint(form);
        }

        internal static System.Windows.Forms.Label FormHeader(string title, string description)
            => new System.Windows.Forms.Label { Text = title + "\r\n" + description, Dock = System.Windows.Forms.DockStyle.Top,
                AutoSize = true, Padding = new System.Windows.Forms.Padding(0, 0, 0, 16), UseMnemonic = false };

        internal static StackPanel Header(string title, string description)
        {
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            header.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            header.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) });
            DockPanel.SetDock(header, Dock.Top);
            return header;
        }

        internal static Border Footer(UIElement buttons)
        {
            var footer = new Border { Child = buttons, BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 12, 0, 0) };
            footer.SetResourceReference(Border.BorderBrushProperty, SystemColors.ActiveBorderBrushKey);
            DockPanel.SetDock(footer, Dock.Bottom);
            return footer;
        }

        internal static TextBlock Status()
        {
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
            return status;
        }

        internal static WrapPanel Buttons(Window window, params (string Text, bool IsDefault, bool IsCancel, Action? Click)[] items)
        {
            var panel = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
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
            DialogParts.Style(this);
            Width = 460; Height = 540; MinWidth = 360; MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            // TextBlock content so an underscore in a column name is not read as an access key.
            var boxes = columns.Select(c => new CheckBox { Content = new TextBlock { Text = c }, IsChecked = checkAll, Margin = new Thickness(2) }).ToList();
            var root = new DockPanel { Margin = new Thickness(12) };
            root.Children.Add(DialogParts.Header("Choose columns", "Select the columns to include in your script."));
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            var all = new Button { Content = "Select _all", Margin = new Thickness(0, 0, 4, 8), Padding = new Thickness(8, 2, 8, 2) };
            var none = new Button { Content = "Select _none", Margin = new Thickness(0, 0, 4, 8), Padding = new Thickness(8, 2, 8, 2) };
            all.Click += (s, e) => boxes.ForEach(b => b.IsChecked = true);
            none.Click += (s, e) => boxes.ForEach(b => b.IsChecked = false);
            top.Children.Add(all); top.Children.Add(none);
            DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
            root.Children.Add(DialogParts.Footer(DialogParts.Buttons(this,
                ("_OK", true, false, () =>
                {
                    Selected.Clear();
                    Selected.AddRange(boxes.Where(b => b.IsChecked == true).Select(b => ((TextBlock)b.Content).Text));
                    DialogResult = true;
                }),
                ("_Cancel", false, true, null))));
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
            DialogParts.Style(this);
            Width = 480; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var root = new DockPanel { Margin = new Thickness(12) };
            root.Children.Add(DialogParts.Header(title.Replace("Querywright: ", ""), choices == null ? "Enter a name to continue." : "Choose an existing name or enter a new one."));
            Control box = choices == null
                ? new TextBox { Text = initial, Margin = new Thickness(0, 4, 0, 4) }
                : new ComboBox { ItemsSource = choices, IsEditable = true, Text = initial, Margin = new Thickness(0, 4, 0, 4) };
            string Text() => (box as TextBox)?.Text ?? ((ComboBox)box).Text;
            var error = DialogParts.Status();
            error.FontWeight = FontWeights.SemiBold;
            box.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((s, e) => error.Text = ""));
            var body = new StackPanel();
            body.Children.Add(new Label { Content = label, Target = box });
            body.Children.Add(box);
            body.Children.Add(error);
            DockPanel.SetDock(body, Dock.Top); root.Children.Add(body);
            root.Children.Add(DialogParts.Footer(DialogParts.Buttons(this,
                ("_OK", true, false, () =>
                {
                    if (string.IsNullOrWhiteSpace(Text())) { error.Text = "Enter a name."; box.Focus(); return; }
                    Value = Text().Trim(); DialogResult = true;
                }),
                ("_Cancel", false, true, null))));
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
        private const long MaxBytes = 256L * 1024 * 1024;
        private static long lastCap;
        internal const string Info = "tab.txt", Title = "title.txt", Favorite = "favorite.txt";

        /// <summary>A user-given name for a saved tab (pre-1.0.2 single-file tabs keep theirs next to the .sql).</summary>
        internal static string TitlePath(string sqlPath) => Path.ChangeExtension(sqlPath, ".title");
        internal static string FavoritePath(string sqlPath) => Path.ChangeExtension(sqlPath, ".favorite");

        /// <summary>Adds a version. Version files are named by UTC ticks; an "x" suffix marks text sent to Execute.</summary>
        internal static void Save(Guid id, string name, string connection, string text, bool executed)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaxChars) return; // ponytail: huge scripts are skipped, not truncated
            string? redacted = Querywright.Core.SqlRefactoring.RedactSecrets(text);
            if (redacted == null) return; // too slow to redact: skip rather than store a password
            try
            {
                var tab = Directory.CreateDirectory(Path.Combine(Folder, id.ToString("N")));
                File.WriteAllText(Path.Combine(tab.FullName, Info), name + "\n" + connection, Encoding.UTF8);
                File.WriteAllText(Path.Combine(tab.FullName, DateTime.UtcNow.Ticks.ToString("D19") + (executed ? "x" : "") + ".sql"), redacted, Encoding.UTF8);
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
                Cap(root);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                // History is best effort; never interrupt editing. Log the failure kind only, never the query text.
                Microsoft.VisualStudio.Shell.ActivityLog.TryLogWarning("Querywright", "Tab history save failed: " + error.GetType().Name);
            }
        }

        // Oldest versions go first once non-favorite history passes MaxBytes.
        // ponytail: checked at most every 10 minutes, so history can briefly exceed the cap.
        private static void Cap(DirectoryInfo root)
        {
            long now = DateTime.UtcNow.Ticks, last = System.Threading.Interlocked.Read(ref lastCap);
            if (now - last < TimeSpan.TicksPerMinute * 10 || System.Threading.Interlocked.CompareExchange(ref lastCap, now, last) != last) return;
            long total = 0;
            foreach (var file in root.GetDirectories().Where(d => !File.Exists(Path.Combine(d.FullName, Favorite))).SelectMany(d => d.GetFiles("*.sql"))
                .Concat(root.GetFiles("*.sql").Where(f => !File.Exists(FavoritePath(f.FullName)))).OrderByDescending(f => f.LastWriteTimeUtc).ToList())
            {
                if ((total += file.Length) <= MaxBytes) continue;
                file.Delete();
                if (string.Equals(file.DirectoryName, root.FullName, StringComparison.OrdinalIgnoreCase)) File.Delete(TitlePath(file.FullName));
                else if (file.Directory!.GetFiles("*.sql").Length == 0) file.Directory.Delete(true);
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
            public override string ToString() => Time.ToString("yyyy-MM-dd h:mm:ss tt", CultureInfo.InvariantCulture) + (Executed ? "   \u25B6 executed" : "   edited");
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
            internal string DisplayName
            {
                get
                {
                    string first = Latest.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "Saved query";
                    return Name ?? (Caption.Length > 0 ? Caption : first.Substring(0, Math.Min(80, first.Length)));
                }
            }
            internal string Detail => Versions[0].Time.ToString("MMM d, h:mm tt", CultureInfo.InvariantCulture) + "  ·  " + Versions.Count + " version(s)" + (Connection.Length > 0 ? "\n" + Connection : "");
            public override string ToString() => DisplayName + "\n" + Detail;
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
            DialogParts.Style(this);
            Width = 1100; Height = 760; MinWidth = 760; MinHeight = 580;
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
            layout.Children.Add(DialogParts.Header("Tab history", "Find a saved query, review a version, and reopen it in a new window."));
            var feedback = DialogParts.Status();
            DockPanel.SetDock(feedback, Dock.Bottom); layout.Children.Add(feedback);
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
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
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
            // Old versions are read once, on the first search that needs them, and kept for this dialog only.
            var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool VersionHas(Version version, string term)
            {
                if (!texts.TryGetValue(version.File.FullName, out var text))
                {
                    try { text = File.ReadAllText(version.File.FullName); }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { text = ""; }
                    texts[version.File.FullName] = text;
                }
                return text.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            void Filter(Tab? keep = null)
            {
                string term = search.Text.Trim();
                bool favorites = favoritesView.IsChecked == true;
                favoritesView.Content = "F_avorites (" + tabs.Count(t => t.Favorite) + ")";
                list.Items.Clear();
                string? group = null;
                foreach (var tab in tabs.Where(t => t.Favorite == favorites && (term.Length == 0 || t.Latest.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0
                    || (t.Name ?? "").IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 || t.Caption.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0
                    || t.Versions.Skip(1).Any(v => VersionHas(v, term)))))
                {
                    string bucket = Bucket(tab.Versions[0].Time);
                    if (!favorites && bucket != group)
                    {
                        group = bucket;
                        list.Items.Add(new ListBoxItem { Content = bucket, IsEnabled = false, Focusable = false, FontWeight = FontWeights.Bold, Margin = new Thickness(0, list.Items.Count == 0 ? 0 : 8, 0, 2) });
                    }
                    var row = new StackPanel();
                    row.Children.Add(new TextBlock { Text = (tab.Favorite ? "\u2605  " : "") + tab.DisplayName, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                    row.Children.Add(new TextBlock { Text = tab.Detail, FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
                    var item = new ListBoxItem { Content = row, Tag = tab, ToolTip = tab.ToString() };
                    AutomationProperties.SetName(item, (tab.Favorite ? "Favorite: " : "") + tab.ToString());
                    list.Items.Add(item);
                    if (tab == keep) list.SelectedItem = item;
                }
                if (Selected() == null) list.SelectedItem = list.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is Tab);
                if (list.Items.Count == 0) list.Items.Add(new ListBoxItem { Content = new TextBlock { Text = term.Length > 0 ? "No tabs match your search. Try a shorter name or SQL fragment." : favorites ? "No favorites yet. Select a tab in History and click Add to favorites." : "No saved tabs yet. Queries appear here as you edit and execute them when tab history is enabled.", TextWrapping = TextWrapping.Wrap }, IsEnabled = false, Focusable = false });
            }
            search.TextChanged += (s, e) => Filter();
            historyView.Checked += (s, e) => Filter();
            favoritesView.Checked += (s, e) => Filter();
            list.SelectionChanged += (s, e) =>
            {
                var tab = Selected();
                versionsLabel.Content = "_Versions:";
                versionsLabel.ToolTip = tab?.DisplayName;
                versionList.ItemsSource = tab?.Versions;
                // Searching: open on the newest version that contains the text.
                string term = search.Text.Trim();
                if (tab != null) versionList.SelectedIndex = term.Length == 0 ? 0 : Math.Max(0, tab.Versions.FindIndex(v => v == tab.Versions[0] ? tab.Latest.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 : VersionHas(v, term)));
            };
            bool previewReady = false;
            versionList.SelectionChanged += (s, e) =>
            {
                previewReady = false;
                feedback.Text = "";
                try { preview.Text = versionList.SelectedItem is Version version ? File.ReadAllText(version.File.FullName) : ""; previewReady = versionList.SelectedItem is Version; }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { preview.Text = ""; feedback.Text = "Could not read this version. " + error.Message; }
            };
            void Open() { if (previewReady) { Text = preview.Text; DialogResult = true; } }
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
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { feedback.Text = "Could not update favorite. " + error.Message; return; }
                    tab.Favorite = !tab.Favorite; Filter();
                    feedback.Text = tab.Favorite ? "Added to favorites." : "Removed from favorites.";
                }),
                ("_Rename...", false, false, () =>
                {
                    if (!(Selected() is Tab tab)) return;
                    var prompt = new PromptDialog("Querywright: rename saved tab", "_Name:", tab.Name ?? tab.Caption) { Owner = this };
                    if (prompt.ShowDialog() != true) return;
                    string name = System.Text.RegularExpressions.Regex.Replace(prompt.Value, @"\s+", " ").Trim();
                    if (name.Length > 200) name = name.Substring(0, 200);
                    try { File.WriteAllText(tab.TitleFile, name, Encoding.UTF8); }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { feedback.Text = "Could not rename this tab. " + error.Message; return; }
                    tab.Name = name; Filter(tab);
                    feedback.Text = "Tab renamed.";
                }),
                ("_Delete tab", false, false, () =>
                {
                    if (!(Selected() is Tab tab)) return;
                    if (MessageBox.Show(this, "Delete \"" + tab.DisplayName + "\" and all " + tab.Versions.Count + " saved version(s)? This cannot be undone.",
                        "Delete saved tab", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                    try
                    {
                        if (tab.Folder != null) tab.Folder.Delete(true);
                        else { tab.Legacy!.Delete(); File.Delete(TabHistory.TitlePath(tab.Legacy.FullName)); File.Delete(TabHistory.FavoritePath(tab.Legacy.FullName)); }
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { feedback.Text = "Could not delete this tab. " + error.Message; return; }
                    tabs.Remove(tab); Filter();
                    feedback.Text = "Saved tab deleted.";
                }),
                ("_Cancel", false, true, null));
            favoriteButton = (Button)buttons.Children[1];
            ((Button)buttons.Children[0]).ToolTip = "Open the selected version without executing it (Enter).";
            ((Button)buttons.Children[3]).ToolTip = "Permanently delete this tab and every saved version. Confirmation required.";
            ((Button)buttons.Children[3]).Margin = new Thickness(16, 4, 4, 4);
            void UpdateButtons()
            {
                ((Button)buttons.Children[0]).IsEnabled = previewReady;
                for (int i = 1; i <= 3; i++) ((Button)buttons.Children[i]).IsEnabled = Selected() != null;
            }
            list.SelectionChanged += (s, e) => UpdateButtons();
            versionList.SelectionChanged += (s, e) => UpdateButtons();
            layout.Children.Add(DialogParts.Footer(buttons));
            var left = new Grid();
            left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 60 });
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 60 });
            Grid.SetRow(versionsLabel, 1); Grid.SetRow(versionList, 2);
            left.Children.Add(list); left.Children.Add(versionsLabel); left.Children.Add(versionList);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 220 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star), MinWidth = 180 });
            var splitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
            System.Windows.Automation.AutomationProperties.SetName(splitter, "Resize history and preview panes");
            Grid.SetColumn(splitter, 1); grid.Children.Add(splitter);
            var previewPanel = new DockPanel();
            var previewLabel = new Label { Content = "SQL preview", FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 4) };
            DockPanel.SetDock(previewLabel, Dock.Top); previewPanel.Children.Add(previewLabel); previewPanel.Children.Add(preview);
            Grid.SetColumn(previewPanel, 2);
            grid.Children.Add(left); grid.Children.Add(previewPanel);
            layout.Children.Add(grid);
            Content = layout;
            Filter();
            UpdateButtons();
            Loaded += (s, e) => Keyboard.Focus(search);
        }
    }

    /// <summary>Formatting style (property grid with a live preview) and analysis rule severities. Edits a copy; the caller saves on OK.</summary>
    internal sealed class FormattingStyleDialog : System.Windows.Forms.Form
    {
        private const string Sample = "select c.CustomerId, c.Name, count(*) as Orders from dbo.Customer c join dbo.[Order] o on o.CustomerId = c.CustomerId\r\n" +
            "where c.Active = 1 and o.Placed >= '20240101' group by c.CustomerId, c.Name having count(*) > 1 order by Orders desc\r\n" +
            "insert into dbo.Audit (Id, Note) values (1, N'x')";
        internal Querywright.Core.WorkbenchSettings Settings { get; }
        private Querywright.Core.FormattingStyle Style => Settings.Formatting;

        internal FormattingStyleDialog(Querywright.Core.WorkbenchSettings current, string settingsPath)
        {
            // ponytail: copy through XML so the dialog never mutates the caller's settings until OK.
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(Querywright.Core.WorkbenchSettings));
            using (var buffer = new MemoryStream())
            {
                serializer.Serialize(buffer, current);
                buffer.Position = 0;
                Settings = (Querywright.Core.WorkbenchSettings)serializer.Deserialize(buffer);
            }
            Text = "Querywright: formatting style and rules (" + settingsPath + ")";
            Width = 1000; Height = 600;
            MinimumSize = new System.Drawing.Size(720, 420);
            Font = System.Drawing.SystemFonts.MessageBoxFont;
            AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi;
            Padding = new System.Windows.Forms.Padding(12);
            ShowInTaskbar = false;
            StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            var grid = new System.Windows.Forms.PropertyGrid { SelectedObject = Style, Dock = System.Windows.Forms.DockStyle.Fill, ToolbarVisible = false, AccessibleName = "Formatting options" };
            var preview = new System.Windows.Forms.TextBox
            {
                Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = System.Windows.Forms.ScrollBars.Both,
                Dock = System.Windows.Forms.DockStyle.Fill, Font = new System.Drawing.Font("Consolas", 10f), AccessibleName = "Formatted SQL preview"
            };
            var buttons = new System.Windows.Forms.FlowLayoutPanel { Dock = System.Windows.Forms.DockStyle.Bottom, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft, AutoSize = true, Padding = new System.Windows.Forms.Padding(0, 12, 0, 0) };
            var cancel = new System.Windows.Forms.Button { Text = "&Cancel", DialogResult = System.Windows.Forms.DialogResult.Cancel, AutoSize = true, MinimumSize = new System.Drawing.Size(90, 32) };
            var ok = new System.Windows.Forms.Button { Text = "&Save", DialogResult = System.Windows.Forms.DialogResult.OK, AutoSize = true, MinimumSize = new System.Drawing.Size(90, 32) };
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
            AcceptButton = ok; CancelButton = cancel;
            var split = new System.Windows.Forms.SplitContainer { Dock = System.Windows.Forms.DockStyle.Fill, Width = 950, SplitterDistance = 340, Panel1MinSize = 240, Panel2MinSize = 240 };
            split.Panel1.Controls.Add(grid); split.Panel2.Controls.Add(preview);
            var rules = new System.Windows.Forms.PropertyGrid { SelectedObject = Settings, Dock = System.Windows.Forms.DockStyle.Fill, ToolbarVisible = false, PropertySort = System.Windows.Forms.PropertySort.Alphabetical, AccessibleName = "Analysis rule severities" };
            var tabs = new System.Windows.Forms.TabControl { Dock = System.Windows.Forms.DockStyle.Fill, AccessibleName = "Formatting and analysis rules (Ctrl+Tab switches)" };
            tabs.TabPages.Add("Formatting"); tabs.TabPages.Add("Analysis rules");
            tabs.TabPages[0].Controls.Add(split); tabs.TabPages[1].Controls.Add(rules);
            Controls.Add(tabs); Controls.Add(buttons);
            Controls.Add(DialogParts.FormHeader("Formatting style and rules", "Formatting: adjust options on the left, review SQL on the right. Analysis rules: Disabled, Info, Warning or Error. Saved to the shared settings file."));
            DialogParts.Style(this);
            void Render()
            {
                if (Style.IndentSize < 1 || Style.IndentSize > 16) { preview.Text = "Indent size must be between 1 and 16."; ok.Enabled = false; return; }
                ok.Enabled = true;
                try { preview.Text = Querywright.Core.SqlFormatting.Format(Sample, Style); }
                catch (Exception error) when (!(error is OutOfMemoryException)) { preview.Text = error.Message; ok.Enabled = false; }
            }
            Load += (s, e) => ActiveControl = grid; // keyboard users start in the options, not on the tab strip
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
            DialogParts.Style(this);
            Width = 800; Height = 620; MinWidth = 440; MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            var root = new DockPanel { Margin = new Thickness(12) };
            root.Children.Add(DialogParts.Header(title, "Inspect the definition and " + (parameters ? "parameters" : "columns") + ". This view is read-only."));
            var status = DialogParts.Status();
            DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
            root.Children.Add(DialogParts.Footer(DialogParts.Buttons(this,
                ("_Copy script", true, false, () =>
                {
                    try { Clipboard.SetText(script); status.Text = "Copied script to clipboard."; }
                    catch (System.Runtime.InteropServices.ExternalException) { status.Text = "Clipboard is busy. Try copying again."; }
                }),
                ("_Close", false, true, () => Close()))));
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
            System.Windows.Automation.AutomationProperties.SetName(text, "Object SQL script");
            System.Windows.Automation.AutomationProperties.SetName(list, "Object columns or parameters");
            var tabs = new TabControl();
            tabs.Items.Add(new TabItem { Header = "Script", Content = text });
            tabs.Items.Add(new TabItem { Header = "Summary", Content = list });
            root.Children.Add(tabs);
            Content = root;
        }
    }
}

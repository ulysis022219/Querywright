using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Querywright.Ssms
{
    /// <summary>Choose the databases a script is generated for. Nothing is ticked unless it was ticked last time.</summary>
    internal sealed class DatabasePickerDialog : Window
    {
        private static readonly HashSet<string> SystemDatabases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "master", "model", "msdb", "tempdb" };

        internal IReadOnlyList<string> Selected { get; private set; } = Array.Empty<string>();
        internal bool StopOnError => stopOnError.IsChecked == true;
        internal bool PrintName => printName.IsChecked == true;

        private readonly CheckBox stopOnError = new CheckBox { Content = "_Stop on first error (SQLCMD mode)", Margin = new Thickness(0, 8, 0, 0) };
        private readonly CheckBox printName = new CheckBox { Content = "_Print each database name", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };

        internal DatabasePickerDialog(string server, IReadOnlyList<string> databases, ISet<string> previous, bool scriptHasUse, string purpose = null)
        {
            Title = purpose == null ? "Querywright: script for multiple databases" : "Querywright: " + purpose;
            DialogParts.Style(this);
            Width = 640; Height = 720; MinWidth = 480; MinHeight = 640;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var root = new DockPanel { Margin = new Thickness(12) };
            root.Children.Add(DialogParts.Header(purpose == null ? "Script across databases" : purpose,
                purpose == null ? "Build one script for your selected databases. Nothing runs until you execute it." : "Choose databases carefully. This operation runs on the server with your login."));

            var top = new StackPanel();
            top.Children.Add(new TextBlock { Text = "Server: " + server, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = server, FontWeight = FontWeights.SemiBold });
            if (scriptHasUse)
                top.Children.Add(new TextBlock { Text = "The script has its own USE statement, which overrides the database of each block.", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 6, 0, 0) });
            var filter = new TextBox { Margin = new Thickness(0, 4, 0, 4) };
            top.Children.Add(new Label { Content = "_Filter:", Target = filter, Padding = new Thickness(0, 8, 0, 0) });
            top.Children.Add(filter);
            var hideSystem = new CheckBox { Content = "_Hide system databases", IsChecked = true };
            top.Children.Add(hideSystem);
            DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);

            var bottom = new StackPanel();
            var count = DialogParts.Status();
            bottom.Children.Add(count);
            if (purpose == null)
            {
                bottom.Children.Add(stopOnError);
                bottom.Children.Add(new TextBlock { Text = "SQLCMD mode treats $(name) in your script as a variable.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 2, 0, 4) });
                bottom.Children.Add(printName);
            }
            var selection = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            var all = new Button { Content = "Select _all shown", MinWidth = 90, Margin = new Thickness(0, 4, 4, 4) };
            var none = new Button { Content = "Select _none shown", MinWidth = 90, Margin = new Thickness(4) };
            selection.Children.Add(all); selection.Children.Add(none); top.Children.Add(selection);
            var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var ok = new Button { Content = purpose == null ? "_Open script" : "_Run", MinWidth = 90, Margin = new Thickness(4), IsDefault = true };
            ok.ToolTip = purpose == null ? "Open the generated script in a new query window." : "Run against every selected database, including selections hidden by filters.";
            var cancel = new Button { Content = "_Cancel", MinWidth = 90, Margin = new Thickness(4), IsCancel = true };
            buttons.Children.Add(ok); buttons.Children.Add(cancel);
            bottom.Children.Add(DialogParts.Footer(buttons));
            DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);

            var items = databases.Select(name => new CheckBox { Content = new TextBlock { Text = name }, Tag = name, IsChecked = previous.Contains(name), Margin = new Thickness(2) }).ToList();
            var list = new ListBox { Margin = new Thickness(0, 6, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetName(list, "Databases");
            var empty = new TextBlock { Text = "No databases match. Clear the filter or show system databases.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
            var listArea = new Grid(); listArea.Children.Add(list); listArea.Children.Add(empty);
            root.Children.Add(listArea);
            Content = root;

            IEnumerable<CheckBox> Visible() => items.Where(i => i.Visibility == Visibility.Visible);
            void Count()
            {
                int n = items.Count(i => i.IsChecked == true);
                int hidden = items.Count(i => i.IsChecked == true && i.Visibility != Visibility.Visible);
                count.Text = n == 1 ? "1 database selected" : n + " databases selected";
                if (hidden > 0) count.Text += " (" + hidden + " hidden by filters)";
                ok.IsEnabled = n > 0;
            }
            void Filter()
            {
                string text = filter.Text.Trim();
                foreach (var item in items)
                {
                    string name = (string)item.Tag;
                    bool show = (hideSystem.IsChecked != true || !SystemDatabases.Contains(name)) && name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
                    item.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                }
                list.ItemsSource = Visible().ToList();
                empty.Visibility = list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                all.IsEnabled = none.IsEnabled = list.Items.Count > 0;
                Count();
            }
            foreach (var item in items) { item.Checked += (s, e) => Count(); item.Unchecked += (s, e) => Count(); }
            filter.TextChanged += (s, e) => Filter();
            hideSystem.Checked += (s, e) => Filter();
            hideSystem.Unchecked += (s, e) => Filter();
            // Select all/none act on the filtered list only, so "filter, then select all" picks just those.
            all.Click += (s, e) => { foreach (var item in Visible()) item.IsChecked = true; };
            none.Click += (s, e) => { foreach (var item in Visible()) item.IsChecked = false; };
            ok.Click += (s, e) =>
            {
                // Hidden but ticked databases are still scripted; the count says how many.
                Selected = items.Where(i => i.IsChecked == true).Select(i => (string)i.Tag).ToArray();
                DialogResult = true;
            };
            Loaded += (s, e) => filter.Focus();
            Filter();
            Count();
        }
    }
}

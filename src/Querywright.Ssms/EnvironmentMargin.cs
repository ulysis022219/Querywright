using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace Querywright.Ssms
{
    /// <summary>Colored strip above the query editor per server/database rule (SQL Prompt's tab coloring).</summary>
    [Export(typeof(IWpfTextViewMarginProvider))]
    [Name("QuerywrightEnvironment")]
    [MarginContainer(PredefinedMarginNames.Top)]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class EnvironmentMarginProvider : IWpfTextViewMarginProvider
    {
        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost host, IWpfTextViewMargin container) =>
            EditorListener.IsSql(host.TextView.TextBuffer.ContentType) ? new EnvironmentMargin(host.TextView, "QuerywrightEnvironment", false) : null;
    }

    /// <summary>"server · database" at the bottom right of the query editor, colored by the same rules.</summary>
    [Export(typeof(IWpfTextViewMarginProvider))]
    [Name("QuerywrightConnection")]
    [MarginContainer(PredefinedMarginNames.Bottom)]
    [Order(After = PredefinedMarginNames.BottomControl)]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class ConnectionMarginProvider : IWpfTextViewMarginProvider
    {
        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost host, IWpfTextViewMargin container) =>
            EditorListener.IsSql(host.TextView.TextBuffer.ContentType) ? new EnvironmentMargin(host.TextView, "QuerywrightConnection", true) : null;
    }

    internal sealed class EnvironmentMargin : Border, IWpfTextViewMargin
    {
        private readonly IWpfTextView view;
        private readonly string name;
        private readonly bool label;
        private readonly TextBlock text;
        // Picks up database changes (USE, toolbar dropdown) while the editor has focus.
        private readonly System.Windows.Threading.DispatcherTimer timer =
            new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };

        internal EnvironmentMargin(IWpfTextView view, string name, bool label)
        {
            this.view = view;
            this.name = name;
            this.label = label;
            Height = 0;
            if (label)
            {
                text = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(6, 1, 6, 1), Opacity = 0.85 };
                Child = text;
            }
            timer.Tick += (sender, args) => Update();
            view.GotAggregateFocus += (sender, args) => { Update(); timer.Start(); };
            view.LostAggregateFocus += (sender, args) => timer.Stop();
            view.Closed += (sender, args) => timer.Stop();
            // The active window's connection is read, so only the focused editor updates itself.
            Loaded += (sender, args) => { Update(); if (view.HasAggregateFocus) timer.Start(); };
        }

        /// <summary>Rules are "pattern=color" pairs separated by ';', matched against "server/database", first match wins.</summary>
        internal static Color? Match(string rules, string server, string database)
        {
            if (string.IsNullOrWhiteSpace(rules) || server == null) return null;
            string target = server + "/" + database;
            foreach (var rule in rules.Split(';').Select(r => r.Split('=')).Where(r => r.Length == 2 && r[0].Trim().Length > 0))
            {
                if (target.IndexOf(rule[0].Trim(), StringComparison.OrdinalIgnoreCase) < 0) continue;
                try { return (Color)ColorConverter.ConvertFromString(rule[1].Trim()); }
                catch (FormatException) { return null; }
            }
            return null;
        }

        private void Update()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var package = WorkbenchPackage.Instance;
            var connection = LiveMetadata.CaptureNames();
            var color = Match(package?.TabColorRules, connection?.Server, connection?.Database);
            string caption = connection == null ? null
                : connection.Value.Server + (string.IsNullOrEmpty(connection.Value.Database) ? "" : " \u00B7 " + connection.Value.Database);
            if (!label)
            {
                Height = color == null ? 0 : 5;
                Background = color == null ? null : new SolidColorBrush(color.Value);
                ToolTip = color == null ? null : caption;
                return;
            }
            bool show = caption != null && package?.ShowConnection != false;
            Height = show ? double.NaN : 0;
            text.Text = show ? caption : "";
            view.Properties["QuerywrightConnection"] = text.Text; // read by the e2e self-test
            Background = show && color != null ? new SolidColorBrush(color.Value) : null;
            if (show && color != null)
            {
                var c = color.Value;
                text.Foreground = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 150 ? Brushes.Black : Brushes.White;
                // High Contrast: the server color stays as the band around the text, the text itself uses the theme colors.
                if (SystemParameters.HighContrast) { text.Foreground = SystemColors.WindowTextBrush; text.Background = SystemColors.WindowBrush; text.Opacity = 1; }
                else text.ClearValue(TextBlock.BackgroundProperty);
            }
            else { text.ClearValue(TextBlock.ForegroundProperty); text.ClearValue(TextBlock.BackgroundProperty); }
        }

        public FrameworkElement VisualElement => this;
        public double MarginSize => ActualHeight;
        public bool Enabled => true;
        public ITextViewMargin GetTextViewMargin(string marginName) => marginName == name ? this : null;
        public void Dispose() => timer.Stop();
    }
}

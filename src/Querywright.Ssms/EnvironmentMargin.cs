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
            EditorListener.IsSql(host.TextView.TextBuffer.ContentType) ? new EnvironmentMargin(host.TextView) : null;
    }

    internal sealed class EnvironmentMargin : Border, IWpfTextViewMargin
    {
        internal EnvironmentMargin(IWpfTextView view)
        {
            Height = 0;
            view.GotAggregateFocus += (sender, args) => Update();
            Loaded += (sender, args) => Update();
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
            var connection = LiveMetadata.Capture();
            var color = Match(WorkbenchPackage.Instance?.TabColorRules, connection?.Server, connection?.Database);
            Height = color == null ? 0 : 5;
            Background = color == null ? null : new SolidColorBrush(color.Value);
            ToolTip = color == null ? null : connection.Server + " / " + connection.Database;
        }

        public FrameworkElement VisualElement => this;
        public double MarginSize => ActualHeight;
        public bool Enabled => true;
        public ITextViewMargin GetTextViewMargin(string marginName) => marginName == "QuerywrightEnvironment" ? this : null;
        public void Dispose() { }
    }
}

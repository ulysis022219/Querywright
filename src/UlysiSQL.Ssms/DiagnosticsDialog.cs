using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.Win32;

namespace UlysiSQL.Ssms
{
    internal sealed class DiagnosticsDialog : Window
    {
        private string snapshot;
        private bool imported;
        private readonly TextBox view = new TextBox { IsReadOnly = true, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 13 };
        private readonly TextBlock status = DialogParts.Status();

        internal DiagnosticsDialog()
        {
            Title = "UlysiSQL: diagnostic logs";
            DialogParts.Style(this);
            Width = 900; Height = 650; MinWidth = 460; MinHeight = 320;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var root = new DockPanel { Margin = new Thickness(12) };
            root.Children.Add(DialogParts.Header("Diagnostic logs", "Enable Verbose diagnostics under Tools > Options > UlysiSQL > General, reproduce the problem, then export. Queries and results may contain sensitive data. Review before sharing.", Icons.Logs));
            var bottom = new StackPanel(); bottom.Children.Add(status);
            bottom.Children.Add(DialogParts.Footer(DialogParts.Buttons(this,
                ("_Refresh", false, false, Refresh), ("_Import logs", false, false, Import), ("_Export logs", false, false, Export), ("_Close", false, true, Close))));
            DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
            view.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ComboBoxBackgroundBrushKey);
            view.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            AutomationProperties.SetName(view, "Diagnostic logs. Read-only; imported logs do not change settings or run queries.");
            root.Children.Add(view); Content = root; Refresh();
        }

        private void Refresh()
        {
            imported = false; snapshot = Diagnostics.Export(); view.Text = Diagnostics.FormatDisplay(snapshot);
            status.Text = Diagnostics.Enabled ? "Verbose capture on. Current session; oldest entries expire when log limit is reached." : "Verbose capture off. Enable it before reproducing the problem.";
        }

        private void Import()
        {
            var picker = new OpenFileDialog { Title = "Import UlysiSQL diagnostic logs", Filter = "UlysiSQL diagnostic logs (*.json)|*.json", CheckFileExists = true };
            if (picker.ShowDialog(this) != true) return;
            try
            {
                using (var stream = new FileStream(picker.FileName, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length > Diagnostics.MaxJsonBytes) throw new FormatException("Diagnostic file is too large.");
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true)) snapshot = Diagnostics.ValidateImport(reader.ReadToEnd());
                }
                view.Text = Diagnostics.FormatDisplay(snapshot);
                imported = true;
                status.Text = "Imported logs. Read-only view; capture and settings unchanged. Refresh returns to current session.";
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is FormatException)
            { status.Text = "Could not import logs: " + Diagnostics.Redact(error.Message); }
        }

        private void Export()
        {
            if (!imported) Refresh();
            var picker = new SaveFileDialog { Title = "Export UlysiSQL diagnostic logs", Filter = "UlysiSQL diagnostic logs (*.json)|*.json", DefaultExt = ".json",
                FileName = "UlysiSQL-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json", OverwritePrompt = true };
            if (picker.ShowDialog(this) != true) return;
            try { File.WriteAllText(picker.FileName, snapshot, new UTF8Encoding(false)); status.Text = "Logs exported. Review sensitive query and result data before sharing."; }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            { status.Text = "Could not export logs: " + Diagnostics.Redact(error.Message); }
        }
    }
}

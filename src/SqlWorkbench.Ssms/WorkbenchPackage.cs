using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;
using SqlWorkbench.Core;

namespace SqlWorkbench.Ssms
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideOptionPage(typeof(WorkbenchOptions), "SqlWorkbench", "General", 0, 0, true)]
    [Guid("a13c1b0c-af94-4f53-8d06-edf816e39450")]
    public sealed class WorkbenchPackage : AsyncPackage
    {
        private IComponentModel components;
        private IVsTextManager textManager;
        private ErrorListProvider errorList;
        private CancellationTokenSource analysisCancellation;
        protected override async Task InitializeAsync(CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var commands = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commands == null) throw new InvalidOperationException("SSMS command service unavailable.");
            var componentServices = await GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
            if (componentServices == null) throw new InvalidOperationException("SSMS component services unavailable.");
            components = componentServices;
            textManager = await GetServiceAsync(typeof(SVsTextManager)) as IVsTextManager;
            if (textManager == null) throw new InvalidOperationException("SSMS text services unavailable.");
            errorList = new ErrorListProvider(this) { ProviderName = "SqlWorkbench", ProviderGuid = new Guid("c493165c-47d9-43d7-b28b-d2d7144d45ac") };
            commands.AddCommand(new MenuCommand(ShowSnippetCheck,
                new CommandID(new Guid("b48a692b-82fb-47cf-bfc9-bdf13483d6c7"), 0x0100)));
            commands.AddCommand(new MenuCommand(InsertSnippet,
                new CommandID(new Guid("b48a692b-82fb-47cf-bfc9-bdf13483d6c7"), 0x0101)));
            commands.AddCommand(new MenuCommand((sender, args) => { _ = JoinableTaskFactory.RunAsync(AnalyzeDocumentAsync); },
                new CommandID(new Guid("b48a692b-82fb-47cf-bfc9-bdf13483d6c7"), 0x0102)));
            commands.AddCommand(new MenuCommand((sender, args) => { _ = JoinableTaskFactory.RunAsync(FormatDocumentAsync); },
                new CommandID(new Guid("b48a692b-82fb-47cf-bfc9-bdf13483d6c7"), 0x0103)));
            commands.AddCommand(new MenuCommand((sender, args) => { _ = JoinableTaskFactory.RunAsync(CompleteAsync); },
                new CommandID(new Guid("b48a692b-82fb-47cf-bfc9-bdf13483d6c7"), 0x0104)));
            commands.AddCommand(new MenuCommand((sender, args) => { _ = JoinableTaskFactory.RunAsync(RenameVariableAsync); },
                new CommandID(new Guid("b48a692b-82fb-47cf-bfc9-bdf13483d6c7"), 0x0105)));
        }

        private async Task RenameVariableAsync()
        {
            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var view = GetSqlView();
                if (!view.Selection.IsEmpty || view.Caret.InVirtualSpace)
                    throw new InvalidOperationException("Place the caret on a local variable without selecting text.");
                var snapshot = view.TextSnapshot;
                var dialog = new RenameVariableDialog(snapshot.GetText(), view.Caret.Position.BufferPosition.Position);
                var shell = await GetServiceAsync(typeof(SVsUIShell)) as IVsUIShell;
                if (shell == null) throw new InvalidOperationException("SSMS window service unavailable.");
                ErrorHandler.ThrowOnFailure(shell.GetDialogOwnerHwnd(out var owner));
                new System.Windows.Interop.WindowInteropHelper(dialog).Owner = owner;
                if (dialog.ShowDialog() != true || dialog.Result == null) return;
                ReplaceText(view, new SnapshotSpan(snapshot, 0, snapshot.Length), dialog.Result.Text, 0, 0, 0, "Rename local variable");
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                ShowWarning(error.Message);
            }
        }

        private async Task CompleteAsync()
        {
            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var view = GetSqlView();
                if (!view.Selection.IsEmpty || view.Caret.InVirtualSpace)
                    throw new InvalidOperationException("Place the caret within a SQL identifier without selecting text.");
                var snapshot = view.TextSnapshot;
                int position = view.Caret.Position.BufferPosition.Position;
                string sql = snapshot.GetText();
                string path = ((WorkbenchOptions)GetDialogPage(typeof(WorkbenchOptions))).SchemaFile;
                if (string.IsNullOrWhiteSpace(path))
                    throw new InvalidOperationException("Set an offline schema SQL file under Tools > Options > SqlWorkbench. Live metadata integration is pending.");
                var result = await Task.Run(() =>
                {
                    if (new FileInfo(path).Length > 4_000_000) throw new IOException("Schema file exceeds 4 MB.");
                    return SqlCompletion.Complete(sql, position, SchemaCatalog.FromDdl(File.ReadAllText(path)));
                });
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                if (view.IsClosed || view.TextSnapshot != snapshot) throw new InvalidOperationException("Query changed. Request suggestions again.");
                if (result.Items.Count == 0)
                {
                    ShowWarning(string.IsNullOrEmpty(result.Limitation) ? "No matching columns or tables in the configured offline schema." : result.Limitation);
                    return;
                }
                var picker = new CompletionPicker(result.Items);
                var shell = await GetServiceAsync(typeof(SVsUIShell)) as IVsUIShell;
                if (shell == null) throw new InvalidOperationException("SSMS window service unavailable.");
                ErrorHandler.ThrowOnFailure(shell.GetDialogOwnerHwnd(out var owner));
                new System.Windows.Interop.WindowInteropHelper(picker).Owner = owner;
                if (picker.ShowDialog() != true || picker.Selected == null) return;
                string text = picker.Selected.InsertText;
                ReplaceText(view, new SnapshotSpan(snapshot, result.Start, result.Length), text, text.Length, 0, 0, "Complete SQL identifier");
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                ShowWarning(error.Message);
            }
        }

        private async Task<WorkbenchSettings> ReadSettingsAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            string path = ((WorkbenchOptions)GetDialogPage(typeof(WorkbenchOptions))).SettingsFile;
            return string.IsNullOrWhiteSpace(path) ? new WorkbenchSettings() : await Task.Run(() => WorkbenchSettings.Load(path));
        }

        private async Task FormatDocumentAsync()
        {
            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var view = GetSqlView();
                if (view.Selection.Mode != TextSelectionMode.Stream)
                    throw new InvalidOperationException("Use a normal selection for formatting.");
                var span = view.Selection.IsEmpty ? new SnapshotSpan(view.TextSnapshot, 0, view.TextSnapshot.Length)
                    : view.Selection.StreamSelectionSpan.SnapshotSpan;
                string original = span.GetText();
                var style = (await ReadSettingsAsync()).Formatting;
                var formatted = await Task.Run(() => SqlFormatting.Format(original, style));
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                if (formatted == original) return;
                ReplaceText(view, span, formatted, 0, 0, formatted.Length, "Format SQL");
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                ShowWarning(error.Message);
            }
        }

        private void ReplaceText(IWpfTextView view, SnapshotSpan span, string text, int caret, int selectionStart, int selectionLength, string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (view.IsClosed || view.TextSnapshot != span.Snapshot)
                throw new InvalidOperationException("Query changed during the operation. Retry.");
            var history = components.GetService<ITextUndoHistoryRegistry>().RegisterHistory(view.TextBuffer);
            var operations = components.GetService<IEditorOperationsFactoryService>().GetEditorOperations(view);
            using (var transaction = history.CreateTransaction(name))
            {
                operations.AddBeforeTextBufferChangePrimitive();
                using (var edit = view.TextBuffer.CreateEdit())
                {
                    if (!edit.Replace(span.Span, text)) throw new InvalidOperationException("Selected SQL text is read-only.");
                    edit.Apply();
                    if (edit.Canceled) throw new InvalidOperationException("SQL edit was canceled.");
                }
                var after = view.TextSnapshot;
                int start = span.Start.Position;
                view.Selection.Clear();
                view.Caret.MoveTo(new SnapshotPoint(after, start + caret));
                if (selectionLength > 0)
                    view.Selection.Select(new SnapshotSpan(after, start + selectionStart, selectionLength), false);
                operations.AddAfterTextBufferChangePrimitive();
                transaction.Complete();
            }
            view.VisualElement.Focus();
            view.Caret.EnsureVisible();
        }

        private IWpfTextView GetSqlView()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (textManager == null || components == null)
                throw new InvalidOperationException("SSMS editor services unavailable.");
            ErrorHandler.ThrowOnFailure(textManager.GetActiveView(1, null, out var adapter));
            var view = adapter == null ? null : components.GetService<IVsEditorAdaptersFactoryService>().GetWpfTextView(adapter);
            if (view == null || view.IsClosed) throw new InvalidOperationException("Open a SQL query editor first.");
            if (!view.TextBuffer.ContentType.IsOfType("SQL") && !view.TextBuffer.ContentType.IsOfType("T-SQL"))
                throw new InvalidOperationException("Active editor is not a recognized SQL buffer.");
            return view;
        }

        private async Task AnalyzeDocumentAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            analysisCancellation?.Cancel();
            var cancellation = new CancellationTokenSource();
            analysisCancellation = cancellation;
            try
            {
                var view = GetSqlView();
                var snapshot = view.TextSnapshot;
                string sql = snapshot.GetText();
                var settings = await ReadSettingsAsync();
                var result = await Task.Run(() => SqlAnalysis.Analyze(sql, cancellation.Token, settings), cancellation.Token);
                await JoinableTaskFactory.SwitchToMainThreadAsync(cancellation.Token);
                if (view.IsClosed || view.TextSnapshot != snapshot)
                    throw new InvalidOperationException("Query changed during analysis. Run analysis again.");
                errorList.Tasks.Clear();
                foreach (var issue in result.Diagnostics)
                {
                    var task = new ErrorTask
                    {
                        Text = issue.Rule + ": " + issue.Message,
                        Line = Math.Max(0, issue.Line - 1), Column = Math.Max(0, issue.Column - 1),
                        Category = TaskCategory.CodeSense,
                        ErrorCategory = settings.Severity(issue.Rule) == RuleSeverity.Error ? TaskErrorCategory.Error
                            : settings.Severity(issue.Rule) == RuleSeverity.Info ? TaskErrorCategory.Message : TaskErrorCategory.Warning
                    };
                    task.Navigate += (sender, args) =>
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();
                        if (view.IsClosed || view.TextSnapshot != snapshot)
                        {
                            ShowWarning("Analysis is stale. Run analysis again.");
                            return;
                        }
                        view.Selection.Select(new SnapshotSpan(snapshot, issue.Offset, issue.Length), false);
                        view.Caret.MoveTo(new SnapshotPoint(snapshot, issue.Offset));
                        view.VisualElement.Focus();
                        view.Caret.EnsureVisible();
                    };
                    errorList.Tasks.Add(task);
                }
                errorList.Show();
                var status = await GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
                status?.SetText($"SqlWorkbench: {result.Diagnostics.Count} diagnostics from four implemented rules; full analysis coverage pending.");
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                ShowWarning(error.Message);
            }
            finally
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                if (analysisCancellation == cancellation) analysisCancellation = null;
                cancellation.Dispose();
            }
        }

        private void ShowWarning(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VsShellUtilities.ShowMessageBox(this, message, "SqlWorkbench",
                OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                analysisCancellation?.Cancel();
                errorList?.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InsertSnippet(object sender, EventArgs args)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var view = GetSqlView();
                if (view.Selection.Mode != TextSelectionMode.Stream || view.Caret.InVirtualSpace)
                    throw new InvalidOperationException("Use a normal selection and place the caret within the SQL text.");
                var before = view.TextSnapshot;
                var selected = view.Selection.StreamSelectionSpan.SnapshotSpan;
                var options = (WorkbenchOptions)GetDialogPage(typeof(WorkbenchOptions));
                SnippetFiles.Initialize(options.SnippetFolder);
                var picker = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Insert SQL snippet", Filter = "SQL snippets (*.sql)|*.sql",
                    InitialDirectory = options.SnippetFolder, CheckFileExists = true, Multiselect = false
                };
                if (picker.ShowDialog() != true) return;
                string template = SnippetFiles.Read(picker.FileName);
                var context = new Dictionary<string, string> { ["MACHINE"] = Environment.MachineName };
                if (template.Contains("$PASTE$")) context["PASTE"] = System.Windows.Clipboard.GetText();
                var expansion = Snippets.Expand(template, context, DateTimeOffset.Now);
                if (view.IsClosed || view.TextSnapshot != before)
                    throw new InvalidOperationException("Query changed while choosing the snippet. Retry insertion.");
                ReplaceText(view, selected, expansion.Text, expansion.Caret, expansion.SelectionStart,
                    expansion.SelectionLength, "Insert SQL snippet");
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
                error is ArgumentException || error is FormatException || error is InvalidOperationException || error is COMException)
            {
                VsShellUtilities.ShowMessageBox(this, error.Message, "SqlWorkbench snippet insertion",
                    OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
        }

        private void ShowSnippetCheck(object sender, EventArgs args)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var expansion = Snippets.Expand("SELECT $CURSOR$; -- $DATE$",
                new Dictionary<string, string>(), DateTimeOffset.Now);
            VsShellUtilities.ShowMessageBox(this,
                "Snippet core loaded. Preview only; editor unchanged.\n\n" + expansion.Text,
                "SqlWorkbench integration check", OLEMSGICON.OLEMSGICON_INFO,
                OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
    }
}

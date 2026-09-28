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
using Querywright.Core;

namespace Querywright.Ssms
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.ShellInitialized_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideOptionPage(typeof(WorkbenchOptions), "Querywright", "General", 0, 0, true)]
    [Guid("a13c1b0c-af94-4f53-8d06-edf816e39450")]
    public sealed class WorkbenchPackage : AsyncPackage
    {
        private IComponentModel components;
        private IVsTextManager textManager;
        private ErrorListProvider errorList;
        private CancellationTokenSource analysisCancellation;
        private WorkbenchOptions options;
        private readonly object schemaLock = new object();
        private (string Path, DateTime Stamp, IReadOnlyList<SchemaTable> Tables)? schemaCache;

        /// <summary>Set after initialization; editor MEF components reach package services through it.</summary>
        internal static WorkbenchPackage Instance { get; private set; }

        internal bool SchemaConfigured => !string.IsNullOrWhiteSpace(options?.SchemaFile);

        /// <summary>Offline schema, reparsed only when the file changes. Null when none is configured. Thread-safe.</summary>
        internal IReadOnlyList<SchemaTable> LoadSchema()
        {
            string path = options?.SchemaFile;
            if (string.IsNullOrWhiteSpace(path)) return null;
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("Offline schema file not found: " + path);
            if (info.Length > 4_000_000) throw new IOException("Schema file exceeds 4 MB.");
            lock (schemaLock)
            {
                var cache = schemaCache;
                if (cache.HasValue && cache.Value.Path == path && cache.Value.Stamp == info.LastWriteTimeUtc) return cache.Value.Tables;
                var loaded = SchemaCatalog.FromDdl(File.ReadAllText(path));
                schemaCache = (path, info.LastWriteTimeUtc, loaded);
                return loaded;
            }
        }
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
            options = (WorkbenchOptions)GetDialogPage(typeof(WorkbenchOptions));
            errorList = new ErrorListProvider(this) { ProviderName = "Querywright", ProviderGuid = new Guid("c493165c-47d9-43d7-b28b-d2d7144d45ac") };
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
            commands.AddCommand(new MenuCommand((sender, args) => { _ = JoinableTaskFactory.RunAsync(AddSemicolonsAsync); },
                new CommandID(new Guid("b48a692b-82fb-47cf-bfc9-bdf13483d6c7"), 0x0106)));
            commands.AddCommand(new MenuCommand((sender, args) => { _ = JoinableTaskFactory.RunAsync(ExpandWildcardAsync); },
                new CommandID(new Guid("b48a692b-82fb-47cf-bfc9-bdf13483d6c7"), 0x0107)));
            Instance = this;
        }

        /// <summary>Tab after a snippet shortcut (ssf) or after * expands in place. False passes Tab to the editor.</summary>
        internal bool TryTabExpand(IWpfTextView view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (view.IsClosed || !view.Selection.IsEmpty || view.Caret.InVirtualSpace) return false;
            try
            {
                var snapshot = view.TextSnapshot;
                int caret = view.Caret.Position.BufferPosition.Position;
                var line = view.Caret.Position.BufferPosition.GetContainingLine();
                if (caret > 0 && snapshot[caret - 1] == '*' && SchemaConfigured)
                {
                    TextEdit edit;
                    try { edit = SqlCompletion.ExpandWildcard(snapshot.GetText(), caret - 1, LoadSchema()); }
                    catch (FormatException) { return false; } // Incomplete SQL while typing: ordinary Tab.
                    ReplaceText(view, new SnapshotSpan(snapshot, edit.Start, edit.Length), edit.Text, edit.Text.Length, 0, 0, "Expand wildcard");
                    return true;
                }
                string shortcut = SnippetFiles.ShortcutBefore(line.GetText(), caret - line.Start.Position);
                if (shortcut == null) return false;
                if (!Directory.Exists(options.SnippetFolder)) SnippetFiles.Initialize(options.SnippetFolder);
                // ponytail: snippet files are small local reads; kept on the UI thread so Tab stays ordered with typing.
                string path = SnippetFiles.FindShortcut(options.SnippetFolder, shortcut);
                if (path == null) return false;
                string template = SnippetFiles.Read(path);
                var context = new Dictionary<string, string> { ["MACHINE"] = Environment.MachineName };
                if (template.Contains("$PASTE$")) context["PASTE"] = System.Windows.Clipboard.GetText();
                var expansion = Snippets.Expand(template, context, DateTimeOffset.Now);
                ReplaceText(view, new SnapshotSpan(snapshot, caret - shortcut.Length, shortcut.Length), expansion.Text,
                    expansion.Caret, expansion.SelectionStart, expansion.SelectionLength, "Expand snippet " + shortcut);
                return true;
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                ShowWarning(error.Message);
                return true;
            }
        }

        /// <summary>F12 on variables, aliases and CTEs jumps in the script; database objects fall through to SSMS's own definition.</summary>
        internal bool TryGoToDefinition(IWpfTextView view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (view.IsClosed) return false;
            DefinitionTarget target;
            try { target = SqlNavigation.FindDefinition(view.TextSnapshot.GetText(), view.Caret.Position.BufferPosition.Position); }
            catch (FormatException) { return false; }
            if (target == null || target.Offset < 0) return false;
            var snapshot = view.TextSnapshot;
            view.Selection.Select(new SnapshotSpan(snapshot, target.Offset, target.Length), false);
            view.Caret.MoveTo(new SnapshotPoint(snapshot, target.Offset));
            view.Caret.EnsureVisible();
            return true;
        }

        private async Task AddSemicolonsAsync()
        {
            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var view = GetSqlView();
                var snapshot = view.TextSnapshot;
                string sql = snapshot.GetText();
                string result = await Task.Run(() => SqlRefactoring.AddSemicolons(sql));
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                if (result == sql) return;
                ReplaceText(view, new SnapshotSpan(snapshot, 0, snapshot.Length), result, 0, 0, 0, "Insert semicolons");
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                ShowWarning(error.Message);
            }
        }

        private async Task ExpandWildcardAsync()
        {
            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var view = GetSqlView();
                if (!view.Selection.IsEmpty || view.Caret.InVirtualSpace)
                    throw new InvalidOperationException("Place the caret on * without selecting text.");
                var snapshot = view.TextSnapshot;
                string sql = snapshot.GetText();
                int position = view.Caret.Position.BufferPosition.Position;
                if (!SchemaConfigured)
                    throw new InvalidOperationException("Set an offline schema SQL file under Tools > Options > Querywright. Live metadata integration is pending.");
                var edit = await Task.Run(() => SqlCompletion.ExpandWildcard(sql, position, LoadSchema()));
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                ReplaceText(view, new SnapshotSpan(snapshot, edit.Start, edit.Length), edit.Text, edit.Text.Length, 0, 0, "Expand wildcard");
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                ShowWarning(error.Message);
            }
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
                if (!SchemaConfigured)
                    throw new InvalidOperationException("Set an offline schema SQL file under Tools > Options > Querywright. Live metadata integration is pending.");
                var result = await Task.Run(() => SqlCompletion.Complete(sql, position, LoadSchema()));
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
            string path = options.SettingsFile;
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
                status?.SetText($"Querywright: {result.Diagnostics.Count} diagnostics from four implemented rules; full analysis coverage pending.");
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
            VsShellUtilities.ShowMessageBox(this, message, "Querywright",
                OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (Instance == this) Instance = null;
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
                VsShellUtilities.ShowMessageBox(this, error.Message, "Querywright snippet insertion",
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
                "Querywright integration check", OLEMSGICON.OLEMSGICON_INFO,
                OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
    }
}

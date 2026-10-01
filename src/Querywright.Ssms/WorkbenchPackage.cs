using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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
        private volatile Tuple<string, DateTime, IReadOnlyList<SchemaTable>> schemaCache;
        private int schemaLoading;

        /// <summary>Set after initialization; editor MEF components reach package services through it.</summary>
        internal static WorkbenchPackage Instance { get; private set; }

        /// <summary>Editors can open before the background autoload finishes; load synchronously on first SQL editor.</summary>
        internal static void EnsureLoaded()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Instance != null) return;
            var shell = Package.GetGlobalService(typeof(SVsShell)) as IVsShell;
            var id = typeof(WorkbenchPackage).GUID;
            shell?.LoadPackage(ref id, out _);
        }

        internal string TabColorRules => options?.TabColorRules ?? "";

        internal void SetTabColorRules(string rules)
        {
            if (options == null) return;
            options.TabColorRules = rules;
            options.SaveSettingsToStorage();
        }
        internal bool ShowConnection => options?.ShowConnection ?? true;

        internal string SettingsFile => options?.SettingsFile ?? "";

        internal bool LiveMetadataEnabled => options?.LiveMetadata != false;

        internal bool SchemaConfigured => !string.IsNullOrWhiteSpace(options?.SchemaFile);

        /// <summary>Live metadata merged over the offline schema; null when neither is available. Never blocks or throws (typing path).</summary>
        internal IReadOnlyList<SchemaTable> CurrentTables()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var live = options?.LiveMetadata != false ? LiveMetadata.TryGet(LiveMetadata.Capture()) : null;
            return Merge(live, OfflineSchemaNow());
        }

        /// <summary>The offline schema as last parsed, without touching the disk; a background reload picks up file changes.</summary>
        private IReadOnlyList<SchemaTable> OfflineSchemaNow()
        {
            string path = options?.SchemaFile;
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (Interlocked.Exchange(ref schemaLoading, 1) == 0)
                _ = Task.Run(() =>
                {
                    try { LoadSchema(); }
                    catch (Exception error) when (!(error is OutOfMemoryException)) { schemaCache = null; } // deleted or broken: stop suggesting it
                    finally { Interlocked.Exchange(ref schemaLoading, 0); }
                });
            var cache = schemaCache;
            return cache != null && cache.Item1 == path ? cache.Item3 : null;
        }

        /// <summary>Databases on the connected server for USE. Never blocks (typing path).</summary>
        internal IReadOnlyList<string> CurrentDatabases()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return options?.LiveMetadata != false ? LiveMetadata.DatabaseNames(LiveMetadata.Capture()) : null;
        }

        /// <summary>Catalog lookup for other databases on the connection, for OtherDb. completion. Never blocks (typing path).</summary>
        internal Func<string, IReadOnlyList<SchemaTable>> CurrentOtherDatabase()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var connection = options?.LiveMetadata != false ? LiveMetadata.Capture() : null;
            return connection == null ? null : name => LiveMetadata.TryGet(connection.WithDatabase(name));
        }

        /// <summary>Live procedures plus those created in the script itself. Never blocks (typing path).</summary>
        internal IReadOnlyList<SchemaProcedure> CurrentProcedures(string sql)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var live = options?.LiveMetadata != false ? LiveMetadata.Procedures(LiveMetadata.Capture()) : null;
            IReadOnlyList<SchemaProcedure> local;
            try { local = sql.Length > 1_000_000 || sql.IndexOf("CREATE", StringComparison.OrdinalIgnoreCase) < 0 ? Array.Empty<SchemaProcedure>() : SqlAssist.ProceduresFromScript(sql); }
            catch (Exception error) when (!(error is OutOfMemoryException)) { local = Array.Empty<SchemaProcedure>(); }
            // Script definitions first: they are what the user is editing and they carry parameter defaults.
            return live == null ? local : local.Concat(live).ToArray();
        }

        /// <summary>For explicit commands: waits for live metadata and reports offline schema errors.</summary>
        private async Task<IReadOnlyList<SchemaTable>> RequireTablesAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var connection = options?.LiveMetadata != false ? LiveMetadata.Capture() : null;
            var live = await LiveMetadata.GetAsync(connection, TimeSpan.FromSeconds(20));
            IReadOnlyList<SchemaTable> offline;
            // A missing or broken offline file only matters when there is no live metadata to use instead.
            try { offline = await Task.Run(LoadSchema); }
            catch (Exception error) when (live != null && !(error is OutOfMemoryException)) { offline = null; }
            var tables = Merge(live, offline);
            if (tables == null)
                throw new InvalidOperationException("Connect the query window to a database, or set an offline schema SQL file under Tools > Options > Querywright.");
            return tables;
        }

        private static IReadOnlyList<SchemaTable> Merge(IReadOnlyList<SchemaTable> live, IReadOnlyList<SchemaTable> offline)
        {
            if (live == null || offline == null) return live ?? offline;
            var names = new HashSet<string>(live.Select(t => t.Schema + "." + t.Name), StringComparer.OrdinalIgnoreCase);
            return live.Concat(offline.Where(t => !names.Contains(t.Schema + "." + t.Name))).ToArray();
        }

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
                if (cache != null && cache.Item1 == path && cache.Item2 == info.LastWriteTimeUtc) return cache.Item3;
                var loaded = SchemaCatalog.FromDdl(File.ReadAllText(path));
                schemaCache = Tuple.Create(path, info.LastWriteTimeUtc, loaded);
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
            GridTotals.Start(() => options?.GridTotals != false, text => (GetService(typeof(SVsStatusbar)) as IVsStatusbar)?.SetText(text));
            void Add(int id, Func<Task> handler) => commands.AddCommand(new MenuCommand((sender, args) => { _ = JoinableTaskFactory.RunAsync(handler); },
                new CommandID(new Guid("b48a692b-82fb-47cf-bfc9-bdf13483d6c7"), id)));
            void AddGrid(int id, Func<bool> shown, Func<Task> handler)
            {
                var command = new OleMenuCommand((sender, args) => { _ = JoinableTaskFactory.RunAsync(handler); },
                    new CommandID(new Guid("b48a692b-82fb-47cf-bfc9-bdf13483d6c7"), id));
                command.BeforeQueryStatus += (sender, args) => ((OleMenuCommand)sender).Visible = shown();
                commands.AddCommand(command);
            }
            Add(0x0101, InsertSnippetAsync);
            Add(0x0102, AnalyzeDocumentAsync);
            Add(0x0103, FormatDocumentAsync);
            Add(0x0104, CompleteAsync);
            Add(0x0105, RenameVariableAsync);
            Add(0x0106, AddSemicolonsAsync);
            Add(0x0107, ExpandWildcardAsync);
            Add(0x0108, () => TransformAsync(sql => SqlRefactoring.ApplyCasing(sql), "Apply casing"));
            Add(0x0109, () => TransformAsync(SqlRefactoring.AddBrackets, "Add square brackets"));
            Add(0x010A, () => TransformAsync(SqlRefactoring.RemoveBrackets, "Remove square brackets"));
            Add(0x010B, () => TransformAsync(sql => SqlRefactoring.QualifyObjectNames(sql), "Qualify object names"));
            Add(0x010C, RenameAliasAsync);
            Add(0x010D, () => ListAsync(SqlRefactoring.Summarize, "statements", TaskErrorCategory.Message));
            Add(0x010E, () => ListAsync(SqlRefactoring.UnusedDeclarationItems, "unused declarations", TaskErrorCategory.Warning));
            Add(0x010F, RefreshMetadataAsync);
            Add(0x0110, ExecuteCurrentStatementAsync);
            Add(0x0111, PickColumnsAsync);
            Add(0x0112, TabHistoryAsync);
            Add(0x0113, RenameObjectAsync);
            Add(0x0114, EncapsulateAsync);
            Add(0x0115, FixAtCaretAsync);
            Add(0x0116, FixAllAsync);
            AddGrid(0x0117, () => options?.ShowCopyAsIn != false, CopyAsInAsync);
            AddGrid(0x0118, () => options?.ShowScriptAsInsert != false, ScriptAsInsertAsync);
            AddGrid(0x0119, () => options?.ShowOpenInExcel != false, OpenInExcelAsync);
            AddGrid(0x011A, () => options?.ShowSaveAsCsv != false, SaveAsCsvAsync);
            Add(0x011B, FindInvalidObjectsAsync);
            Add(0x011C, SplitTableAsync);
            Add(0x011D, EditFormattingStyleAsync);
            Add(0x011E, FormatFolderAsync);
            Add(0x011F, CompareObjectAsync);
            Add(0x0120, GoToDefinitionAsync);
            Add(0x0122, ScriptForDatabasesAsync);
            AddGrid(0x0123, () => options?.ShowCopyAsMarkdown != false, () => CopyGridAsync(c => ResultGrid.Markdown(c.Headers, c.Rows), "Markdown"));
            AddGrid(0x0124, () => options?.ShowCopyAsJson != false, () => CopyGridAsync(c => ResultGrid.Json(c.Headers, c.Types, c.Rows), "JSON"));
            AddGrid(0x0125, () => options?.ShowScriptAsUpdate != false, () => ScriptGridAsync((c, t) => ResultGrid.UpdateScript(c.Headers, c.Types, c.Rows, t), "UPDATE"));
            AddGrid(0x0126, () => options?.ShowScriptAsMerge != false, () => ScriptGridAsync((c, t) => ResultGrid.MergeScript(c.Headers, c.Types, c.Rows, t), "MERGE"));
            AddGrid(0x0127, () => options?.ShowScriptAsCreateTable != false, () => ScriptGridAsync((c, t) => ResultGrid.CreateTableScript(c.Headers, c.Types, c.Rows), "CREATE TABLE"));
            Add(0x0128, RunInDatabasesAsync);
            Add(0x0129, SearchDatabasesAsync);
            Add(0x012A, () => TransformSelectionAsync(sql => SqlRefactoring.WrapAsDynamicSql(sql), "Wrap in dynamic SQL"));
            Add(0x012B, () => TransformSelectionAsync(SqlRefactoring.UnwrapDynamicSql, "Unwrap dynamic SQL"));
            Add(0x012D, SearchCodeAsync);
            Add(0x012C, () => Task.Run(() => UpdateCheck.RunAsync(this, manual: true)));
            Add(0x0121, async () => { await JoinableTaskFactory.SwitchToMainThreadAsync(); ShowOptionPage(typeof(WorkbenchOptions)); });
            // Warm the caches that the typing path reads without touching the disk.
            SnippetList();
            OfflineSchemaNow();
            Instance = this;
            ServerColorMenu.Start();
            PriorityCommands.Start(this);
            ActivityLog.TryLogInformation("Querywright", "Package initialized");
            _ = JoinableTaskFactory.RunAsync(() => SelfTest.RunAsync(this));
            if (options.CheckForUpdates && Environment.GetEnvironmentVariable("QUERYWRIGHT_SELFTEST") == null)
                _ = Task.Run(() => UpdateCheck.RunAsync(this));
        }

        private volatile Tuple<string, DateTime, IReadOnlyList<KeyValuePair<string, string>>> snippetCache;
        private int snippetLoading;

        /// <summary>(shortcut, first line) pairs for the popup and Tab. Never touches the disk: the folder, which may be a slow
        /// or missing share, is listed in the background at most every 30 seconds.</summary>
        // ponytail: a new snippet file works from the next listing; Tab covers what the popup lists (first 500, ASCII names).
        internal IReadOnlyList<KeyValuePair<string, string>> SnippetList()
        {
            string folder = options?.SnippetFolder ?? "";
            var cache = snippetCache;
            if ((cache == null || cache.Item1 != folder || DateTime.UtcNow - cache.Item2 >= TimeSpan.FromSeconds(30))
                && Interlocked.Exchange(ref snippetLoading, 1) == 0)
                _ = Task.Run(() =>
                {
                    IReadOnlyList<KeyValuePair<string, string>> list = Array.Empty<KeyValuePair<string, string>>();
                    try
                    {
                        if (folder.Length > 0 && !Directory.Exists(folder)) SnippetFiles.Initialize(folder);
                        list = SnippetFiles.List(folder);
                    }
                    catch (Exception error) when (!(error is OutOfMemoryException)) { }
                    finally
                    {
                        snippetCache = Tuple.Create(folder, DateTime.UtcNow, list);
                        Interlocked.Exchange(ref snippetLoading, 0);
                    }
                });
            return cache != null && cache.Item1 == folder ? cache.Item3 : Array.Empty<KeyValuePair<string, string>>();
        }

        private bool IsSnippet(string shortcut) =>
            shortcut != null && SnippetList().Any(p => string.Equals(p.Key, shortcut, StringComparison.OrdinalIgnoreCase));

        internal bool HasSnippetShortcut(IWpfTextView view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!view.Selection.IsEmpty) return false;
            var point = view.Caret.Position.BufferPosition;
            var line = point.GetContainingLine();
            return IsSnippet(SnippetFiles.ShortcutBefore(line.GetText(), point.Position - line.Start.Position));
        }

        /// <summary>After a suggestion is committed: INSERT INTO t / EXEC p gets its column list or parameters right away.</summary>
        internal void TryFillAfterCommit(IWpfTextView view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (view.IsClosed || !view.Selection.IsEmpty || view.Caret.InVirtualSpace) return;
            try { Fill(view, view.TextSnapshot, view.Caret.Position.BufferPosition.Position); }
            catch (Exception error) when (!(error is OutOfMemoryException)) { ShowWarning(error.Message); }
        }

        // Tab after INSERT INTO t / EXEC p writes the column list or parameters.
        private bool Fill(IWpfTextView view, ITextSnapshot snapshot, int caret)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // ponytail: cheap keyword gate so ordinary Tabs never parse the whole script on the UI thread.
            int from = Math.Max(0, caret - 300);
            string recent = snapshot.GetText(from, caret - from);
            string text = recent.IndexOf("INSERT", StringComparison.OrdinalIgnoreCase) >= 0 || recent.IndexOf("EXEC", StringComparison.OrdinalIgnoreCase) >= 0
                ? snapshot.GetText() : null;
            var fill = text == null ? null : SqlAssist.FillStatement(text, caret, CurrentTables(), CurrentProcedures(text));
            if (fill == null) return false;
            ReplaceText(view, new SnapshotSpan(snapshot, fill.Start, fill.Length), fill.Text, fill.Text.Length, 0, 0, "Fill statement");
            return true;
        }

        private const string FieldsKey = "QuerywrightSnippetFields";

        private static void SelectField(IWpfTextView view, (List<ITrackingSpan> Fields, ITrackingPoint End) stops, int index)
        {
            var field = stops.Fields[index].GetSpan(view.TextSnapshot);
            view.Selection.Select(field, false);
            view.Caret.MoveTo(field.End);
            view.Caret.EnsureVisible();
        }

        /// <summary>Tab after a snippet with $name$ fields: select the next field, then go to $CURSOR$ (or the end) and stop.
        /// False when no snippet fields are active or the caret has left them, so Tab works as usual.</summary>
        internal bool TryNextField(IWpfTextView view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!view.Properties.TryGetProperty(FieldsKey, out (List<ITrackingSpan> Fields, ITrackingPoint End) stops)) return false;
            var snapshot = view.TextSnapshot;
            int caret = view.Caret.Position.BufferPosition.Position;
            int current = stops.Fields.FindIndex(f => f.GetSpan(snapshot).Contains(caret) || f.GetSpan(snapshot).End.Position == caret);
            if (current < 0) { EndFields(view); return false; }
            if (current + 1 < stops.Fields.Count) { SelectField(view, stops, current + 1); return true; }
            EndFields(view);
            view.Selection.Clear();
            view.Caret.MoveTo(stops.End.GetPoint(snapshot));
            return true;
        }

        internal static void EndFields(IWpfTextView view) => view.Properties.RemoveProperty(FieldsKey);

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
                if (caret > 0 && snapshot[caret - 1] == '*')
                {
                    var tables = CurrentTables();
                    TextEdit edit;
                    if (tables == null)
                    {
                        // First Tab after connecting: the catalog is still loading, so expand once it arrives if the text is unchanged.
                        var connection = options?.LiveMetadata != false ? LiveMetadata.Capture() : null;
                        if (connection == null) return false;
                        _ = JoinableTaskFactory.RunAsync(async () =>
                        {
                            var loaded = await LiveMetadata.GetAsync(connection, TimeSpan.FromSeconds(20));
                            await JoinableTaskFactory.SwitchToMainThreadAsync();
                            if (loaded == null || view.IsClosed || view.TextSnapshot != snapshot) return;
                            try { edit = SqlCompletion.ExpandWildcard(snapshot.GetText(), caret - 1, loaded); }
                            catch (Exception error) when (error is FormatException || error is InvalidOperationException) { return; }
                            ReplaceText(view, new SnapshotSpan(snapshot, edit.Start, edit.Length), edit.Text, edit.Text.Length, 0, 0, "Expand wildcard");
                        });
                        return true;
                    }
                    try { edit = SqlCompletion.ExpandWildcard(snapshot.GetText(), caret - 1, tables); }
                    catch (FormatException) { return false; } // Incomplete SQL while typing: ordinary Tab.
                    ReplaceText(view, new SnapshotSpan(snapshot, edit.Start, edit.Length), edit.Text, edit.Text.Length, 0, 0, "Expand wildcard");
                    return true;
                }
                if (Fill(view, snapshot, caret)) return true;
                string shortcut = SnippetFiles.ShortcutBefore(line.GetText(), caret - line.Start.Position);
                // An ordinary word, or a snippet folder that is empty, missing or unreachable, leaves Tab to the editor.
                if (!IsSnippet(shortcut)) return false;
                string template;
                // ponytail: one small read on the UI thread so Tab stays ordered with typing; the folder answered the last listing.
                try { template = SnippetFiles.Read(Path.Combine(options.SnippetFolder, shortcut + ".sql")); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is DecoderFallbackException)
                {
                    (GetService(typeof(SVsStatusbar)) as IVsStatusbar)?.SetText("Querywright: snippet " + shortcut + " could not be read. " + error.Message);
                    return false;
                }
                var expansion = Snippets.Expand(template, SnippetContext(template, ""), DateTimeOffset.Now);
                ReplaceText(view, new SnapshotSpan(snapshot, caret - shortcut.Length, shortcut.Length), expansion.Text,
                    expansion.Caret, expansion.SelectionStart, expansion.SelectionLength, "Expand snippet " + shortcut, expansion.Fields);
                return true;
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                ShowWarning(error.Message);
                return true;
            }
        }

        /// <summary>F12 on variables, aliases and CTEs jumps in the script; database objects open as a script in a new query.</summary>
        internal bool TryGoToDefinition(IWpfTextView view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (view.IsClosed) return false;
            // A highlighted name counts as the caret at its start.
            int position = view.Selection.IsEmpty ? view.Caret.Position.BufferPosition.Position : view.Selection.Start.Position.Position;
            DefinitionTarget target;
            try { target = SqlNavigation.FindDefinition(view.TextSnapshot.GetText(), position); }
            catch (FormatException) { return false; }
            SelfTest.Note = target == null ? "f12 no target" : "f12 target " + target.Offset;
            if (target == null) return false;
            if (target.Offset < 0)
            {
                if (target.Name == null || options?.LiveMetadata == false) return false;
                var connection = LiveMetadata.Capture();
                SelfTest.Note += connection == null ? " no connection" : " scripting";
                if (connection == null)
                {
                    // Say why instead of silently falling back to SSMS's own F12, which has nothing for objects.
                    ShowWarning(LiveMetadata.CaptureNames() == null
                        ? "F12 on " + target.Name + " needs a connected query window. Connect this window and try again."
                        : "F12 on " + target.Name + " could not use this window's connection: " + LiveMetadata.CaptureProblem + ".");
                    return true;
                }
                // OtherDb.dbo.Proc: read the definition from that database on the same server.
                if (target.Database != null) connection = connection.WithDatabase(target.Database);
                // One tab per object: F12 again switches to it while it is open, and does nothing while it is still loading.
                // ponytail: "Orders" and "dbo.Orders" share a key only when the schema is dbo.
                string key = connection.Key + "\0" + (target.Schema ?? "dbo") + "." + target.Name;
                if (scriptTabs.TryGetValue(key, out var tab))
                {
                    if (tab.View == null) { SelfTest.Note += " loading"; return true; }
                    if (!tab.View.IsClosed && ErrorHandler.Succeeded(tab.Frame.Show())) { SelfTest.Note += " reused"; return true; }
                }
                scriptTabs[key] = default;
                _ = JoinableTaskFactory.RunAsync(() => ScriptObjectAsync(connection, target.Schema, target.Name, key));
                return true;
            }
            var snapshot = view.TextSnapshot;
            view.Selection.Select(new SnapshotSpan(snapshot, target.Offset, target.Length), false);
            view.Caret.MoveTo(new SnapshotPoint(snapshot, target.Offset));
            view.Caret.EnsureVisible();
            return true;
        }

        /// <summary>F12 when SSMS binds nothing to it. Quiet outside SQL editors and where there is nothing to go to.</summary>
        private Task GoToDefinitionAsync() => RunCommandAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            IWpfTextView view;
            try { view = GetSqlView(); } catch (InvalidOperationException) { return; }
            TryGoToDefinition(view);
            ActivityLog.TryLogInformation("Querywright", "F12 (Querywright command): " + SelfTest.Note);
        });

        /// <summary>SQL Prompt's F12: a table opens as CREATE TABLE, a procedure/view/function/trigger as ALTER, in a new query. Never executed.</summary>
        private readonly Dictionary<string, (IWpfTextView View, IVsWindowFrame Frame)> scriptTabs = new Dictionary<string, (IWpfTextView, IVsWindowFrame)>(StringComparer.OrdinalIgnoreCase);

        private async Task ScriptObjectAsync(ActiveConnection connection, string schema, string name, string key, int line = 0)
        {
            Exception failure = null;
            try
            {
                var details = await Task.Run(() =>
                {
                    try { return LiveMetadata.Details(connection, schema, name); }
                    catch (Exception error) when (error is System.Data.SqlClient.SqlException || error is InvalidOperationException) { failure = error; return null; }
                });
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                bool table = details?.Type == "U" && details.Columns.Count > 0;
                if (details == null || (!table && details.Definition == null))
                {
                    SelfTest.Note += failure != null ? " failed" : details == null ? " not found" : " no definition";
                    ShowWarning(failure != null ? "Could not read " + name + " from the server: " + Reason(failure)
                        : details == null ? name + " was not found in " + (connection.Database ?? "the current database") + "."
                        : name + "'s definition is encrypted or not visible with your permissions (VIEW DEFINITION).");
                    return;
                }
                string owner = details.Schema ?? schema ?? "dbo";
                // Like SSMS Modify / Script as: USE (so the new window targets the object's database), the Object comment and SET options.
                string header = Header(details, owner, name);
                string text = header + (table ? ScriptOf(details, owner, name) : SqlRefactoring.CreateToAlter(details.Definition));
                var view = await OpenInNewQueryAsync(text, "Script " + name);
                if (line > 0 && view != null)
                {
                    // ponytail: assumes CREATE -> ALTER keeps the line count, which it does for the keyword swap.
                    var snapshot = view.TextSnapshot;
                    int target = Math.Min(snapshot.LineCount - 1, header.Split('\n').Length - 2 + line);
                    view.Caret.MoveTo(snapshot.GetLineFromLineNumber(target).Start);
                    view.ViewScroller.EnsureSpanVisible(snapshot.GetLineFromLineNumber(target).Extent, EnsureSpanVisibleOptions.AlwaysCenter);
                }
                if (await GetServiceAsync(typeof(SVsShellMonitorSelection)) is IVsMonitorSelection selection
                    && ErrorHandler.Succeeded(selection.GetCurrentElementValue((uint)VSConstants.VSSELELEMID.SEID_WindowFrame, out object frame)) && frame is IVsWindowFrame opened)
                    scriptTabs[key] = (view, opened);
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                // SqlException text can name the server or login; show only what failed.
                ShowWarning((error as System.Reflection.TargetInvocationException)?.InnerException?.Message ?? error.Message);
            }
            finally
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                if (scriptTabs.TryGetValue(key, out var tab) && tab.View == null) scriptTabs.Remove(key);
            }
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
                var tables = await RequireTablesAsync();
                var edit = await Task.Run(() => SqlCompletion.ExpandWildcard(sql, position, tables));
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
                string sql = snapshot.GetText();
                int position = view.Caret.Position.BufferPosition.Position;
                var dialog = new RenameVariableDialog(sql, "local variable", "@newName", name => SqlRefactoring.RenameLocalVariable(sql, position, name));
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
                var tables = await RequireTablesAsync();
                var result = await Task.Run(() => SqlCompletion.Complete(sql, position, tables, qualifySingleTable: Options?.QualifySingleTable == true));
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                if (view.IsClosed || view.TextSnapshot != snapshot) throw new InvalidOperationException("Query changed. Request suggestions again.");
                if (result.Items.Count == 0)
                {
                    ShowWarning(string.IsNullOrEmpty(result.Limitation) ? "No matching columns or tables in the database metadata." : result.Limitation);
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

        /// <summary>Whole-document rewrite as one undo step; transforms validate their own output.</summary>
        private async Task TransformAsync(Func<string, string> transform, string name)
        {
            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var view = GetSqlView();
                var snapshot = view.TextSnapshot;
                string sql = snapshot.GetText();
                int caret = view.Caret.Position.BufferPosition.Position;
                string result = await Task.Run(() => transform(sql));
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                if (result == sql) return;
                ReplaceText(view, new SnapshotSpan(snapshot, 0, snapshot.Length), result, Math.Min(caret, result.Length), 0, 0, name);
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                ShowWarning(error.Message);
            }
        }

        private async Task RenameAliasAsync()
        {
            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var view = GetSqlView();
                if (!view.Selection.IsEmpty || view.Caret.InVirtualSpace)
                    throw new InvalidOperationException("Place the caret on a table alias without selecting text.");
                var snapshot = view.TextSnapshot;
                string sql = snapshot.GetText();
                int position = view.Caret.Position.BufferPosition.Position;
                var dialog = new RenameVariableDialog(sql, "table alias", "newAlias", name => SqlRefactoring.RenameAlias(sql, position, name));
                var shell = await GetServiceAsync(typeof(SVsUIShell)) as IVsUIShell;
                if (shell == null) throw new InvalidOperationException("SSMS window service unavailable.");
                ErrorHandler.ThrowOnFailure(shell.GetDialogOwnerHwnd(out var owner));
                new System.Windows.Interop.WindowInteropHelper(dialog).Owner = owner;
                if (dialog.ShowDialog() != true || dialog.Result == null) return;
                ReplaceText(view, new SnapshotSpan(snapshot, 0, snapshot.Length), dialog.Result.Text, Math.Min(position, dialog.Result.Text.Length), 0, 0, "Rename alias");
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                ShowWarning(error.Message);
            }
        }

        /// <summary>Summarize script / find unused declarations: results in the Error List, double-click navigates.</summary>
        private async Task ListAsync(Func<string, IReadOnlyList<OutlineItem>> find, string what, TaskErrorCategory category)
        {
            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var view = GetSqlView();
                var snapshot = view.TextSnapshot;
                string sql = snapshot.GetText();
                var items = await Task.Run(() => find(sql));
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                ShowTasks(view, snapshot, items.Select(i => (i.Kind + (i.Target.Length == 0 ? "" : " " + i.Target), i.Offset, i.Length, category)));
                var status = await GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
                status?.SetText($"Querywright: {items.Count} {what}.");
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                ShowWarning(error.Message);
            }
        }

        private void ShowTasks(IWpfTextView view, ITextSnapshot snapshot, IEnumerable<(string Text, int Offset, int Length, TaskErrorCategory Category)> items)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            errorList.Tasks.Clear();
            foreach (var item in items)
            {
                int offset = Math.Max(0, Math.Min(item.Offset, snapshot.Length)), length = Math.Max(0, Math.Min(item.Length, snapshot.Length - offset));
                var line = snapshot.GetLineFromPosition(offset);
                var task = new ErrorTask
                {
                    Text = item.Text, Line = line.LineNumber, Column = offset - line.Start.Position,
                    Category = TaskCategory.CodeSense, ErrorCategory = item.Category
                };
                task.Navigate += (sender, args) =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    if (view.IsClosed || view.TextSnapshot != snapshot)
                    {
                        ShowWarning("Results are stale. Run the command again.");
                        return;
                    }
                    view.Selection.Select(new SnapshotSpan(snapshot, offset, length), false);
                    view.Caret.MoveTo(new SnapshotPoint(snapshot, offset));
                    view.VisualElement.Focus();
                    view.Caret.EnsureVisible();
                };
                errorList.Tasks.Add(task);
            }
            errorList.Show();
        }

        private async Task RefreshMetadataAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            LiveMetadata.Refresh(); // every consumer (popup, quick info, *, INSERT/EXEC fill, JOIN ON, column picker) reads this cache
            var status = await GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
            if (options?.LiveMetadata == false)
            {
                status?.SetText("Querywright: Read live metadata is off (Tools > Options > Querywright); the offline schema file is re-read whenever it changes.");
                return;
            }
            var connection = LiveMetadata.Capture();
            if (connection == null)
            {
                status?.SetText(LiveMetadata.CaptureNames() == null ? "Querywright: no live connection; the offline schema file is re-read whenever it changes."
                    : "Querywright: live metadata can't use this window's connection (" + LiveMetadata.CaptureProblem + ").");
                return;
            }
            var load = LiveMetadata.LoadAsync(connection);
            uint cookie = 0;
            while (!load.IsCompleted && status != null)
            {
                var (percent, step) = LiveMetadata.Progress;
                string text = $"Querywright: refreshing metadata {percent}% ({step})...";
                status.Progress(ref cookie, 1, text, (uint)percent, 100);
                status.SetText(text);
                await Task.WhenAny(load, Task.Delay(250));
                await JoinableTaskFactory.SwitchToMainThreadAsync();
            }
            status?.Progress(ref cookie, 0, "", 0, 0);
            var tables = await load;
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var problem = LiveMetadata.LastProblem;
            status?.SetText(tables == null ? "Querywright: metadata refresh failed (" + problem + "); the last good metadata stays in use. See the SSMS activity log."
                : "Querywright: metadata refreshed (" + tables.Count + " tables and views, "
                    + (LiveMetadata.Procedures(connection)?.Count(p => !p.IsFunction) ?? 0) + " procedures)" + (problem.Length == 0 ? "." : "; " + problem + "."));
        }

        /// <summary>Shift+F5: selects the statement under the caret and runs SSMS's own Execute. Only on this explicit command.</summary>
        private async Task ExecuteCurrentStatementAsync()
        {
            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var view = GetSqlView();
                if (view.Selection.IsEmpty)
                {
                    var snapshot = view.TextSnapshot;
                    var statement = SqlNavigation.StatementAt(snapshot.GetText(), view.Caret.Position.BufferPosition.Position);
                    if (statement == null) throw new InvalidOperationException("Place the caret in a complete SQL statement.");
                    view.Selection.Select(new SnapshotSpan(snapshot, statement.Start, statement.Length), false);
                }
                var dte = await GetServiceAsync(typeof(SDTE));
                if (dte == null) throw new InvalidOperationException("SSMS automation service unavailable.");
                dte.GetType().InvokeMember("ExecuteCommand", System.Reflection.BindingFlags.InvokeMethod, null, dte, new object[] { "Query.Execute", "" });
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                ShowWarning((error as System.Reflection.TargetInvocationException)?.InnerException?.Message ?? error.Message);
            }
        }

        /// <summary>Hover link click: Script and Summary of one object from the connected database. Read-only; nothing is executed.</summary>
        internal async Task ShowObjectAsync(string schema, string name)
        {
            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var connection = LiveMetadata.Capture();
                if (connection == null) throw new InvalidOperationException("Connect the query window to a database first.");
                var details = await Task.Run(() => LiveMetadata.Details(connection, schema, name));
                if (details == null) throw new InvalidOperationException(schema + "." + name + " was not found in the connected database, or you lack VIEW DEFINITION permission.");
                string script = Header(details, details.Schema ?? schema, name) + ScriptOf(details, details.Schema ?? schema, name);
                bool parameters = details.Columns.Count == 0 && details.Parameters.Count > 0;
                var summary = parameters
                    ? details.Parameters.Select(p => (p.Name, p.Type, p.Output ? "OUTPUT" : "IN"))
                    : details.Columns.Select(c => (c.Name, c.DataType, c.Nullable ? "NULL" : "NOT NULL"));
                await ShowDialogAsync(new ObjectInfoWindow(schema + "." + name, script, summary.ToList(), parameters));
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                // Our own not-found text names only the object; a server's message can name the server or login.
                ShowWarning(error is InvalidOperationException && !(error.InnerException is System.Data.SqlClient.SqlException) ? error.Message : "Could not script the object: " + Reason(error));
            }
        }

        private static string Header(LiveMetadata.ObjectDetails details, string schema, string name) =>
            ObjectScript.Header(details.Database, details.Type, schema, name, details.AnsiNulls, details.QuotedIdentifier, DateTime.Now.ToString(CultureInfo.CurrentCulture));

        private static string ScriptOf(LiveMetadata.ObjectDetails details, string schema, string name) =>
            details.Type == "U" && details.Columns.Count > 0
                ? ObjectScript.CreateTable(schema, name, details.Columns, details.Filegroup, details.Constraints)
                : details.Definition ?? "-- The definition is encrypted or not visible with your permissions.";

        /// <summary>Compare the object at the caret with the same object in another database on the server: equal, or a diff window. Read-only.</summary>
        private Task CompareObjectAsync() => RunCommandAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = GetSqlView();
            // A selection counts as the caret at its start, so a highlighted name works.
            int position = view.Selection.IsEmpty ? view.Caret.Position.BufferPosition.Position : view.Selection.Start.Position.Position;
            DefinitionTarget target;
            try { target = SqlNavigation.FindDefinition(view.TextSnapshot.GetText(), position); }
            catch (FormatException) { target = null; }
            if (target == null || target.Offset >= 0 || target.Name == null)
                throw new InvalidOperationException("Highlight a table, view, procedure or function name in a script without syntax errors.");
            var connection = options?.LiveMetadata != false ? LiveMetadata.Capture() : null;
            if (connection == null) throw new InvalidOperationException("Connect the query window to the database that holds the object.");
            if (target.Database != null) connection = connection.WithDatabase(target.Database);
            string schema = target.Schema ?? "dbo", name = target.Name, full = schema + "." + name;
            var databases = (await Task.Run(() => LiveMetadata.Databases(connection))).Where(d => !string.Equals(d, connection.Database, StringComparison.OrdinalIgnoreCase)).ToList();
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (databases.Count == 0) throw new InvalidOperationException("No other database on this server to compare with.");
            // ponytail: same server only; another server would need its own credentials.
            var prompt = new PromptDialog("Querywright: compare " + full, "_Compare with database:", databases[0], databases);
            if (!await ShowDialogAsync(prompt)) return;
            string other = prompt.Value;
            var (left, right) = await Task.Run(() => (LiveMetadata.Details(connection, schema, name), LiveMetadata.Details(connection.WithDatabase(other), schema, name)));
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (left == null) throw new InvalidOperationException(full + " was not found in " + connection.Database + ", or you lack VIEW DEFINITION permission.");
            if (right == null) throw new InvalidOperationException(full + " does not exist in " + other + ".");
            string a = ScriptOf(left, schema, name), b = ScriptOf(right, schema, name);
            if (ObjectScript.SameScript(a, b))
            {
                VsShellUtilities.ShowMessageBox(this, full + " is identical in " + connection.Database + " and " + other + ".", "Querywright",
                    OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }
            var diff = await GetServiceAsync(typeof(SVsDifferenceService)) as IVsDifferenceService;
            if (diff == null) throw new InvalidOperationException(full + " differs between " + connection.Database + " and " + other + " (diff window unavailable).");
            // The diff window deletes both files when it closes (the Temporary flags).
            string folder = Path.Combine(Path.GetTempPath(), "Querywright");
            Directory.CreateDirectory(folder);
            string stamp = Guid.NewGuid().ToString("N");
            string leftFile = Path.Combine(folder, "Compare-" + stamp + "-a.sql"), rightFile = Path.Combine(folder, "Compare-" + stamp + "-b.sql");
            File.WriteAllText(leftFile, a); File.WriteAllText(rightFile, b);
            diff.OpenComparisonWindow2(leftFile, rightFile, full + ": " + connection.Database + " vs " + other, null,
                connection.Database + ": " + full, other + ": " + full, null, null, (uint)(__VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary | __VSDIFFSERVICEOPTIONS.VSDIFFOPT_RightFileIsTemporary));
        });

        private async Task<bool> ShowDialogAsync(System.Windows.Window dialog)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var shell = await GetServiceAsync(typeof(SVsUIShell)) as IVsUIShell;
            if (shell == null) throw new InvalidOperationException("SSMS window service unavailable.");
            ErrorHandler.ThrowOnFailure(shell.GetDialogOwnerHwnd(out var owner));
            new System.Windows.Interop.WindowInteropHelper(dialog).Owner = owner;
            return dialog.ShowDialog() == true;
        }

        /// <summary>New query window (same connection as the active one) holding <paramref name="text"/>. Never executes it.</summary>
        private async Task<IWpfTextView> OpenInNewQueryAsync(string text, string name)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var dte = await GetServiceAsync(typeof(SDTE));
            if (dte == null) throw new InvalidOperationException("SSMS automation service unavailable.");
            IWpfTextView source = null;
            try { source = GetSqlView(); } catch (InvalidOperationException) { }
            // SSMS names new queries SQLQueryN.sql from its own counter; when that name is taken (a reopened or recovered
            // SQLQueryN.sql) it fails with STG_E_FILEALREADYEXISTS. The counter has moved on, so the next try gets a free name.
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    dte.GetType().InvokeMember("ExecuteCommand", System.Reflection.BindingFlags.InvokeMethod, null, dte, new object[] { "File.NewQuery", "" });
                    break;
                }
                catch (System.Reflection.TargetInvocationException error) when (attempt < 5 && error.InnerException?.HResult == unchecked((int)0x80030050)) { }
            }
            var view = GetSqlView();
            if (view == source) throw new InvalidOperationException("Could not open a new query window.");
            ReplaceText(view, new SnapshotSpan(view.TextSnapshot, 0, view.TextSnapshot.Length), text, 0, 0, 0, name);
            return view;
        }

        private async Task RunCommandAsync(Func<Task> body, [System.Runtime.CompilerServices.CallerMemberName] string command = "")
        {
            try { await body(); }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var cause = (error as System.Reflection.TargetInvocationException)?.InnerException ?? error;
                // ponytail: InvalidOperationException ("select a table"), Core's FormatException ("fix SQL syntax errors first") and file errors (file in use) are user-facing messages; anything else is a bug worth reporting.
                if (cause is InvalidOperationException || cause is FormatException || cause is IOException || cause is UnauthorizedAccessException) { ShowWarning(cause.Message); return; }
                // A server error is not a bug, and its text can name the server or login.
                if (cause is System.Data.SqlClient.SqlException) { ShowWarning("Could not read from the server: " + Reason(cause)); return; }
                string line = Updates.ErrorLine(UpdateCheck.InstalledVersion(), command, cause.GetType());
                int answer = VsShellUtilities.ShowMessageBox(this,
                    cause.Message + "\r\n\r\n" + line + "\r\n\r\nOpen a GitHub issue with this line? Only the line above is sent; the error message, your query and connection are not.",
                    "Querywright error", OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_YESNO, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND);
                if (answer == (int)VSConstants.MessageBoxResult.IDYES)
                    try { System.Diagnostics.Process.Start(Updates.IssueUrl(line)); } catch (System.ComponentModel.Win32Exception) { }
            }
        }

        /// <summary>SQL Prompt's column picker: choose which columns replace * (or alias.*).</summary>
        private Task PickColumnsAsync() => RunCommandAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = GetSqlView();
            if (!view.Selection.IsEmpty || view.Caret.InVirtualSpace) throw new InvalidOperationException("Place the caret on * without selecting text.");
            var snapshot = view.TextSnapshot;
            string sql = snapshot.GetText();
            int position = view.Caret.Position.BufferPosition.Position;
            var tables = await RequireTablesAsync();
            var found = await Task.Run(() => SqlCompletion.WildcardColumns(sql, position, tables));
            var dialog = new ColumnPickerDialog(found.Columns);
            if (!await ShowDialogAsync(dialog) || dialog.Selected.Count == 0) return;
            string text = SqlCompletion.ColumnList(sql, found.Wildcard.Start, dialog.Selected);
            ReplaceText(view, new SnapshotSpan(snapshot, found.Wildcard.Start, found.Wildcard.Length), text, text.Length, 0, 0, "Pick columns");
        });

        private Task TabHistoryAsync() => RunCommandAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var dialog = new TabHistoryDialog(TabHistory.Folder);
            if (!await ShowDialogAsync(dialog) || dialog.Text == null) return;
            await OpenInNewQueryAsync(dialog.Text, "Reopen from tab history");
        });

        internal bool TabHistoryEnabled => options?.TabHistory != false;

        /// <summary>Smart rename: a reviewable sp_rename + ALTER script for dependent modules, opened in a new window, never executed.</summary>
        private Task RenameObjectAsync() => RunCommandAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = GetSqlView();
            DefinitionTarget target;
            try { target = SqlNavigation.FindDefinition(view.TextSnapshot.GetText(), view.Caret.Position.BufferPosition.Position); }
            catch (FormatException) { target = null; }
            if (target == null || target.Offset >= 0 || target.Name == null)
                throw new InvalidOperationException("Place the caret on a table, view, procedure or function name in a script without syntax errors.");
            if (target.Database != null) throw new InvalidOperationException("Rename works on objects in the connected database; open a query on " + target.Database + " first.");
            var connection = options?.LiveMetadata != false ? LiveMetadata.Capture() : null;
            if (connection == null) throw new InvalidOperationException("Connect the query window to the database that holds the object.");
            string schema = target.Schema ?? "dbo";
            var prompt = new PromptDialog("Querywright: rename " + schema + "." + target.Name, "_New name (the script opens in a new window; nothing runs):", target.Name);
            if (!await ShowDialogAsync(prompt)) return;
            string newName = prompt.Value;
            var dependents = await Task.Run(() => LiveMetadata.Dependents(connection, schema, target.Name));
            string script = SqlRefactoring.RenameObjectScript(schema, target.Name, newName, dependents);
            await OpenInNewQueryAsync(script, "Rename " + target.Name);
        });

        /// <summary>Split table: choose columns to move; a reviewable script opens in a new window and is never executed.</summary>
        private Task SplitTableAsync() => RunCommandAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = GetSqlView();
            DefinitionTarget target;
            try { target = SqlNavigation.FindDefinition(view.TextSnapshot.GetText(), view.Caret.Position.BufferPosition.Position); }
            catch (FormatException) { target = null; }
            if (target == null || target.Offset >= 0 || target.Name == null)
                throw new InvalidOperationException("Place the caret on a table name in a script without syntax errors.");
            if (target.Database != null) throw new InvalidOperationException("Split works on tables in the connected database; open a query on " + target.Database + " first.");
            var connection = options?.LiveMetadata != false ? LiveMetadata.Capture() : null;
            if (connection == null) throw new InvalidOperationException("Connect the query window to the database that holds the table.");
            string schema = target.Schema ?? "dbo";
            var tables = await RequireTablesAsync();
            var names = StringComparer.OrdinalIgnoreCase;
            var table = tables.FirstOrDefault(t => names.Equals(t.Schema, schema) && names.Equals(t.Name, target.Name))
                ?? throw new InvalidOperationException(schema + "." + target.Name + " is not a table in the connected database.");
            var key = await Task.Run(() => LiveMetadata.PrimaryKey(connection, schema, target.Name));
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (key.Count == 0) throw new InvalidOperationException(schema + "." + target.Name + " has no primary key; add one before splitting it.");
            var candidates = table.Columns.Where(c => !key.Contains(c, names)).ToList();
            if (candidates.Count == 0) throw new InvalidOperationException("Every column is part of the primary key; nothing to move.");
            var picker = new ColumnPickerDialog(candidates, false) { Title = "Querywright: columns to move out of " + target.Name };
            if (!await ShowDialogAsync(picker) || picker.Selected.Count == 0) return;
            var prompt = new PromptDialog("Querywright: split " + schema + "." + target.Name, "_New table (schema.name; the script opens in a new window, nothing runs):", schema + "." + target.Name + "Details");
            if (!await ShowDialogAsync(prompt)) return;
            string full = prompt.Value.Trim();
            int dot = full.IndexOf('.');
            string newSchema = dot > 0 ? full.Substring(0, dot).Trim('[', ']') : schema, newName = (dot > 0 ? full.Substring(dot + 1) : full).Trim('[', ']');
            string script = SqlRefactoring.SplitTableScript(table, key, picker.Selected, newSchema, newName);
            await OpenInNewQueryAsync(script, "Split " + target.Name);
        });

        /// <summary>Find invalid objects: a read-only binding check of every module, reported in a new window.</summary>
        private Task FindInvalidObjectsAsync() => RunCommandAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var connection = options?.LiveMetadata != false ? LiveMetadata.Capture() : null;
            if (connection == null) throw new InvalidOperationException("Connect the query window to the database to check.");
            var status = await GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
            status?.SetText("Querywright: checking modules for invalid references...");
            var items = await Task.Run(() => LiveMetadata.InvalidObjects(connection));
            await OpenInNewQueryAsync(SqlRefactoring.InvalidObjectsReport(connection.Database, items), "Invalid objects");
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            status?.SetText("Querywright: " + items.Count + " invalid object issue(s) found.");
        });

        private Task ScriptForDatabasesAsync() => RunCommandAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            string sql = QueryTextOrNull();
            if (string.IsNullOrWhiteSpace(sql)) throw new InvalidOperationException("Open a query window with the script to run in each database.");
            var connection = options?.LiveMetadata != false ? LiveMetadata.Capture() : null;
            if (connection == null) throw new InvalidOperationException("Connect the query window to the server whose databases you want, and turn on Read live metadata under Tools > Options > Querywright.");
            var databases = await Task.Run(() => LiveMetadata.Databases(connection));
            bool hasUse = await Task.Run(() => SqlRefactoring.ContainsUse(sql));
            var previous = new HashSet<string>((options.MultiDatabaseSelection ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
            var picker = new DatabasePickerDialog(connection.Server, databases, previous, hasUse);
            if (!await ShowDialogAsync(picker)) return;
            options.MultiDatabaseSelection = string.Join("\n", picker.Selected);
            options.SaveSettingsToStorage();
            // WPF controls belong to the UI thread: read the checkboxes before Task.Run.
            var selected = picker.Selected;
            bool stopOnError = picker.StopOnError, printName = picker.PrintName;
            string script = await Task.Run(() => SqlRefactoring.ForDatabases(sql, selected, stopOnError, printName));
            await OpenInNewQueryAsync(script, "Script for " + selected.Count + " databases");
        });

        /// <summary>Rewrites the selection (the whole window when nothing is selected) as one undo step.</summary>
        private Task TransformSelectionAsync(Func<string, string> transform, string name) => RunCommandAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = GetSqlView();
            var snapshot = view.TextSnapshot;
            // ponytail: a box or multi-range selection is transformed as the one range from its start to its end.
            var span = view.Selection.IsEmpty ? new SnapshotSpan(snapshot, 0, snapshot.Length) : new SnapshotSpan(view.Selection.Start.Position, view.Selection.End.Position);
            string result = transform(span.GetText());
            ReplaceText(view, span, result, result.Length, 0, 0, name); // caret is relative to the span start
        });

        /// <summary>Runs the script in each ticked database and shows the first result set of each, merged, in a window.</summary>
        private Task RunInDatabasesAsync() => RunCommandAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            string sql = QueryTextOrNull();
            if (string.IsNullOrWhiteSpace(sql)) throw new InvalidOperationException("Open a query window with the script to run in each database.");
            if (DatabaseTools.HasGo(sql)) throw new InvalidOperationException("Remove the GO separators first; the script runs as one batch in each database.");
            var connection = options?.LiveMetadata != false ? LiveMetadata.Capture() : null;
            if (connection == null) throw new InvalidOperationException("Connect the query window to the server whose databases you want, and turn on Read live metadata under Tools > Options > Querywright.");
            var databases = await Task.Run(() => LiveMetadata.Databases(connection));
            var previous = new HashSet<string>((options.MultiDatabaseSelection ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
            var picker = new DatabasePickerDialog(connection.Server, databases, previous, await Task.Run(() => SqlRefactoring.ContainsUse(sql)), "run in multiple databases");
            if (!await ShowDialogAsync(picker)) return;
            options.MultiDatabaseSelection = string.Join("\n", picker.Selected);
            options.SaveSettingsToStorage();
            var risky = await Task.Run(() => SqlAnalysis.UnfilteredChanges(sql, true, true, schema: true));
            var production = picker.Selected.Where(d => ColorRules.Matches(options.ProductionServers, connection.Server, d)).ToList();
            if (risky.Count > 0 && VsShellUtilities.ShowMessageBox(this,
                    (production.Count > 0 ? "PRODUCTION (" + connection.Server + "/" + string.Join(", ", production.Take(3)) + (production.Count > 3 ? ", ..." : "") + "): " : "")
                    + "This script changes schema or can change or remove every row (" + string.Join(", ", risky.Take(3)) + ") and will run in " + picker.Selected.Count + " databases. Run it?",
                    "Querywright", OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_YESNO, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND) != (int)VSConstants.MessageBoxResult.IDYES) return;
            var errors = new List<string>();
            var table = await Task.Run(() => DatabaseTools.Run(connection, picker.Selected, sql, null, true, errors));
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            DatabaseTools.Show("Querywright: results from " + picker.Selected.Count + " databases", table, errors);
        });

        /// <summary>Finds tables, views, procedures, functions, triggers and columns whose name contains the text, across the ticked databases.</summary>
        private Task SearchDatabasesAsync() => RunCommandAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var connection = options?.LiveMetadata != false ? LiveMetadata.Capture() : null;
            if (connection == null) throw new InvalidOperationException("Connect the query window to the server to search, and turn on Read live metadata under Tools > Options > Querywright.");
            string text = DatabaseTools.Ask("Querywright: find in all databases", "Object or column name contains:");
            if (text == null) return;
            var databases = await Task.Run(() => LiveMetadata.Databases(connection));
            var errors = new List<string>();
            var table = await Task.Run(() => DatabaseTools.Search(connection, databases, text, errors));
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            DatabaseTools.Show("Querywright: \"" + text + "\" in " + databases.Count + " databases", table, errors);
        });

        /// <summary>Procedures, views, functions and triggers in the current database whose code contains the text. Explicit command only;
        /// capped, low lock priority, and a table or view name goes through the dependency catalog instead of scanning every definition.</summary>
        private Task SearchCodeAsync() => RunCommandAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var connection = options?.LiveMetadata != false ? LiveMetadata.Capture() : null;
            if (connection == null) throw new InvalidOperationException("Connect the query window to the server to search, and turn on Read live metadata under Tools > Options > Querywright.");
            string initial = null;
            try
            {
                var view = GetSqlView();
                var caret = view.Caret.Position.BufferPosition;
                var line = caret.GetContainingLine();
                int column = caret.Position - line.Start.Position;
                initial = !view.Selection.IsEmpty ? view.Selection.StreamSelectionSpan.GetText()
                    : System.Text.RegularExpressions.Regex.Matches(line.GetText(), @"[\w@#$]+").Cast<System.Text.RegularExpressions.Match>().FirstOrDefault(m => m.Index <= column && column <= m.Index + m.Length)?.Value;
            }
            catch (InvalidOperationException) { }
            string text = DatabaseTools.Ask("Querywright: find in database code", "Code contains (a table or view name finds its usages):", initial);
            if (text == null) return;
            var errors = new List<string>();
            var table = await Task.Run(() => DatabaseTools.SearchCode(connection, text, errors));
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            DatabaseTools.Show("Querywright: \"" + text + "\" in " + (connection.Database ?? "database") + " code", table, errors, row =>
            {
                string key = connection.Key + "\0" + row["Schema"] + "." + row["Object"];
                scriptTabs[key] = default;
                _ = JoinableTaskFactory.RunAsync(() => ScriptObjectAsync(connection, (string)row["Schema"], (string)row["Object"], key, row["Line"] as int? ?? 0));
            });
        });

        private Task EncapsulateAsync() => RunCommandAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = GetSqlView();
            if (view.Selection.IsEmpty || view.Selection.Mode != TextSelectionMode.Stream)
                throw new InvalidOperationException("Select the statements to encapsulate.");
            var span = view.Selection.StreamSelectionSpan.SnapshotSpan;
            string sql = span.Snapshot.GetText();
            var prompt = new PromptDialog("Querywright: encapsulate as stored procedure", "_Procedure name (schema.name):", "dbo.usp_NewProcedure");
            if (!await ShowDialogAsync(prompt)) return;
            string full = prompt.Value.Trim();
            int dot = full.IndexOf('.');
            string schema = dot > 0 ? full.Substring(0, dot).Trim('[', ']') : "dbo", name = (dot > 0 ? full.Substring(dot + 1) : full).Trim('[', ']');
            string script = await Task.Run(() => SqlRefactoring.EncapsulateAsProcedure(sql, span.Start.Position, span.Length, schema, name));
            await OpenInNewQueryAsync(script, "Encapsulate as procedure");
        });

        private Task FixAtCaretAsync() => RunCommandAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = GetSqlView();
            var snapshot = view.TextSnapshot;
            string sql = snapshot.GetText();
            int caret = view.Caret.Position.BufferPosition.Position;
            var settings = await ReadSettingsAsync();
            var diagnostic = (await Task.Run(() => SqlAnalysis.Analyze(sql, default, settings))).Diagnostics
                .Where(d => SqlAnalysis.FixableRules.Contains(d.Rule) && d.Offset <= caret && caret <= d.Offset + d.Length)
                .OrderBy(d => d.Length).FirstOrDefault();
            if (diagnostic == null) throw new InvalidOperationException("No fixable issue at the caret. Fixable rules: " + string.Join(", ", SqlAnalysis.FixableRules) + ".");
            var tables = diagnostic.Rule == "SW001" ? await RequireTablesAsync() : null;
            var edit = await Task.Run(() => SqlAnalysis.Fix(sql, diagnostic, tables));
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (edit == null) throw new InvalidOperationException(diagnostic.Rule + " has no safe automatic fix here.");
            ReplaceText(view, new SnapshotSpan(snapshot, edit.Start, edit.Length), edit.Text, edit.Text.Length, 0, 0, "Fix " + diagnostic.Rule);
        });

        private Task FixAllAsync() => RunCommandAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = GetSqlView();
            var snapshot = view.TextSnapshot;
            string sql = snapshot.GetText();
            var tables = CurrentTables();
            var settings = await ReadSettingsAsync();
            var result = await Task.Run(() => SqlAnalysis.FixAll(sql, tables, "dbo", settings));
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (result.Fixed > 0)
                ReplaceText(view, new SnapshotSpan(snapshot, 0, snapshot.Length), result.Text, Math.Min(view.Caret.Position.BufferPosition.Position, result.Text.Length), 0, 0, "Fix all issues");
            var status = await GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
            status?.SetText($"Querywright: fixed {result.Fixed} issues.");
        });

        /// <summary>
        /// Reads the focused results grid. Must run before the command's first yielding await, while the grid
        /// still has keyboard focus. Cell text stays in memory; it is never logged.
        /// </summary>
        private GridCells ReadFocusedGrid(bool valuesOnly)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var grid = ResultsGridReader.FocusedGrid()
                ?? throw new InvalidOperationException("Click a cell in a query results grid, then use this command from the grid's right-click menu.");
            GridCells cells;
            try { cells = ResultsGridReader.Read(grid, valuesOnly); }
            catch (Exception error) when (error is System.Reflection.TargetInvocationException || error is NullReferenceException || error is InvalidCastException || error is FormatException || error is OverflowException)
            {
                throw new InvalidOperationException("Could not read this SSMS version's results grid (" + (error.InnerException ?? error).GetType().Name + ").");
            }
            if (cells.Rows.Count == 0) throw new InvalidOperationException(valuesOnly ? "Select the cells to copy." : "The results grid has no rows.");
            return cells;
        }

        /// <summary>Selected text of the query window, else its whole text; null when there is none. Never logged.</summary>
        private string QueryTextOrNull()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var view = GetSqlView();
                return view.Selection.IsEmpty ? view.TextSnapshot.GetText() : string.Concat(view.Selection.SelectedSpans.Select(s => s.GetText()));
            }
            catch (InvalidOperationException) { return null; }
        }

        private async Task GridStatusAsync(string message, GridCells cells)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var status = await GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
            status?.SetText("Querywright: " + message + (cells.Truncated ? " Stopped at the cell limit; select fewer cells for the rest." : ""));
        }

        private Task CopyAsInAsync() => RunCommandAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var cells = ReadFocusedGrid(valuesOnly: true);
            string text = ResultGrid.InClause(cells.Rows.Select(r => r[0]));
            System.Windows.Clipboard.SetDataObject(text, true);
            await GridStatusAsync("copied IN clause.", cells);
        });

        private Task ScriptAsInsertAsync() => RunCommandAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var cells = ReadFocusedGrid(valuesOnly: false);
            string script = await Task.Run(() => ResultGrid.InsertScript(cells.Headers, cells.Types, cells.Rows));
            await OpenInNewQueryAsync(script, "Script results as INSERT");
            await GridStatusAsync($"scripted {cells.Rows.Count} rows as INSERT (not executed).", cells);
        });

        private Task CopyGridAsync(Func<GridCells, string> format, string what) => RunCommandAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var cells = ReadFocusedGrid(valuesOnly: false);
            System.Windows.Clipboard.SetDataObject(format(cells), true);
            await GridStatusAsync($"copied {cells.Rows.Count} rows as {what}.", cells);
        });

        /// <summary>Scripts the grid into a new window; <paramref name="script"/> gets the cells and the query's first table. Never executed.</summary>
        private Task ScriptGridAsync(Func<GridCells, string, string> script, string what) => RunCommandAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var cells = ReadFocusedGrid(valuesOnly: false);
            string table = ResultGrid.SourceTable(QueryTextOrNull()) ?? "[dbo].[TargetTable]";
            string text = await Task.Run(() => script(cells, table));
            await OpenInNewQueryAsync(text, "Script results as " + what);
            await GridStatusAsync($"scripted {cells.Rows.Count} rows as {what} (not executed).", cells);
        });

        /// <summary>
        /// Excel opens a UTF-16 tab-delimited .csv correctly in every locale (a comma CSV breaks where the list separator is ';').
        /// Files older than a day are removed so result data does not linger in %TEMP%.
        /// </summary>
        private Task OpenInExcelAsync() => RunCommandAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var cells = ReadFocusedGrid(valuesOnly: false);
            string baseName = ResultGrid.FileName(QueryTextOrNull(), DateTime.Now);
            string path = await Task.Run(() =>
            {
                string folder = Path.Combine(Path.GetTempPath(), "Querywright", "Results");
                Directory.CreateDirectory(folder);
                foreach (var old in new DirectoryInfo(folder).GetFiles("*.xlsx").Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)))
                    try { old.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                string file = Path.Combine(folder, baseName + ".xlsx");
                for (int n = 2; File.Exists(file); n++) file = Path.Combine(folder, baseName + "_" + n + ".xlsx");
                using (var stream = File.Create(file)) ResultGrid.Xlsx(stream, cells.Headers, cells.Types, cells.Rows);
                return file;
            });
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("No program is registered for .xlsx files. The results were saved to " + path); }
            await GridStatusAsync($"opened {cells.Rows.Count} rows.", cells);
        });

        private Task SaveAsCsvAsync() => RunCommandAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var cells = ReadFocusedGrid(valuesOnly: false);
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Querywright: save results as CSV", Filter = "CSV (comma delimited)|*.csv|All files|*.*", FileName = ResultGrid.FileName(QueryTextOrNull(), DateTime.Now) + ".csv", OverwritePrompt = true };
            if (dialog.ShowDialog() != true) return;
            string file = dialog.FileName;
            char separator = CsvSeparator(options?.CsvDelimiter);
            bool headers = options?.CsvHeaders != false;
            await Task.Run(() => File.WriteAllText(file, ResultGrid.Delimited(cells.Headers, cells.Rows, separator, includeHeaders: headers), new UTF8Encoding(true)));
            await GridStatusAsync($"saved {cells.Rows.Count} rows.", cells);
        });

        private static char CsvSeparator(CsvDelimiter? delimiter) =>
            delimiter == CsvDelimiter.Semicolon ? ';' : delimiter == CsvDelimiter.Tab ? '\t' : delimiter == CsvDelimiter.Pipe ? '|' : ',';

        private Task EditFormattingStyleAsync() => RunCommandAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            string path = options.SettingsFile;
            bool created = string.IsNullOrWhiteSpace(path);
            if (created) path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Querywright", "settings.xml");
            var settings = File.Exists(path) ? await Task.Run(() => WorkbenchSettings.Load(path)) : new WorkbenchSettings();
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            using (var dialog = new FormattingStyleDialog(settings, path))
            {
                if (DialogParts.ShowModal(dialog) != System.Windows.Forms.DialogResult.OK) return;
                settings = dialog.Settings;
            }
            await Task.Run(() => settings.Save(path));
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (created) { options.SettingsFile = path; options.SaveSettingsToStorage(); }
            (await GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar)?.SetText("Querywright: formatting style and rules saved to " + path);
        });

        /// <summary>Formats every .sql file under a folder after a preview count and confirmation. Never touches a database.</summary>
        private Task FormatFolderAsync() => RunCommandAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            string folder;
            using (var picker = new System.Windows.Forms.FolderBrowserDialog { Description = "Querywright: format all .sql files in this folder and its subfolders", ShowNewFolderButton = false })
            {
                if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                folder = picker.SelectedPath;
            }
            var style = (await ReadSettingsAsync()).Formatting;
            var status = await GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            status?.SetText("Querywright: checking .sql files...");
            var files = await Task.Run(() => SqlFiles(folder));
            var preview = await Task.Run(() => SqlFormatting.FormatFiles(files, style, write: false));
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            int changes = preview.Count(r => r.Status == SqlFormatting.FileStatus.Changed), failed = preview.Count(r => r.Status == SqlFormatting.FileStatus.Failed);
            var results = preview;
            if (changes > 0)
            {
                string question = $"Format {changes} of {files.Count} .sql file(s) in place?\n\n{files.Count - changes - failed} already formatted, {failed} cannot be formatted (left untouched).\n" +
                    "Files keep their encoding and line endings. There is no undo; use source control or a copy.";
                if (VsShellUtilities.ShowMessageBox(this, question, "Querywright", OLEMSGICON.OLEMSGICON_QUERY, OLEMSGBUTTON.OLEMSGBUTTON_YESNO, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND) != 6) return; // 6 = IDYES
                status?.SetText("Querywright: formatting " + changes + " file(s)...");
                var targets = preview.Where(r => r.Status == SqlFormatting.FileStatus.Changed).Select(r => r.Path).ToList();
                var written = await Task.Run(() => SqlFormatting.FormatFiles(targets, style, write: true));
                results = preview.Where(r => r.Status != SqlFormatting.FileStatus.Changed).Concat(written).OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList();
            }
            var report = new StringBuilder($"-- Querywright bulk format: {folder}\r\n");
            foreach (var group in results.GroupBy(r => r.Status).OrderByDescending(g => g.Key))
            {
                report.Append($"--\r\n-- {group.Key} ({group.Count()})\r\n");
                foreach (var item in group) report.Append("--   " + item.Path + (item.Message == null ? "" : "  : " + System.Text.RegularExpressions.Regex.Replace(item.Message, @"\s+", " ")) + "\r\n");
            }
            if (files.Count == 0) report.Append("-- No .sql files found.\r\n");
            await OpenInNewQueryAsync(report.ToString(), "Bulk format report");
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            status?.SetText($"Querywright: formatted {results.Count(r => r.Status == SqlFormatting.FileStatus.Changed)} file(s); {results.Count(r => r.Status == SqlFormatting.FileStatus.Failed)} failed.");
        });

        /// <summary>*.sql files under a folder; skips hidden, system and link folders (.git, junction loops) and unreadable ones.</summary>
        private static List<string> SqlFiles(string root)
        {
            const int Limit = 20_000;
            var files = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0 && files.Count < Limit)
            {
                string folder = pending.Pop();
                try
                {
                    files.AddRange(Directory.EnumerateFiles(folder, "*.sql").Where(f => f.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)).Take(Limit - files.Count));
                    foreach (string child in Directory.EnumerateDirectories(folder))
                        if ((File.GetAttributes(child) & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) == 0) pending.Push(child);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { }
            }
            files.Sort(StringComparer.OrdinalIgnoreCase);
            return files;
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

        /// <summary>Format on save: the whole document, silently skipped when it does not parse, so a save is never blocked.</summary>
        internal void FormatBeforeSave(IWpfTextView view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var span = new SnapshotSpan(view.TextSnapshot, 0, view.TextSnapshot.Length);
            string original = span.GetText();
            // ponytail: synchronous; the save waits for the formatter, which is fine for ordinary scripts and skipped past 1 MB.
            if (original.Length > 1_000_000) return;
            string path = options.SettingsFile;
            string formatted;
            try { formatted = SqlFormatting.Format(original, (string.IsNullOrWhiteSpace(path) ? new WorkbenchSettings() : WorkbenchSettings.Load(path)).Formatting); }
            catch (Exception error) when (!(error is OutOfMemoryException)) { return; }
            if (formatted != original) ReplaceText(view, span, formatted, 0, 0, 0, "Format SQL on save");
        }

        private void ReplaceText(IWpfTextView view, SnapshotSpan span, string text, int caret, int selectionStart, int selectionLength, string name,
            IReadOnlyList<(int Start, int Length)> fields = null)
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
                if (fields?.Count > 0)
                {
                    view.Properties[FieldsKey] = (fields.Select(f => after.CreateTrackingSpan(start + f.Start, f.Length, SpanTrackingMode.EdgeInclusive)).ToList(),
                        after.CreateTrackingPoint(start + caret, PointTrackingMode.Positive));
                    SelectField(view, ((List<ITrackingSpan>, ITrackingPoint))view.Properties[FieldsKey], 0);
                }
                operations.AddAfterTextBufferChangePrimitive();
                transaction.Complete();
            }
            view.VisualElement.Focus();
            view.Caret.EnsureVisible();
        }

        internal WorkbenchOptions Options => options;

        /// <summary>For File > Save: the focused SQL view, else the active document's SQL view when focus is in its results.
        /// Null when the document being saved is not SQL, so another tab is never formatted.</summary>
        internal IWpfTextView SavedSqlView()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return GetSqlView(); }
            catch (InvalidOperationException) { }
            var view = GetSqlView(mustHaveFocus: false);
            var selection = GetService(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection;
            if (selection == null || ErrorHandler.Failed(selection.GetCurrentElementValue((uint)VSConstants.VSSELELEMID.SEID_DocumentFrame, out object frame))
                || !(frame is IVsWindowFrame window) || ErrorHandler.Failed(window.GetProperty((int)__VSFPROPID.VSFPROPID_pszMkDocument, out object moniker)))
                return null;
            return view.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document)
                && string.Equals(document.FilePath, moniker as string, StringComparison.OrdinalIgnoreCase) ? view : null;
        }

        internal IWpfTextView GetSqlView(bool mustHaveFocus = true)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (textManager == null || components == null)
                throw new InvalidOperationException("SSMS editor services unavailable.");
            ErrorHandler.ThrowOnFailure(textManager.GetActiveView(mustHaveFocus ? 1 : 0, null, out var adapter));
            var view = adapter == null ? null : components.GetService<IVsEditorAdaptersFactoryService>().GetWpfTextView(adapter);
            if (view == null || view.IsClosed) throw new InvalidOperationException("Open a SQL query editor first.");
            if (!EditorListener.IsSql(view.TextBuffer.ContentType))
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
                status?.SetText($"Querywright: {result.Diagnostics.Count} diagnostics.");
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

        /// <summary>Why a metadata connection failed, without the server's own text (it can name the server or login).</summary>
        internal static string Reason(Exception error)
        {
            var sql = error as System.Data.SqlClient.SqlException;
            if (error.InnerException is System.Security.Authentication.AuthenticationException || error.Message.IndexOf("certificate", StringComparison.OrdinalIgnoreCase) >= 0)
                return "the server's certificate is not trusted. Tick \"Trust server certificate\" in the Connect dialog, or install the certificate.";
            if (sql?.Number == 18456) return "login failed (SQL error 18456).";
            if (sql?.Number == 4060) return "cannot open the database (SQL error 4060).";
            if (sql?.Number == -2) return "the server did not answer in time.";
            return sql != null ? "SQL error " + sql.Number + "." : error.GetType().Name + ".";
        }

        private void ShowWarning(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            SelfTest.Note += " warning"; // not the message: F12 logs Note, and messages can hold object names
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

        /// <summary>Placeholder values; the connection is looked up only when the template uses it.</summary>
        private static Dictionary<string, string> SnippetContext(string template, string selectedText)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var context = new Dictionary<string, string> { ["MACHINE"] = Environment.MachineName, ["SELECTEDTEXT"] = selectedText };
            if (template.Contains("$PASTE$")) context["PASTE"] = System.Windows.Clipboard.GetText();
            var connection = template.Contains("$SERVER$") || template.Contains("$DBNAME$") || template.Contains("$USER$") ? LiveMetadata.Capture() : null;
            context["SERVER"] = connection?.Server ?? "";
            context["DBNAME"] = connection?.Database ?? "";
            context["USER"] = connection == null || connection.Integrated ? Environment.UserDomainName + "\\" + Environment.UserName : connection.User;
            return context;
        }

        private async Task InsertSnippetAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
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
                var expansion = Snippets.Expand(template, SnippetContext(template, selected.GetText()), DateTimeOffset.Now);
                if (view.IsClosed || view.TextSnapshot != before)
                    throw new InvalidOperationException("Query changed while choosing the snippet. Retry insertion.");
                ReplaceText(view, selected, expansion.Text, expansion.Caret, expansion.SelectionStart,
                    expansion.SelectionLength, "Insert SQL snippet", expansion.Fields);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
                error is ArgumentException || error is FormatException || error is InvalidOperationException || error is COMException)
            {
                VsShellUtilities.ShowMessageBox(this, error.Message, "Querywright snippet insertion",
                    OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
        }
    }
}

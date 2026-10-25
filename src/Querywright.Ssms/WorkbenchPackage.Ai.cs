using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Querywright.Core;

namespace Querywright.Ssms
{
    // AI assistant and MCP server. The provider is the user's choice (Tools > Options > Querywright > AI); nothing is sent
    // until an AI command is used, and then the script text, the prompt and matching table definitions go to that provider.
    [ProvideOptionPage(typeof(AiOptions), "Querywright", "AI", 0, 0, true)]
    [ProvideToolWindow(typeof(AiToolWindow), Style = VsDockStyle.Tabbed, Orientation = ToolWindowOrientation.Right, Window = "{EEFA5220-E298-11D0-8F78-00A0C9110057}")] // docks with Properties
    public sealed partial class WorkbenchPackage
    {
        private AiOptions aiOptions;
        private McpHost mcp;

        internal AiOptions Ai => aiOptions;

        private void InitializeAi(Action<int, Func<Task>> add)
        {
            aiOptions = (AiOptions)GetDialogPage(typeof(AiOptions));
            add(0x0180, () => ShowAiAsync(null));
            add(0x0181, () => ShowAiAsync(AiTask.Explain));
            add(0x0182, () => ShowAiAsync(AiTask.Fix));
            add(0x0183, () => ShowAiAsync(AiTask.Optimize));
            add(0x0184, AiCompleteAsync);
            add(0x0185, CopyMcpConfigurationAsync);
            add(0x0186, async () => { await JoinableTaskFactory.SwitchToMainThreadAsync(); ShowOptionPage(typeof(AiOptions)); });
            RestartMcp();
        }

        /// <summary>Starts, stops or moves the MCP server to match the options. A port in use is reported, not fatal.</summary>
        internal void RestartMcp()
        {
            mcp?.Dispose();
            mcp = null;
            if (aiOptions?.McpServer != true) return;
            try { mcp = new McpHost(this, aiOptions.McpPort); }
            catch (Exception error) when (error is System.Net.HttpListenerException || error is ArgumentException || error is InvalidOperationException)
            {
                Diagnostics.TryLogWarning("Querywright", "MCP server not started: " + error.GetType().Name);
                _ = JoinableTaskFactory.RunAsync(async () =>
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    ShowWarning("The Querywright MCP server could not listen on port " + aiOptions.McpPort + " (" + error.Message.Trim() + "). Pick another port under Tools > Options > Querywright > AI.");
                });
            }
        }

        private void StopAi() { mcp?.Dispose(); mcp = null; }

        private Task ShowAiAsync(AiTask? start) => RunCommandAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var window = FindToolWindow(typeof(AiToolWindow), 0, true) as AiToolWindow;
            if (!(window?.Frame is IVsWindowFrame frame)) throw new InvalidOperationException("Could not create the Querywright AI window.");
            ErrorHandler.ThrowOnFailure(frame.Show());
            if (start != null) window.Panel.Start(start.Value);
            else window.Panel.FocusPrompt();
        });

        /// <summary>Selected text of the last active query window, else its whole text; empty when no query window is open.</summary>
        internal string AiEditorText(bool selectionOnly = false, bool wholeScript = false)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            IWpfTextView view;
            try { view = GetSqlView(mustHaveFocus: false); }
            catch (Exception error) when (error is InvalidOperationException || error is System.Runtime.InteropServices.COMException) { return ""; }
            if (!wholeScript && !view.Selection.IsEmpty) return string.Concat(view.Selection.SelectedSpans.Select(s => s.GetText()));
            return selectionOnly ? "" : view.TextSnapshot.GetText();
        }

        internal ActiveConnection AiConnection(bool required)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var connection = LiveMetadata.Capture();
            if (connection == null && required) throw new InvalidOperationException("Connect the query window to a database first.");
            return connection;
        }

        /// <summary>Catalog of the connected database (waits briefly for a first load) merged over the offline schema; null when neither exists.</summary>
        internal async Task<IReadOnlyList<SchemaTable>> AiTablesAsync(ActiveConnection connection, int seconds = 10)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            IReadOnlyList<SchemaTable> live = null;
            // A catalog that cannot be read (permissions, dropped connection) only costs the schema context.
            try { live = LiveMetadataEnabled ? await LiveMetadata.GetAsync(connection, TimeSpan.FromSeconds(seconds)) : null; }
            catch (Exception error) when (error is System.Data.SqlClient.SqlException || error is InvalidOperationException) { }
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            return Merge(live, OfflineSchemaNow());
        }

        internal WorkbenchSettings AiSettings()
        {
            string path = options?.SettingsFile;
            try { return string.IsNullOrWhiteSpace(path) ? new WorkbenchSettings() : WorkbenchSettings.Load(path); }
            catch (Exception error) when (!(error is OutOfMemoryException)) { return new WorkbenchSettings(); }
        }

        internal static string AiScript(LiveMetadata.ObjectDetails details, string schema, string name) => ScriptOf(details, details.Schema ?? schema, name);

        internal (string Where, string Scope, int Tables) AiContextPreview(string instruction, bool wholeScript, bool includeSchema)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var names = LiveMetadata.CaptureNames();
            string sql = AiEditorText(wholeScript: wholeScript);
            AiAssist.SchemaContext(includeSchema ? CurrentTables() : null, instruction + "\n" + sql, out int count, aiOptions.MaxSchemaTables);
            return (names == null ? "No connection" : names.Value.Database + " on " + names.Value.Server,
                !wholeScript && AiEditorText(true).Length > 0 ? "Selection (" + (sql.Count(c => c == '\n') + 1) + " lines)" : "Whole script", count);
        }

        /// <summary>Cached table and view names for @mention suggestions: prefix matches first, then names containing the typed text.</summary>
        internal IReadOnlyList<(string Name, bool IsView)> AiMentionNames(string typed, int max = 12)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var tables = CurrentTables();
            if (tables == null) return new (string, bool)[0];
            typed = typed ?? "";
            return tables.Select(t => (Name: t.Schema + "." + t.Name, Short: t.Name, t.IsView))
                .Where(t => t.Name.IndexOf(typed, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(t => t.Short.StartsWith(typed, StringComparison.OrdinalIgnoreCase) || t.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Take(max).Select(t => (t.Name, t.IsView)).ToList();
        }

        /// <summary>One AI request for the panel. A Chat task continues <paramref name="conversation"/> when it has one; any other
        /// task starts over from the query window. The reply is appended to the conversation and returned.</summary>
        internal async Task<AiReply> AiAskAsync(AiTask task, string instruction, List<AiMessage> conversation, Action<string> progress, CancellationToken cancellation,
            IProgress<string> deltas = null, bool wholeScript = false, bool includeSchema = true, Action<IReadOnlyList<AiMessage>, string> prepared = null, IProgress<string> thoughts = null)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellation);
            var ai = aiOptions;
            var provider = (ai.ApiFormat, ai.BaseUrl, ai.Model, ai.Reasoning, ai.ExtraRequestJson);
            int maxSchemaTables = ai.MaxSchemaTables;
            bool followUp = task == AiTask.Chat && conversation.Count > 0;
            var messages = followUp ? conversation.ToList() : new List<AiMessage>();
            string sql = AiEditorText(wholeScript: wholeScript);
            if (Diagnostics.Enabled) Diagnostics.Write("ai_context", task.ToString(), text: "Query:\n" + sql + "\nPrompt:\n" + instruction);
            AiAssist.Messages(task, instruction, sql, null); // validate before any server work
            var names = LiveMetadata.CaptureNames();
            string scope = !wholeScript && AiEditorText(true).Length > 0 ? "Selection (" + (sql.Count(c => c == '\n') + 1) + " lines)" : "Whole script";
            var connection = LiveMetadataEnabled ? AiConnection(required: false) : null;
            string extra = null;
            if (task == AiTask.Fix && connection != null)
            {
                progress("Asking SQL Server for compile errors...");
                var errors = await Task.Run(() => AiSql.Errors(connection, sql), cancellation);
                if (errors.Count == 0 && string.IsNullOrWhiteSpace(instruction))
                    throw new InvalidOperationException("SQL Server reports no compile errors for this script. For an error that only shows when it runs, paste the message into the prompt box and use Fix again.");
                extra = string.Join("\n", errors);
            }
            else if (task == AiTask.Optimize && connection != null)
            {
                progress("Reading the estimated execution plan...");
                try { extra = await Task.Run(() => AiSql.Plan(connection, sql), cancellation); }
                catch (System.Data.SqlClient.SqlException) { extra = null; }
            }
            else if (task == AiTask.Review)
            {
                var settings = AiSettings();
                extra = string.Join("\n", SqlAnalysis.Analyze(sql, cancellation, settings).Diagnostics.Take(80)
                    .Select(d => "Line " + d.Line + ", column " + d.Column + ": [" + d.Rule + "] " + d.Message));
            }
            string objects = null; int objectCount = 0;
            var mentions = AiAssist.Mentions(instruction);
            if (mentions.Count > 0 && connection != null)
            {
                progress("Reading definitions of mentioned objects...");
                var known = CurrentTables() ?? new SchemaTable[0];
                objects = await Task.Run(() =>
                {
                    var scripts = new List<string>();
                    foreach (var (schemaName, name) in mentions)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        string owner = schemaName ?? known.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))?.Schema;
                        // An unknown name (often a T-SQL @variable) or an unreadable object only costs its definition.
                        try { var details = LiveMetadata.Details(connection, owner, name); if (details != null) scripts.Add(AiScript(details, owner, name)); }
                        catch (System.Data.SqlClient.SqlException) { }
                    }
                    objectCount = scripts.Count;
                    return string.Join("\n\nGO\n\n", scripts);
                }, cancellation);
            }
            string schema = null; int tableCount = 0;
            if (includeSchema && maxSchemaTables > 0)
            {
                progress("Reading the database schema...");
                schema = AiAssist.SchemaContext(await AiTablesAsync(connection), instruction + "\n" + sql, out tableCount, maxSchemaTables);
            }
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellation);
            var request = AiAssist.Messages(task, instruction, sql, schema, extra, objects);
            if (followUp) messages.Add(request.Last()); else messages.AddRange(request);
            prepared?.Invoke(messages, string.Join(", ", new[]
            {
                names == null ? "No connection" : names.Value.Database + " on " + names.Value.Server,
                scope.Substring(0, 1).ToLowerInvariant() + scope.Substring(1),
                includeSchema ? tableCount + (tableCount == 1 ? " table" : " tables") : "no schema",
                objectCount > 0 ? objectCount + " mentioned object" + (objectCount == 1 ? "" : "s") : null
            }.Where(part => part != null)));
            progress("Waiting for " + provider.Model + "...");
            return await AiReplyAsync(messages, conversation, cancellation, deltas, thoughts, provider);
        }

        internal async Task<AiReply> AiReplyAsync(IReadOnlyList<AiMessage> messages, List<AiMessage> conversation, CancellationToken cancellation, IProgress<string> deltas, IProgress<string> thoughts = null,
            (AiApiFormat Format, string BaseUrl, string Model, AiReasoningLevel Reasoning, string ExtraRequestJson)? request = null)
        {
            var ai = aiOptions;
            var provider = request ?? (ai.ApiFormat, ai.BaseUrl, ai.Model, ai.Reasoning, ai.ExtraRequestJson);
            var reply = await AiClient.ReplyAsync(provider.Item1, provider.BaseUrl, provider.Model, messages, cancellation, progress: deltas, reasoning: provider.Reasoning, extraJson: provider.ExtraRequestJson, thoughts: thoughts);
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellation);
            conversation.Clear(); conversation.AddRange(messages);
            conversation.Add(new AiMessage("assistant", reply.Text));
            return reply;
        }

        /// <summary>Puts AI SQL into the last active query window: over the selection (the whole script when nothing is selected), or at the caret.</summary>
        internal async Task AiInsertAsync(string sql, bool replace)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = GetSqlView(mustHaveFocus: false);
            var snapshot = view.TextSnapshot;
            SnapshotSpan span;
            if (!replace) span = new SnapshotSpan(view.Caret.Position.BufferPosition, 0);
            else if (view.Selection.IsEmpty) span = new SnapshotSpan(snapshot, 0, snapshot.Length);
            else span = new SnapshotSpan(view.Selection.Start.Position, view.Selection.End.Position);
            sql = sql.Replace("\r\n", "\n").Replace("\n", NewlineOf(snapshot.GetText()));
            if (replace)
            {
                var diff = await GetServiceAsync(typeof(SVsDifferenceService)) as IVsDifferenceService;
                var shell = await GetServiceAsync(typeof(SVsUIShell)) as IVsUIShell;
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                if (diff == null || shell == null) throw new InvalidOperationException("SSMS comparison window service unavailable.");
                ErrorHandler.ThrowOnFailure(shell.GetDialogOwnerHwnd(out var owner));
                string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Querywright"), stamp = Guid.NewGuid().ToString("N");
                string leftFile = System.IO.Path.Combine(folder, "AI-" + stamp + "-current.sql"), rightFile = System.IO.Path.Combine(folder, "AI-" + stamp + "-replacement.sql");
                IVsWindowFrame frame = null;
                IVsWindowFrame2 events = null;
                uint cookie = 0;
                var dialog = new AiReplaceDialog();
                var choice = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler cancel = (s, e) => choice.TrySetResult(false);
                try
                {
                    System.IO.Directory.CreateDirectory(folder);
                    System.IO.File.WriteAllText(leftFile, span.GetText()); System.IO.File.WriteAllText(rightFile, sql);
                    frame = diff.OpenComparisonWindow2(leftFile, rightFile, "Querywright: AI replacement", null,
                        "Current text", "AI replacement", null, null,
                        (uint)(__VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary | __VSDIFFSERVICEOPTIONS.VSDIFFOPT_RightFileIsTemporary));
                    if (frame == null || !(frame is IVsWindowFrame2)) throw new InvalidOperationException("Could not open the AI comparison window.");
                    events = (IVsWindowFrame2)frame;
                    ErrorHandler.ThrowOnFailure(events.Advise(new AiReplaceFrameEvents(() => choice.TrySetResult(false)), out cookie));
                    ErrorHandler.ThrowOnFailure(frame.Show());
                    new System.Windows.Interop.WindowInteropHelper(dialog).Owner = owner;
                    dialog.Closed += (s, e) => choice.TrySetResult(dialog.Apply);
                    view.Closed += cancel;
                    using (DisposalToken.Register(() => choice.TrySetResult(false)))
                    {
                        dialog.Show();
                        if (!await choice.Task) return;
                    }
                }
                finally
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    view.Closed -= cancel;
                    try { if (cookie != 0) events.Unadvise(cookie); }
                    catch (System.Runtime.InteropServices.COMException) { }
                    dialog.Close();
                    try { frame?.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave); }
                    catch (System.Runtime.InteropServices.COMException) { }
                    foreach (string file in new[] { leftFile, rightFile })
                        try { System.IO.File.Delete(file); }
                        catch (Exception error) when (error is System.IO.IOException || error is UnauthorizedAccessException) { }
                }
                await JoinableTaskFactory.SwitchToMainThreadAsync();
            }
            ReplaceText(view, span, sql, sql.Length, 0, 0, replace ? "AI replace" : "AI insert");
        }

        private sealed class AiReplaceFrameEvents : IVsWindowFrameNotify
        {
            private readonly Action cancel;
            internal AiReplaceFrameEvents(Action cancel) => this.cancel = cancel;
            public int OnShow(int reason) { if (reason == (int)__FRAMESHOW.FRAMESHOW_WinClosed) cancel(); return VSConstants.S_OK; }
            public int OnMove() => VSConstants.S_OK;
            public int OnSize() => VSConstants.S_OK;
            public int OnDockableChange(int dockable) => VSConstants.S_OK;
        }

        /// <summary>Opens AI SQL in a new query window on the same connection and, when asked, executes it there.</summary>
        internal async Task AiOpenAsync(string sql, bool run)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (run && aiOptions.ConfirmExecution)
            {
                var changes = AiAssist.SqlChanges(sql);
                string warning = changes.Count == 0 ? "" : "\r\n\r\nPotential data or schema changes: " + string.Join(", ", changes) + ".";
                if (!ConfirmRun(sql, "Run this AI-written SQL in a new query window?" + warning)) return;
            }
            await OpenInNewQueryAsync(sql, "AI query");
            if (!run) return;
            var dte = await GetServiceAsync(typeof(SDTE));
            if (dte == null) throw new InvalidOperationException("SSMS automation service unavailable.");
            dte.GetType().InvokeMember("ExecuteCommand", System.Reflection.BindingFlags.InvokeMethod, null, dte, new object[] { "Query.Execute", "" });
        }

        /// <summary>Yes/No box showing the SQL and where it would run; No is the default.</summary>
        internal bool ConfirmRun(string sql, string question)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var names = LiveMetadata.CaptureNames();
            string where = names == null ? "" : "\r\n\r\nServer: " + names.Value.Server + "\r\nDatabase: " + names.Value.Database;
            string shown = sql.Length > 1500 ? sql.Substring(0, 1500) + "\r\n... (" + (sql.Length - 1500) + " more characters)" : sql;
            return VsShellUtilities.ShowMessageBox(this, question + where + "\r\n\r\n" + shown, "Querywright AI",
                OLEMSGICON.OLEMSGICON_QUERY, OLEMSGBUTTON.OLEMSGBUTTON_YESNO, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND) == (int)VSConstants.MessageBoxResult.IDYES;
        }

        // ponytail: an explicit command, not grey ghost text while typing; the text arrives in one piece and Ctrl+Z removes it.
        private Task AiCompleteAsync() => RunCommandAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = GetSqlView();
            var snapshot = view.TextSnapshot;
            int caret = view.Caret.Position.BufferPosition.Position;
            string sql = snapshot.GetText();
            var ai = aiOptions;
            var status = await GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
            status?.SetText("Querywright: asking " + ai.Model + " to complete...");
            string schema = ai.MaxSchemaTables > 0 ? AiAssist.SchemaContext(CurrentTables(), sql, ai.MaxSchemaTables) : null;
            var messages = AiAssist.Messages(AiTask.Complete, null, AiAssist.CompletionWindow(sql, caret), schema);
            string text = AiAssist.CompletionText(await AiClient.AskAsync(ai.ApiFormat, ai.BaseUrl, ai.Model, messages, DisposalToken, 700));
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (text.Trim().Length == 0) { status?.SetText("Querywright: the AI had nothing to add here."); return; }
            text = text.Replace("\r\n", "\n").Replace("\n", NewlineOf(sql));
            // ReplaceText refuses when the script changed while the provider was answering.
            ReplaceText(view, new SnapshotSpan(snapshot, caret, 0), text, text.Length, 0, 0, "AI complete");
            status?.SetText("Querywright: AI completion inserted. Ctrl+Z removes it.");
        });

        private Task CopyMcpConfigurationAsync() => RunCommandAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            string url = "http://localhost:" + aiOptions.McpPort + "/mcp";
            var server = new Dictionary<string, object>
            {
                ["type"] = "http", ["url"] = url,
                ["headers"] = new Dictionary<string, object> { ["Authorization"] = "Bearer " + McpHost.Token() }
            };
            System.Windows.Clipboard.SetText(MiniJson.Write(new Dictionary<string, object> { ["mcpServers"] = new Dictionary<string, object> { ["querywright"] = server } }));
            VsShellUtilities.ShowMessageBox(this,
                "Copied the MCP client configuration (server URL " + url + " and its access token) to the clipboard. Paste it into your MCP client's configuration file."
                + (mcp == null ? "\r\n\r\nThe server is not running: turn on \"Run MCP server\" under Tools > Options > Querywright > AI." : "")
                + "\r\n\r\nAnyone holding the token can read and run SQL through your open query window while SSMS runs, so keep it out of shared files.",
                "Querywright MCP", OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        });
    }
}

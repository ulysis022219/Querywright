#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Core.Imaging;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using Querywright.Core;
using VsCompletionItem = Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data.CompletionItem;

namespace Querywright.Ssms
{
    /// <summary>Hooks Tab (snippet shortcuts, wildcard expansion) and F12 (go to definition) in SQL editors.</summary>
    // ponytail: exported for all text so SSMS's query-editor content type (name unconfirmed) is covered; filtered in code.
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class EditorListener : IVsTextViewCreationListener
    {
        [Import] internal IVsEditorAdaptersFactoryService Adapters = null!;
        [Import] internal IAsyncCompletionBroker Completion = null!;
        [Import] internal IAsyncQuickInfoBroker QuickInfo = null!;

        internal static bool IsSql(IContentType type) =>
            type.IsOfType("SQL") || type.IsOfType("T-SQL") || type.TypeName.IndexOf("SQL", StringComparison.OrdinalIgnoreCase) >= 0;

        // UI thread only.
        private static readonly HashSet<IWpfTextView> OpenViews = new HashSet<IWpfTextView>();

        /// <summary>One view per SQL document with unsaved changes, for format on Save All.</summary>
        internal static IReadOnlyList<IWpfTextView> UnsavedViews() => OpenViews
            .Where(v => !v.IsClosed && v.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document) && document.IsDirty)
            .GroupBy(v => v.TextBuffer).Select(g => g.First()).ToList();

        public void VsTextViewCreated(IVsTextView adapter)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            var view = Adapters.GetWpfTextView(adapter);
            if (view == null) return;
            var type = view.TextBuffer.ContentType;
            Microsoft.VisualStudio.Shell.ActivityLog.TryLogInformation("Querywright",
                "Editor opened: content type " + type.TypeName + " (" + string.Join(",", type.BaseTypes.Select(b => b.TypeName)) + ")");
            if (!IsSql(type)) return;
            WorkbenchPackage.EnsureLoaded();
            WorkbenchPackage.Instance?.CurrentTables(); // start the live metadata load before the first keystroke
            // ponytail: SSMS raises no public connect event; a cheap poll starts the load once the window connects.
            // TryGet only starts a background load when the connection key is new, so repeats are no-ops.
            var poll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            poll.Tick += (s, e) =>
            {
                try { if (view.HasAggregateFocus && WorkbenchPackage.Instance?.LiveMetadataEnabled == true) LiveMetadata.TryGet(LiveMetadata.Capture()); }
                catch (Exception error) when (!(error is OutOfMemoryException)) { EditorCommandFilter.Swallowed(error); }
            };
            OpenViews.Add(view);
            view.Closed += (s, e) => { poll.Stop(); OpenViews.Remove(view); };
            poll.Start();
            // Tab history: a timestamped version per window a few seconds after each edit, on execute, and on close.
            // Local only; opt out in options.
            var historyId = Guid.NewGuid();
            ITextSnapshot? saved = null;
            void SaveHistory(bool executed)
            {
                var snapshot = view.TextBuffer.CurrentSnapshot;
                if ((!executed && snapshot == saved) || WorkbenchPackage.Instance?.TabHistoryEnabled != true) return;
                saved = snapshot;
                string text = snapshot.GetText();
                string name = view.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document)
                    ? System.IO.Path.GetFileName(document.FilePath) : "";
                string connection = view.Properties.TryGetProperty("QuerywrightConnection", out string label) ? label : "";
                _ = System.Threading.Tasks.Task.Run(() => TabHistory.Save(historyId, name, connection, text, executed));
            }
            view.Properties["QuerywrightHistory"] = (Action<bool>)SaveHistory;
            var history = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            history.Tick += (s, e) =>
            {
                history.Stop();
                try { SaveHistory(false); }
                catch (Exception error) when (!(error is OutOfMemoryException)) { EditorCommandFilter.Swallowed(error); }
            };
            view.TextBuffer.Changed += (s, e) => { history.Stop(); history.Start(); };
            view.Closed += (s, e) => { history.Stop(); SaveHistory(false); };
            var filter = new EditorCommandFilter(view, Completion, QuickInfo);
            if (ErrorHandler.Succeeded(adapter.AddCommandFilter(filter, out var next))) filter.Next = next;
            SelfTest.Adapter = adapter;
            SelfTest.View = view;
        }
    }

    internal sealed class EditorCommandFilter : IOleCommandTarget
    {
        private readonly IWpfTextView view;
        private readonly IAsyncCompletionBroker completion;
        private readonly IAsyncQuickInfoBroker quickInfo;
        internal IOleCommandTarget? Next;
        internal EditorCommandFilter(IWpfTextView view, IAsyncCompletionBroker completion, IAsyncQuickInfoBroker quickInfo) { this.view = view; this.completion = completion; this.quickInfo = quickInfo; }

        internal const string HintKey = "QuerywrightParameterHint";

        /// <summary>Space, ( or , typed in an EXEC or call: show the parameter hint through quick info.</summary>
        private void TryParameterHint(Guid group, uint id, IntPtr input)
        {
            if (group != VSConstants.VSStd2K || id != (uint)VSConstants.VSStd2KCmdID.TYPECHAR || input == IntPtr.Zero ||
                WorkbenchPackage.Instance?.Options?.ParameterHints == false) return;
            char typed = (char)(ushort)System.Runtime.InteropServices.Marshal.GetObjectForNativeVariant(input);
            if (typed != ' ' && typed != '(' && typed != ',') return;
            var caret = view.Caret.Position.BufferPosition;
            string line = caret.GetContainingLine().GetText();
            // ponytail: cheap gate; the real check (tokens, known procedure) runs in the quick info source.
            if (typed == ' ' && line.IndexOf("EXEC", StringComparison.OrdinalIgnoreCase) < 0) return;
            view.Properties[HintKey] = caret.Position;
            _ = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                var open = quickInfo.GetSession(view);
                if (open != null) await open.DismissAsync();
                await quickInfo.TriggerQuickInfoAsync(view, caret.Snapshot.CreateTrackingPoint(caret.Position, PointTrackingMode.Positive), QuickInfoSessionOptions.None);
            });
        }

        /// <summary>The closing quote this filter inserted; typing ' right before it steps over it.</summary>
        private ITrackingPoint? closer;

        /// <summary>SQL Prompt-style closing quote: ' types '' with the caret between; Backspace in an empty pair removes both.</summary>
        private bool TryQuote(Guid group, uint id, IntPtr input)
        {
            if (group != VSConstants.VSStd2K || !view.Selection.IsEmpty) return false;
            var caret = view.Caret.Position.BufferPosition;
            var snapshot = caret.Snapshot;
            int at = caret.Position;
            bool atCloser = closer != null && closer.GetPosition(snapshot) == at && at < snapshot.Length && snapshot[at] == '\'';
            if (id == (uint)VSConstants.VSStd2KCmdID.BACKSPACE)
            {
                if (!atCloser || at == 0 || snapshot[at - 1] != '\'') return false;
                view.TextBuffer.Delete(new Span(at - 1, 2));
                closer = null;
                return true;
            }
            if (id != (uint)VSConstants.VSStd2KCmdID.TYPECHAR || input == IntPtr.Zero ||
                (char)(ushort)System.Runtime.InteropServices.Marshal.GetObjectForNativeVariant(input) != '\'') return false;
            if (atCloser)
            {
                view.Caret.MoveTo(new SnapshotPoint(snapshot, at + 1));
                closer = null;
                return true;
            }
            var line = caret.GetContainingLine();
            string before = snapshot.GetText(line.Start, at - line.Start);
            char next = at < line.End ? snapshot[at] : ' ';
            // ponytail: single-line check; a quote inside a multi-line string or block comment may still get a pair.
            // Plain quote inside a string or -- comment, when doubling an escape, or touching a word (N'...' excepted).
            if (before.Count(c => c == '\'') % 2 == 1 || before.Contains("--") || before.EndsWith("'") || char.IsLetterOrDigit(next) || next == '_' ||
                (System.Text.RegularExpressions.Regex.IsMatch(before, @"[\w@#]$") && !System.Text.RegularExpressions.Regex.IsMatch(before, @"(^|[^\w@#])[Nn]$")))
                return false;
            var after = view.TextBuffer.Insert(at, "''");
            view.Caret.MoveTo(new SnapshotPoint(after, at + 1));
            closer = after.CreateTrackingPoint(at + 1, PointTrackingMode.Positive);
            return true;
        }

        private static bool IsTab(Guid group, uint id) => group == VSConstants.VSStd2K && id == (uint)VSConstants.VSStd2KCmdID.TAB;
        private static bool IsReturn(Guid group, uint id) => group == VSConstants.VSStd2K && id == (uint)VSConstants.VSStd2KCmdID.RETURN;
        private static bool IsGoToDefinition(Guid group, uint id) =>
            group == VSConstants.GUID_VSStandardCommandSet97 && id == (uint)VSConstants.VSStd97CmdID.GotoDefn;

        /// <summary>False when the typed word is not a prefix of the selected item, so Tab never swaps in an unrelated name.</summary>
        private bool SelectionMatches()
        {
            var session = completion.GetSession(view);
            if (session == null || session.IsDismissed) return true;
            string typed = session.ApplicableToSpan.GetText(view.TextBuffer.CurrentSnapshot);
            var selected = session.GetComputedItems(CancellationToken.None).SelectedItem;
            return typed.Length == 0 || selected?.FilterText.StartsWith(typed, StringComparison.OrdinalIgnoreCase) == true;
        }

        public int Exec(ref Guid group, uint id, uint options, IntPtr input, IntPtr output)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            var package = WorkbenchPackage.Instance;
            // A bug in an extra must never surface as a dialog while typing: log the type and let the key through.
            try
            {
            // ponytail: no commit manager is registered for SQL, so the session would otherwise span spaces and dots and
            // Tab would replace the whole run. Punctuation closes the list without inserting; Tab/Enter still commit.
            if (group == VSConstants.VSStd2K && id == (uint)VSConstants.VSStd2KCmdID.TYPECHAR && input != IntPtr.Zero &&
                completion.IsCompletionActive(view))
            {
                char typed = (char)(ushort)System.Runtime.InteropServices.Marshal.GetObjectForNativeVariant(input);
                if (!char.IsLetterOrDigit(typed) && typed != '_' && typed != '@' && typed != '#') completion.GetSession(view)?.Dismiss();
            }
            if (package != null)
            {
                if (package.Options?.CloseQuotes != false && TryQuote(group, id, input)) return VSConstants.S_OK;
                // SQL Prompt: Tab on a typed snippet shortcut expands it even while the suggestion list is open.
                if (IsTab(group, id) && completion.IsCompletionActive(view) && (package.HasSnippetShortcut(view) || !SelectionMatches()))
                    completion.GetSession(view)?.Dismiss();
                if (group == VSConstants.VSStd2K && id == (uint)VSConstants.VSStd2KCmdID.CANCEL) WorkbenchPackage.EndFields(view);
                if (IsTab(group, id) && !completion.IsCompletionActive(view) && package.TryNextField(view)) return VSConstants.S_OK;
                if (IsTab(group, id) && package.Options?.TabExpandSnippets != false && !completion.IsCompletionActive(view))
                {
                    bool expanded = package.TryTabExpand(view);
                    Microsoft.VisualStudio.Shell.ActivityLog.TryLogInformation("Querywright", "Tab received; expanded=" + expanded);
                    if (expanded) return VSConstants.S_OK;
                }
                if (IsGoToDefinition(group, id) && package.Options?.GoToDefinition != false && package.TryGoToDefinition(view)) return VSConstants.S_OK;
            }
            }
            catch (Exception error) when (!(error is OutOfMemoryException)) { Swallowed(error); }
            // Tab/Enter that commits a table or procedure after INSERT INTO / EXEC fills the statement in the same keystroke.
            // SSMS's own IntelliSense list commits inside Next.Exec too; a changed buffer after Tab/Enter gets the same fill.
            bool key = package != null && (IsTab(group, id) || IsReturn(group, id));
            bool committing = key && completion.IsCompletionActive(view);
            var before = view.TextSnapshot;
            int result;
            // ponytail: SSMS's own IntelliSense can throw while typing an unknown name, and the shell turns that into a
            // modal "Object reference not set" box. Log it instead; the red squiggle already marks the name.
            try { result = Next?.Exec(ref group, id, options, input, output) ?? (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED; }
            catch (Exception error) when (error is NullReferenceException || error is InvalidOperationException || error is ArgumentException)
            {
                Swallowed(error);
                return VSConstants.S_OK;
            }
            if (ErrorHandler.Succeeded(result) && view.TextSnapshot != before)
            {
                try { TryParameterHint(group, id, input); }
                catch (Exception error) when (!(error is OutOfMemoryException)) { Swallowed(error); }
            }
            if (key && ErrorHandler.Succeeded(result) && !completion.IsCompletionActive(view) && (committing || view.TextSnapshot != before))
            {
                try { package!.TryFillAfterCommit(view); }
                catch (Exception error) when (!(error is OutOfMemoryException)) { Swallowed(error); }
            }
            return result;
        }

        internal static void Swallowed(Exception error) =>
            Microsoft.VisualStudio.Shell.ActivityLog.TryLogWarning("Querywright", "Editor command failed: " + error.GetType().Name + " at " + error.TargetSite?.DeclaringType?.FullName + "." + error.TargetSite?.Name);

        public int QueryStatus(ref Guid group, uint count, OLECMD[] commands, IntPtr text)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            int result = Next?.QueryStatus(ref group, count, commands, text) ?? (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            // Keep F12 available even where the host has no native definition for the token (variables, aliases, CTEs).
            for (int i = 0; i < count; i++)
                if (IsGoToDefinition(group, commands[i].cmdID))
                {
                    commands[i].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED);
                    result = VSConstants.S_OK;
                }
            return result;
        }
    }

    [Export(typeof(IAsyncCompletionSourceProvider))]
    [Name("Querywright schema completion")]
    [ContentType("text")]
    internal sealed class CompletionSourceProvider : IAsyncCompletionSourceProvider
    {
        public IAsyncCompletionSource GetOrCreate(ITextView textView) =>
            textView.Properties.GetOrCreateSingletonProperty(() => new CompletionSource());
    }

    /// <summary>Popup suggestions while typing: keywords, variables and aliases always; objects from live or offline metadata.</summary>
    internal sealed class CompletionSource : IAsyncCompletionSource
    {
        private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
        private volatile IReadOnlyList<SchemaTable>? tables;
        private volatile IReadOnlyList<string>? databases;
        private volatile IReadOnlyList<SchemaProcedure>? procedures;
        private volatile Func<string, IReadOnlyList<SchemaTable>>? otherDatabase;

        public CompletionStartData InitializeCompletion(CompletionTrigger trigger, SnapshotPoint location, CancellationToken token)
        {
            if (!EditorListener.IsSql(location.Snapshot.ContentType) || WorkbenchPackage.Instance == null)
                return CompletionStartData.DoesNotParticipateInCompletion;
            // Connection lookup needs the UI thread, where the broker normally calls this.
            if (Microsoft.VisualStudio.Shell.ThreadHelper.CheckAccess())
            {
                tables = WorkbenchPackage.Instance.CurrentTables();
                databases = WorkbenchPackage.Instance.CurrentDatabases();
                otherDatabase = WorkbenchPackage.Instance.CurrentOtherDatabase();
                procedures = WorkbenchPackage.Instance.CurrentProcedures(""); // live only; the script is parsed off the UI thread
            }
            if (trigger.Reason == CompletionTriggerReason.Insertion && WorkbenchPackage.Instance.Options?.SuggestWhileTyping == false)
                return CompletionStartData.DoesNotParticipateInCompletion;
            if (trigger.Reason == CompletionTriggerReason.Insertion &&
                !(char.IsLetter(trigger.Character) || trigger.Character == '_' || trigger.Character == '.' || trigger.Character == ' '))
                return CompletionStartData.DoesNotParticipateInCompletion;
            var snapshot = location.Snapshot;
            int start = location.Position, end = location.Position;
            while (start > 0 && IsWord(snapshot[start - 1])) start--;
            while (end < snapshot.Length && IsWord(snapshot[end])) end++;
            return new CompletionStartData(CompletionParticipation.ProvidesItems, new SnapshotSpan(snapshot, start, end - start));
        }

        public async Task<CompletionContext> GetCompletionContextAsync(IAsyncCompletionSession session, CompletionTrigger trigger,
            SnapshotPoint location, SnapshotSpan applicableTo, CancellationToken token)
        {
            var package = WorkbenchPackage.Instance;
            if (package == null) return CompletionContext.Empty;
            string sql = location.Snapshot.GetText();
            int position = location.Position;
            var tables = this.tables;
            var databases = this.databases;
            var live = this.procedures;
            var other = this.otherDatabase;
            // Mouse clicks commit too; any committed procedure gets its parameters.
            if (!session.Properties.ContainsProperty(typeof(CompletionSource)))
            {
                session.Properties.AddProperty(typeof(CompletionSource), true);
                session.ItemCommitted += (s, e) => { _ = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await Task.Yield(); // after the commit edit and the key that caused it
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (session.TextView is IWpfTextView view) WorkbenchPackage.Instance?.TryFillAfterCommit(view);
                }); };
            }
            var result = await Task.Run(() =>
            {
                try
                {
                    var procedures = live;
                    try
                    {
                        if (sql.Length <= 1_000_000 && sql.IndexOf("CREATE", StringComparison.OrdinalIgnoreCase) >= 0)
                            procedures = SqlAssist.ProceduresFromScript(sql).Concat(live ?? Array.Empty<SchemaProcedure>()).ToArray();
                    }
                    catch (Exception error) when (!(error is OutOfMemoryException)) { }
                    return SqlCompletion.Complete(sql, position, tables, databases: databases, procedures: procedures,
                        qualifySingleTable: WorkbenchPackage.Instance?.Options?.QualifySingleTable == true, otherDatabase: other); // null tables: keywords, functions, variables
                }
                // ponytail: typing must never raise dialogs; explicit commands report schema errors.
                catch (Exception error) when (!(error is OutOfMemoryException)) { return null; }
            }, token).ConfigureAwait(false);
            bool member = applicableTo.Start.Position > 0 && location.Snapshot[applicableTo.Start.Position - 1] == '.';
            // After FROM/JOIN/INTO/UPDATE/TABLE the user wants a table, not a snippet.
            int back = applicableTo.Start.Position;
            while (back > 0 && char.IsWhiteSpace(location.Snapshot[back - 1])) back--;
            int wordStart = back;
            while (wordStart > 0 && char.IsLetter(location.Snapshot[wordStart - 1])) wordStart--;
            bool expectsTable = back < applicableTo.Start.Position && System.Array.IndexOf(new[] { "FROM", "JOIN", "INTO", "UPDATE", "TABLE" },
                location.Snapshot.GetText(wordStart, back - wordStart).ToUpperInvariant()) >= 0;
            var snippets = applicableTo.IsEmpty || member || expectsTable ? Array.Empty<KeyValuePair<string, string>>() : package.SnippetList();
            if ((result == null || result.Items.Count == 0) && snippets.Count == 0) return CompletionContext.Empty;
            // Snippets insert their shortcut; Tab then expands it (see EditorCommandFilter).
            var items = snippets.Select(s => new VsCompletionItem(s.Key, this, null!, ImmutableArray<CompletionFilter>.Empty,
                    "snippet: " + s.Value, s.Key, "\uFFFF" + s.Key, s.Key, ImmutableArray<ImageElement>.Empty))
                // Sort text keeps the core ranking (in-scope columns grouped per table) instead of the editor's alphabetical default.
                .Concat((result?.Items ?? Array.Empty<Querywright.Core.CompletionItem>()).Select((i, n) => new VsCompletionItem(i.Name, this, null!, ImmutableArray<CompletionFilter>.Empty,
                    i.Description, i.InsertText, n.ToString("D6"), i.Name, ImmutableArray<ImageElement>.Empty))).ToImmutableArray();
            // Soft selection after a space so Enter still inserts a new line.
            return new CompletionContext(items, null, applicableTo.IsEmpty ? InitialSelectionHint.SoftSelection : InitialSelectionHint.RegularSelection);
        }

        public Task<object> GetDescriptionAsync(IAsyncCompletionSession session, VsCompletionItem item, CancellationToken token) =>
            Task.FromResult<object>(item.Suffix);
    }

    [Export(typeof(IAsyncQuickInfoSourceProvider))]
    [Name("Querywright quick info")]
    [ContentType("text")]
    internal sealed class QuickInfoSourceProvider : IAsyncQuickInfoSourceProvider
    {
        public IAsyncQuickInfoSource? TryCreateQuickInfoSource(ITextBuffer textBuffer) =>
            EditorListener.IsSql(textBuffer.ContentType) ? textBuffer.Properties.GetOrCreateSingletonProperty(() => new QuickInfoSource()) : null;
    }

    /// <summary>Hover: variable types, procedure parameters, table columns. Reads cached metadata only; never queries on hover.</summary>
    internal sealed class QuickInfoSource : IAsyncQuickInfoSource
    {
        private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#';

        public async Task<QuickInfoItem?> GetQuickInfoItemAsync(IAsyncQuickInfoSession session, CancellationToken token)
        {
            var package = WorkbenchPackage.Instance;
            var point = session.GetTriggerPoint(session.TextView.TextBuffer.CurrentSnapshot);
            if (package == null || point == null) return null;
            var snapshot = point.Value.Snapshot;
            int position = point.Value.Position;
            if (session.TextView.Properties.TryGetProperty(EditorCommandFilter.HintKey, out int hintAt))
            {
                session.TextView.Properties.RemoveProperty(EditorCommandFilter.HintKey);
                if (hintAt == position) return await HintAsync(package, snapshot, position, token);
            }
            if (package.Options?.HoverInfo == false) return null;
            // END: which block it closes, when the opening line is off screen.
            var block = BlockCache.For(snapshot.TextBuffer).Latest is { } cached && cached.Snapshot == snapshot
                ? cached.Blocks.FirstOrDefault(b => BlockCache.On(position, b.CloseStart, b.CloseLength)) : null;
            if (block != null)
            {
                var line = snapshot.GetLineFromPosition(block.HeaderStart >= 0 ? block.HeaderStart : block.OpenStart);
                string opener = line.GetText().Trim();
                return new QuickInfoItem(snapshot.CreateTrackingSpan(block.CloseStart, block.CloseLength, SpanTrackingMode.EdgeInclusive),
                    new ClassifiedTextElement(new ClassifiedTextRun("text", "line " + (line.LineNumber + 1) + ": " + (opener.Length > 120 ? opener.Substring(0, 120) + "..." : opener))));
            }
            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            var tables = package.CurrentTables();
            var live = package.CurrentProcedures(""); // live only; the script is parsed off the UI thread below
            string sql = snapshot.GetText();
            var found = await Task.Run<(string? Text, IReadOnlyList<SchemaProcedure>? Procedures)>(() =>
            {
                try
                {
                    var procedures = sql.Length > 1_000_000 ? live : SqlAssist.ProceduresFromScript(sql).Concat(live).ToArray();
                    return (SqlAssist.Describe(sql, position, tables, procedures), procedures);
                }
                catch (Exception error) when (!(error is OutOfMemoryException)) { return (null, null); }
            }, token).ConfigureAwait(false);
            string? text = found.Text;
            if (string.IsNullOrEmpty(text)) return null;
            int start = position, end = position;
            while (start > 0 && IsWord(snapshot[start - 1])) start--;
            while (end < snapshot.Length && IsWord(snapshot[end])) end++;
            var span = snapshot.CreateTrackingSpan(start, end - start, SpanTrackingMode.EdgeInclusive);

            // Tables, views and procedures: a link that opens the Script/Summary popup (queried only on click).
            string first = text!.Split('\n')[0].TrimEnd('\r');
            var table = tables?.FirstOrDefault(t => first == "table " + t.Schema + "." + t.Name);
            var procedure = table == null ? found.Procedures?.FirstOrDefault(p => first == "procedure " + p.Schema + "." + p.Name) : null;
            if (table != null || procedure != null)
            {
                string schema = table?.Schema ?? procedure!.Schema, name = table?.Name ?? procedure!.Name;
                string kind = table == null ? "Procedure" : table.IsView ? "View" : "Table";
                int image = table == null ? KnownImageIds.StoredProcedure : table.IsView ? KnownImageIds.View : KnownImageIds.Table;
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(schema + "." + name + " (" + kind + ")"));
                link.Click += (s, e) =>
                {
                    _ = session.DismissAsync();
                    _ = package.JoinableTaskFactory.RunAsync(() => package.ShowObjectAsync(schema, name));
                };
                return new QuickInfoItem(span, new ContainerElement(ContainerElementStyle.Wrapped,
                    new ImageElement(new ImageId(KnownImageIds.ImageCatalogGuid, image)),
                    new System.Windows.Controls.TextBlock(link) { Margin = new System.Windows.Thickness(4, 0, 0, 0) }));
            }
            return new QuickInfoItem(span, new ContainerElement(ContainerElementStyle.Stacked,
                text.Split('\n').Select(line => (object)new ClassifiedTextElement(new ClassifiedTextRun("text", line.TrimEnd('\r'))))));
        }

        private static async Task<QuickInfoItem?> HintAsync(WorkbenchPackage package, ITextSnapshot snapshot, int position, CancellationToken token)
        {
            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            var live = package.CurrentProcedures("");
            string sql = snapshot.GetText();
            var hint = await Task.Run(() =>
            {
                try
                {
                    var procedures = sql.Length > 1_000_000 || sql.IndexOf("CREATE", StringComparison.OrdinalIgnoreCase) < 0 ? live
                        : SqlAssist.ProceduresFromScript(sql).Concat(live).ToArray();
                    return SqlAssist.ParameterHintAt(sql, position, procedures);
                }
                catch (Exception error) when (!(error is OutOfMemoryException)) { return null; }
            }, token).ConfigureAwait(false);
            if (hint == null) return null;
            var runs = new List<ClassifiedTextRun> { new ClassifiedTextRun("text", hint.Name + (hint.Parameters.Count == 0 ? " (no parameters)" : " ")) };
            for (int i = 0; i < hint.Parameters.Count; i++)
            {
                if (i > 0) runs.Add(new ClassifiedTextRun("text", ", "));
                runs.Add(new ClassifiedTextRun("text", hint.Parameters[i], i == hint.Current ? ClassifiedTextRunStyle.Bold : ClassifiedTextRunStyle.Plain));
            }
            return new QuickInfoItem(snapshot.CreateTrackingSpan(position, 0, SpanTrackingMode.EdgeInclusive), new ClassifiedTextElement(runs));
        }

        public void Dispose() { }
    }
}

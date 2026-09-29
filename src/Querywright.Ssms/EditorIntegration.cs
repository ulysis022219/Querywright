#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
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

        internal static bool IsSql(IContentType type) =>
            type.IsOfType("SQL") || type.IsOfType("T-SQL") || type.TypeName.IndexOf("SQL", StringComparison.OrdinalIgnoreCase) >= 0;

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
            poll.Tick += (s, e) => { if (view.HasAggregateFocus && WorkbenchPackage.Instance?.LiveMetadataEnabled == true) LiveMetadata.TryGet(LiveMetadata.Capture()); };
            view.Closed += (s, e) => poll.Stop();
            poll.Start();
            // Tab history: one file per window, saved every minute when changed and on close. Local only; opt out in options.
            var historyId = Guid.NewGuid();
            ITextSnapshot? saved = null;
            void SaveHistory()
            {
                var snapshot = view.TextBuffer.CurrentSnapshot;
                if (snapshot == saved || WorkbenchPackage.Instance?.TabHistoryEnabled != true) return;
                saved = snapshot;
                string text = snapshot.GetText();
                _ = System.Threading.Tasks.Task.Run(() => TabHistory.Save(historyId, text));
            }
            var history = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            history.Tick += (s, e) => SaveHistory();
            view.Closed += (s, e) => { history.Stop(); SaveHistory(); };
            history.Start();
            var filter = new EditorCommandFilter(view, Completion);
            if (ErrorHandler.Succeeded(adapter.AddCommandFilter(filter, out var next))) filter.Next = next;
            SelfTest.Adapter = adapter;
            SelfTest.View = view;
        }
    }

    internal sealed class EditorCommandFilter : IOleCommandTarget
    {
        private readonly IWpfTextView view;
        private readonly IAsyncCompletionBroker completion;
        internal IOleCommandTarget? Next;
        internal EditorCommandFilter(IWpfTextView view, IAsyncCompletionBroker completion) { this.view = view; this.completion = completion; }

        private static bool IsTab(Guid group, uint id) => group == VSConstants.VSStd2K && id == (uint)VSConstants.VSStd2KCmdID.TAB;
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
                // SQL Prompt: Tab on a typed snippet shortcut expands it even while the suggestion list is open.
                if (IsTab(group, id) && completion.IsCompletionActive(view) && (package.HasSnippetShortcut(view) || !SelectionMatches()))
                    completion.GetSession(view)?.Dismiss();
                if (IsTab(group, id) && !completion.IsCompletionActive(view))
                {
                    bool expanded = package.TryTabExpand(view);
                    Microsoft.VisualStudio.Shell.ActivityLog.TryLogInformation("Querywright", "Tab received; expanded=" + expanded);
                    if (expanded) return VSConstants.S_OK;
                }
                if (IsGoToDefinition(group, id) && package.TryGoToDefinition(view)) return VSConstants.S_OK;
            }
            return Next?.Exec(ref group, id, options, input, output) ?? (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
        }

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

        public CompletionStartData InitializeCompletion(CompletionTrigger trigger, SnapshotPoint location, CancellationToken token)
        {
            if (!EditorListener.IsSql(location.Snapshot.ContentType) || WorkbenchPackage.Instance == null)
                return CompletionStartData.DoesNotParticipateInCompletion;
            // Connection lookup needs the UI thread, where the broker normally calls this.
            if (Microsoft.VisualStudio.Shell.ThreadHelper.CheckAccess()) tables = WorkbenchPackage.Instance.CurrentTables();
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
            var result = await Task.Run(() =>
            {
                try
                {
                    return SqlCompletion.Complete(sql, position, tables); // null tables: keywords, functions, variables
                }
                // ponytail: typing must never raise dialogs; explicit commands report schema errors.
                catch (Exception error) when (!(error is OutOfMemoryException)) { return null; }
            }, token).ConfigureAwait(false);
            bool member = applicableTo.Start.Position > 0 && location.Snapshot[applicableTo.Start.Position - 1] == '.';
            var snippets = applicableTo.IsEmpty || member ? Array.Empty<KeyValuePair<string, string>>() : package.SnippetList();
            if ((result == null || result.Items.Count == 0) && snippets.Count == 0) return CompletionContext.Empty;
            // Snippets insert their shortcut; Tab then expands it (see EditorCommandFilter).
            var items = snippets.Select(s => new VsCompletionItem(s.Key, this, null!, ImmutableArray<CompletionFilter>.Empty,
                    "snippet: " + s.Value, s.Key, s.Key, s.Key, ImmutableArray<ImageElement>.Empty))
                .Concat((result?.Items ?? Array.Empty<Querywright.Core.CompletionItem>()).Select(i => new VsCompletionItem(i.Name, this, null!, ImmutableArray<CompletionFilter>.Empty,
                    i.Description, i.InsertText, i.Name, i.Name, ImmutableArray<ImageElement>.Empty))).ToImmutableArray();
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
            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            var tables = package.CurrentTables();
            var live = package.CurrentProcedures(""); // live only; the script is parsed off the UI thread below
            string sql = snapshot.GetText();
            var text = await Task.Run(() =>
            {
                try
                {
                    var procedures = sql.Length > 1_000_000 ? live : SqlAssist.ProceduresFromScript(sql).Concat(live).ToArray();
                    return SqlAssist.Describe(sql, position, tables, procedures);
                }
                catch (Exception error) when (!(error is OutOfMemoryException)) { return null; }
            }, token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(text)) return null;
            int start = position, end = position;
            while (start > 0 && IsWord(snapshot[start - 1])) start--;
            while (end < snapshot.Length && IsWord(snapshot[end])) end++;
            var span = snapshot.CreateTrackingSpan(start, end - start, SpanTrackingMode.EdgeInclusive);
            return new QuickInfoItem(span, new ContainerElement(ContainerElementStyle.Stacked,
                text!.Split('\n').Select(line => (object)new ClassifiedTextElement(new ClassifiedTextRun("text", line.TrimEnd('\r'))))));
        }

        public void Dispose() { }
    }
}

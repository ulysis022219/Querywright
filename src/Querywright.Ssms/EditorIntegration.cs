#nullable enable
using System;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
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
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType("SQL")]
    [ContentType("T-SQL")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class EditorListener : IVsTextViewCreationListener
    {
        [Import] internal IVsEditorAdaptersFactoryService Adapters = null!;
        [Import] internal IAsyncCompletionBroker Completion = null!;

        public void VsTextViewCreated(IVsTextView adapter)
        {
            var view = Adapters.GetWpfTextView(adapter);
            if (view == null) return;
            var filter = new EditorCommandFilter(view, Completion);
            if (ErrorHandler.Succeeded(adapter.AddCommandFilter(filter, out var next))) filter.Next = next;
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

        public int Exec(ref Guid group, uint id, uint options, IntPtr input, IntPtr output)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            var package = WorkbenchPackage.Instance;
            if (package != null)
            {
                if (IsTab(group, id) && !completion.IsCompletionActive(view) && package.TryTabExpand(view)) return VSConstants.S_OK;
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
    [ContentType("SQL")]
    [ContentType("T-SQL")]
    internal sealed class CompletionSourceProvider : IAsyncCompletionSourceProvider
    {
        public IAsyncCompletionSource GetOrCreate(ITextView textView) =>
            textView.Properties.GetOrCreateSingletonProperty(() => new CompletionSource());
    }

    /// <summary>Popup suggestions while typing, from the offline schema snapshot.</summary>
    internal sealed class CompletionSource : IAsyncCompletionSource
    {
        private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

        public CompletionStartData InitializeCompletion(CompletionTrigger trigger, SnapshotPoint location, CancellationToken token)
        {
            if (WorkbenchPackage.Instance?.SchemaConfigured != true) return CompletionStartData.DoesNotParticipateInCompletion;
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
            var result = await Task.Run(() =>
            {
                try
                {
                    var tables = package.LoadSchema();
                    return tables == null ? null : SqlCompletion.Complete(sql, position, tables);
                }
                // ponytail: typing must never raise dialogs; explicit commands report schema errors.
                catch (Exception error) when (!(error is OutOfMemoryException)) { return null; }
            }, token).ConfigureAwait(false);
            if (result == null || result.Items.Count == 0) return CompletionContext.Empty;
            var items = result.Items.Select(i => new VsCompletionItem(i.Name, this, null!, ImmutableArray<CompletionFilter>.Empty,
                i.Description, i.InsertText, i.Name, i.Name, ImmutableArray<ImageElement>.Empty)).ToImmutableArray();
            // Soft selection after a space so Enter still inserts a new line.
            return new CompletionContext(items, null, applicableTo.IsEmpty ? InitialSelectionHint.SoftSelection : InitialSelectionHint.RegularSelection);
        }

        public Task<object> GetDescriptionAsync(IAsyncCompletionSession session, VsCompletionItem item, CancellationToken token) =>
            Task.FromResult<object>(item.Suffix);
    }
}

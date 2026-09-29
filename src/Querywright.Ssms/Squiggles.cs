using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using Querywright.Core;

namespace Querywright.Ssms
{
    [Export(typeof(IViewTaggerProvider))]
    [ContentType("text")]
    [TagType(typeof(IErrorTag))]
    internal sealed class SquiggleTaggerProvider : IViewTaggerProvider
    {
        public ITagger<T> CreateTagger<T>(ITextView view, ITextBuffer buffer) where T : ITag
        {
            if (buffer != view.TextBuffer || !EditorListener.IsSql(buffer.ContentType)) return null;
            return buffer.Properties.GetOrCreateSingletonProperty(() => new SquiggleTagger(buffer)) as ITagger<T>;
        }
    }

    /// <summary>Analysis rules as squiggles, re-run in the background after typing pauses. Syntax errors are left to SSMS.</summary>
    internal sealed class SquiggleTagger : ITagger<IErrorTag>
    {
        private readonly ITextBuffer buffer;
        private CancellationTokenSource pending;
        private (ITextSnapshot Snapshot, IReadOnlyList<(SqlDiagnostic Issue, RuleSeverity Severity)> Issues)? latest;

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        internal SquiggleTagger(ITextBuffer buffer)
        {
            this.buffer = buffer;
            buffer.Changed += (sender, args) => Schedule();
            Schedule();
        }

        private static (string Path, DateTime Written, WorkbenchSettings Settings) loaded;

        // Re-read only when the file changes; every pause in typing would otherwise parse it again.
        private static WorkbenchSettings Settings(string path)
        {
            if (path.Length == 0) return new WorkbenchSettings();
            try
            {
                var written = System.IO.File.GetLastWriteTimeUtc(path);
                var cached = loaded;
                if (cached.Path == path && cached.Written == written) return cached.Settings;
                var settings = WorkbenchSettings.Load(path);
                loaded = (path, written, settings);
                return settings;
            }
            catch (Exception error) when (!(error is OutOfMemoryException)) { return new WorkbenchSettings(); }
        }

        private void Schedule()
        {
            pending?.Cancel();
            var cancellation = pending = new CancellationTokenSource();
            var snapshot = buffer.CurrentSnapshot;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(700, cancellation.Token);
                    if (snapshot.Length > 2_000_000) return;
                    if (WorkbenchPackage.Instance?.Options?.LiveAnalysis == false)
                    {
                        // Clears squiggles already shown when the option is turned off.
                        if (latest != null) { latest = null; TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length))); }
                        return;
                    }
                    string settingsFile = WorkbenchPackage.Instance?.SettingsFile ?? "";
                    var settings = Settings(settingsFile);
                    var result = SqlAnalysis.Analyze(snapshot.GetText(), cancellation.Token, settings);
                    latest = (snapshot, result.Diagnostics.Where(d => d.Rule.StartsWith("SW", StringComparison.Ordinal))
                        .Select(d => (d, settings.Severity(d.Rule))).Where(d => d.Item2 != RuleSeverity.Disabled).ToArray());
                    TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
                }
                catch (OperationCanceledException) { }
                catch (Exception error) when (!(error is OutOfMemoryException))
                {
                    Microsoft.VisualStudio.Shell.ActivityLog.TryLogWarning("Querywright", "Live analysis failed: " + error.GetType().Name);
                }
            });
        }

        public IEnumerable<ITagSpan<IErrorTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            var current = latest;
            if (current == null || spans.Count == 0) yield break;
            var (snapshot, issues) = current.Value;
            var target = spans[0].Snapshot;
            foreach (var (issue, severity) in issues)
            {
                if (issue.Offset < 0 || issue.Offset + issue.Length > snapshot.Length) continue;
                var span = new SnapshotSpan(snapshot, issue.Offset, Math.Min(Math.Max(1, issue.Length), snapshot.Length - issue.Offset))
                    .TranslateTo(target, SpanTrackingMode.EdgeExclusive);
                if (!spans.IntersectsWith(new NormalizedSnapshotSpanCollection(span))) continue;
                string type = severity == RuleSeverity.Error ? PredefinedErrorTypeNames.SyntaxError
                    : severity == RuleSeverity.Info ? PredefinedErrorTypeNames.HintedSuggestion : PredefinedErrorTypeNames.Warning;
                yield return new TagSpan<IErrorTag>(span, new ErrorTag(type, issue.Rule + ": " + issue.Message));
            }
        }
    }
}

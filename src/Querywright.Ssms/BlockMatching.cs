using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using Querywright.Core;

namespace Querywright.Ssms
{
    /// <summary>BEGIN/END pairs of a buffer, recomputed in the background after typing pauses.</summary>
    internal sealed class BlockCache
    {
        private readonly ITextBuffer buffer;
        private CancellationTokenSource pending;
        private (ITextSnapshot Snapshot, IReadOnlyList<SqlBlock> Blocks)? latest;
        internal event Action<ITextSnapshot> Updated;

        private BlockCache(ITextBuffer buffer)
        {
            this.buffer = buffer;
            buffer.Changed += (sender, args) => Schedule(300);
            Schedule(0);
        }

        internal static BlockCache For(ITextBuffer buffer) => buffer.Properties.GetOrCreateSingletonProperty(() => new BlockCache(buffer));

        private void Schedule(int delay)
        {
            pending?.Cancel();
            var cancellation = pending = new CancellationTokenSource();
            var snapshot = buffer.CurrentSnapshot;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, cancellation.Token);
                    latest = (snapshot, Compute(snapshot));
                    Updated?.Invoke(snapshot);
                }
                catch (OperationCanceledException) { }
            });
        }

        private static IReadOnlyList<SqlBlock> Compute(ITextSnapshot snapshot)
        {
            try { return snapshot.Length > 2_000_000 ? Array.Empty<SqlBlock>() : SqlNavigation.Blocks(snapshot.GetText()); }
            catch (Exception error) when (!(error is OutOfMemoryException)) { return Array.Empty<SqlBlock>(); }
        }

        /// <summary>Latest background result, possibly for an older snapshot.</summary>
        internal (ITextSnapshot Snapshot, IReadOnlyList<SqlBlock> Blocks)? Latest => latest;

        /// <summary>Blocks for exactly this snapshot; computes now when the background result is stale (explicit commands only).</summary>
        internal IReadOnlyList<SqlBlock> Current(ITextSnapshot snapshot) =>
            latest is { } found && found.Snapshot == snapshot ? found.Blocks : Compute(snapshot);

        internal static bool On(int position, int start, int length) => start >= 0 && position >= start && position <= start + length;

        internal static SqlBlock At(IReadOnlyList<SqlBlock> blocks, int position) =>
            blocks.FirstOrDefault(b => On(position, b.OpenStart, b.OpenLength) || On(position, b.CloseStart, b.CloseLength) || On(position, b.HeaderStart, b.HeaderLength));

        internal static IEnumerable<Span> Spans(SqlBlock b)
        {
            if (b.HeaderStart >= 0) yield return new Span(b.HeaderStart, b.HeaderLength);
            yield return new Span(b.OpenStart, b.OpenLength);
            yield return new Span(b.CloseStart, b.CloseLength);
        }
    }

    [Export(typeof(IViewTaggerProvider))]
    [ContentType("text")]
    [TagType(typeof(ClassificationTag))]
    internal sealed class BlockColorTaggerProvider : IViewTaggerProvider
    {
        [Import] internal IClassificationTypeRegistryService Registry = null;

        public ITagger<T> CreateTagger<T>(ITextView view, ITextBuffer buffer) where T : ITag =>
            buffer != view.TextBuffer || !EditorListener.IsSql(buffer.ContentType) ? null
                : buffer.Properties.GetOrCreateSingletonProperty(() => new BlockColorTagger(buffer, Registry)) as ITagger<T>;
    }

    [Export(typeof(IViewTaggerProvider))]
    [ContentType("text")]
    [TagType(typeof(TextMarkerTag))]
    internal sealed class BlockCaretTaggerProvider : IViewTaggerProvider
    {
        public ITagger<T> CreateTagger<T>(ITextView view, ITextBuffer buffer) where T : ITag =>
            buffer != view.TextBuffer || !EditorListener.IsSql(buffer.ContentType) ? null
                : view.Properties.GetOrCreateSingletonProperty(() => new BlockCaretTagger(view)) as ITagger<T>;
    }

    /// <summary>Colors each BEGIN/END pair (and its IF/WHILE/ELSE) by nesting level, so each END shows which block it closes.</summary>
    internal sealed class BlockColorTagger : ITagger<ClassificationTag>
    {
        private readonly BlockCache cache;
        private readonly ClassificationTag[] levels;
        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        internal BlockColorTagger(ITextBuffer buffer, IClassificationTypeRegistryService registry)
        {
            levels = BlockLevels.Names.Select(n => new ClassificationTag(registry.GetClassificationType(n))).ToArray();
            cache = BlockCache.For(buffer);
            cache.Updated += snapshot => TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
        }

        public IEnumerable<ITagSpan<ClassificationTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0 || WorkbenchPackage.Instance?.Options?.ColorBlocks == false) yield break;
            var latest = cache.Latest;
            if (latest == null) yield break;
            var (snapshot, blocks) = latest.Value;
            var target = spans[0].Snapshot;
            int from = spans[0].Start.TranslateTo(snapshot, PointTrackingMode.Negative), to = spans[spans.Count - 1].End.TranslateTo(snapshot, PointTrackingMode.Positive);
            foreach (var block in blocks)
            {
                if (block.CloseStart + block.CloseLength < from || Math.Min(block.OpenStart, block.HeaderStart < 0 ? int.MaxValue : block.HeaderStart) > to) continue;
                var tag = levels[block.Depth % levels.Length];
                foreach (var span in BlockCache.Spans(block))
                {
                    if (span.End < from || span.Start > to) continue;
                    yield return new TagSpan<ClassificationTag>(new SnapshotSpan(snapshot, span).TranslateTo(target, SpanTrackingMode.EdgeExclusive), tag);
                }
            }
        }
    }

    /// <summary>Brace-style highlight of the pair under the caret.</summary>
    internal sealed class BlockCaretTagger : ITagger<TextMarkerTag>
    {
        private static readonly TextMarkerTag Marker = new TextMarkerTag("bracehighlight");
        private readonly ITextView view;
        private readonly BlockCache cache;
        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        internal BlockCaretTagger(ITextView view)
        {
            this.view = view;
            cache = BlockCache.For(view.TextBuffer);
            view.Caret.PositionChanged += (sender, args) => Refresh(view.TextSnapshot);
            cache.Updated += snapshot => Refresh(view.TextSnapshot);
        }

        private void Refresh(ITextSnapshot snapshot) => TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));

        public IEnumerable<ITagSpan<TextMarkerTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0) yield break;
            var latest = cache.Latest;
            if (latest == null) yield break;
            var (snapshot, blocks) = latest.Value;
            var target = spans[0].Snapshot;
            var caret = view.Caret.Position.BufferPosition;
            if (caret.Snapshot != target) yield break;
            var block = BlockCache.At(blocks, caret.TranslateTo(snapshot, PointTrackingMode.Negative));
            if (block == null) yield break;
            foreach (var span in BlockCache.Spans(block))
                yield return new TagSpan<TextMarkerTag>(new SnapshotSpan(snapshot, span).TranslateTo(target, SpanTrackingMode.EdgeExclusive), Marker);
        }
    }

    /// <summary>Six pair colors, editable under Tools > Options > Fonts and Colors.</summary>
    internal static class BlockLevels
    {
        internal static readonly string[] Names = { "Querywright Block 1", "Querywright Block 2", "Querywright Block 3", "Querywright Block 4", "Querywright Block 5", "Querywright Block 6" };

        // ponytail: mid-tone colors that read on both light and dark themes; users can change them in Fonts and Colors.
        [Export, Name("Querywright Block 1")] internal static ClassificationTypeDefinition Level1 = null;
        [Export, Name("Querywright Block 2")] internal static ClassificationTypeDefinition Level2 = null;
        [Export, Name("Querywright Block 3")] internal static ClassificationTypeDefinition Level3 = null;
        [Export, Name("Querywright Block 4")] internal static ClassificationTypeDefinition Level4 = null;
        [Export, Name("Querywright Block 5")] internal static ClassificationTypeDefinition Level5 = null;
        [Export, Name("Querywright Block 6")] internal static ClassificationTypeDefinition Level6 = null;
    }

    internal abstract class BlockFormat : ClassificationFormatDefinition
    {
        protected BlockFormat(int level, byte r, byte g, byte b)
        {
            DisplayName = "Querywright BEGIN/END level " + level;
            ForegroundColor = Color.FromRgb(r, g, b);
            IsBold = true;
        }
    }

    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = "Querywright Block 1"), Name("Querywright Block 1"), UserVisible(true), Order(After = Priority.High)]
    internal sealed class BlockFormat1 : BlockFormat { public BlockFormat1() : base(1, 0xD1, 0x87, 0x00) { } }
    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = "Querywright Block 2"), Name("Querywright Block 2"), UserVisible(true), Order(After = Priority.High)]
    internal sealed class BlockFormat2 : BlockFormat { public BlockFormat2() : base(2, 0xB0, 0x4F, 0xC4) { } }
    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = "Querywright Block 3"), Name("Querywright Block 3"), UserVisible(true), Order(After = Priority.High)]
    internal sealed class BlockFormat3 : BlockFormat { public BlockFormat3() : base(3, 0x1F, 0x8F, 0xD6) { } }
    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = "Querywright Block 4"), Name("Querywright Block 4"), UserVisible(true), Order(After = Priority.High)]
    internal sealed class BlockFormat4 : BlockFormat { public BlockFormat4() : base(4, 0x2E, 0x9E, 0x5B) { } }
    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = "Querywright Block 5"), Name("Querywright Block 5"), UserVisible(true), Order(After = Priority.High)]
    internal sealed class BlockFormat5 : BlockFormat { public BlockFormat5() : base(5, 0xD4, 0x55, 0x3B) { } }
    [Export(typeof(EditorFormatDefinition)), ClassificationType(ClassificationTypeNames = "Querywright Block 6"), Name("Querywright Block 6"), UserVisible(true), Order(After = Priority.High)]
    internal sealed class BlockFormat6 : BlockFormat { public BlockFormat6() : base(6, 0x0F, 0xA3, 0xA3) { } }
}

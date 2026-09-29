using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Querywright.Core
{
    /// <summary>Formatting options; defaults match the SQL Server script generator. Attributes label the style editor.</summary>
    public sealed class FormattingStyle
    {
        [Category("Layout"), DisplayName("Indent size"), Description("Spaces per indent level (1-16).")]
        public int IndentSize { get; set; } = 4;
        [Category("Casing"), DisplayName("Lowercase keywords"), Description("Write keywords such as SELECT and FROM in lowercase.")]
        public bool LowercaseKeywords { get; set; }
        [Category("Lists"), DisplayName("Leading commas"), Description("Put commas at the start of list lines instead of the end.")]
        public bool LeadingCommas { get; set; }
        [Category("Lists"), DisplayName("One column per line"), Description("Place each SELECT column on its own line.")]
        public bool MultilineColumns { get; set; } = true;
        [Category("Clauses"), DisplayName("New line before FROM")]
        public bool NewLineBeforeFrom { get; set; } = true;
        [Category("Clauses"), DisplayName("New line before WHERE")]
        public bool NewLineBeforeWhere { get; set; } = true;
        [Category("Clauses"), DisplayName("New line before JOIN")]
        public bool NewLineBeforeJoin { get; set; } = true;
        [Category("Clauses"), DisplayName("New line before GROUP BY, HAVING, ORDER BY")]
        public bool NewLineBeforeGrouping { get; set; } = true;
        [Category("Clauses"), DisplayName("Align clause bodies"), Description("Indent clause contents to a common column.")]
        public bool AlignClauseBodies { get; set; } = true;
        [Category("Lists"), DisplayName("One predicate per line"), Description("Place each AND/OR condition of WHERE on its own line.")]
        public bool MultilinePredicates { get; set; } = true;
        [Category("Lists"), DisplayName("One INSERT column per line")]
        public bool MultilineInsertColumns { get; set; }
        [Category("Statements"), DisplayName("Add semicolons"), Description("Terminate statements with semicolons.")]
        public bool IncludeSemicolons { get; set; }
        [Category("Statements"), DisplayName("Indent view body")]
        public bool IndentViewBody { get; set; }
    }

    public static class SqlFormatting
    {
        public static string Format(string sql, FormattingStyle? style = null, CancellationToken cancellation = default)
        {
            if (sql == null) throw new ArgumentNullException(nameof(sql));
            if (sql.Length > 1_000_000) throw new ArgumentException("Formatting input exceeds 1,000,000 characters.", nameof(sql));
            cancellation.ThrowIfCancellationRequested();
            var tokens = new TSql170Parser(true).GetTokenStream(new StringReader(sql), out var errors);
            if (errors.Count > 0) throw new FormatException("Cannot tokenize SQL for formatting.");
            var output = new StringBuilder();
            int offset = 0;
            foreach (var token in tokens.Where(t => t.TokenType == TSqlTokenType.Go))
            {
                // GO repetition belongs to the client, not T-SQL. Preserve separator lines verbatim.
                // Lexer tokens prevent GO inside strings/comments from splitting batches.
                int start = token.Offset == 0 ? 0 : sql.LastIndexOf('\n', token.Offset - 1) + 1;
                int newline = sql.IndexOf('\n', token.Offset);
                int end = newline < 0 ? sql.Length : newline + 1;
                string separator = sql.Substring(start, end - start);
                if (!Regex.IsMatch(separator.TrimEnd('\r', '\n'), @"^[ \t]*GO(?:[ \t]+[0-9]+)?[ \t]*(?:--[^\r\n]*)?$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) continue;
                output.Append(FormatBatch(sql.Substring(offset, start - offset), style, cancellation));
                if (output.Length > 0 && output[output.Length - 1] != '\n') output.AppendLine();
                output.Append(separator);
                offset = end;
            }
            output.Append(FormatBatch(sql.Substring(offset), style, cancellation));
            return output.ToString();
        }

        private static string FormatBatch(string sql, FormattingStyle? style, CancellationToken cancellation)
        {
            if (sql == null) throw new ArgumentNullException(nameof(sql));
            if (sql.Length > 1_000_000) throw new ArgumentException("Formatting input exceeds 1,000,000 characters.", nameof(sql));
            style = style ?? new FormattingStyle();
            if (style.IndentSize < 1 || style.IndentSize > 16) throw new ArgumentException("Indent size must be between 1 and 16.", nameof(style));
            cancellation.ThrowIfCancellationRequested();
            var parser = new TSql170Parser(true);
            var original = parser.Parse(new StringReader(sql), out var errors);
            if (errors.Count > 0) throw new FormatException($"Cannot format SQL: line {errors[0].Line}, column {errors[0].Column}: {errors[0].Message}");
            if (string.IsNullOrWhiteSpace(sql)) return sql;
            var generator = new Sql170ScriptGenerator(new SqlScriptGeneratorOptions
            {
                PreserveComments = true,
                IndentationSize = style.IndentSize,
                KeywordCasing = style.LowercaseKeywords ? KeywordCasing.Lowercase : KeywordCasing.Uppercase,
                CommaPlacement = style.LeadingCommas ? CommaPlacement.Leading : CommaPlacement.Trailing,
                MultilineSelectElementsList = style.MultilineColumns,
                NewLineBeforeFromClause = style.NewLineBeforeFrom,
                // ponytail: only generator options present in SSMS's older ScriptDOM (18.0.56) are used.
                NewLineBeforeWhereClause = style.NewLineBeforeWhere,
                NewLineBeforeJoinClause = style.NewLineBeforeJoin,
                NewLineBeforeGroupByClause = style.NewLineBeforeGrouping,
                NewLineBeforeHavingClause = style.NewLineBeforeGrouping,
                NewLineBeforeOrderByClause = style.NewLineBeforeGrouping,
                AlignClauseBodies = style.AlignClauseBodies,
                MultilineWherePredicatesList = style.MultilinePredicates,
                MultilineInsertTargetsList = style.MultilineInsertColumns,
                IncludeSemicolons = style.IncludeSemicolons,
                IndentViewBody = style.IndentViewBody
            });
            generator.GenerateScript(original, out var formatted);
            cancellation.ThrowIfCancellationRequested();
            var regenerated = parser.Parse(new StringReader(formatted), out var generatedErrors);
            if (generatedErrors.Count != 0) throw new InvalidOperationException("Formatter produced invalid SQL; original text retained.");
            // Verify sensitive tokens independently of regeneration so dropped literals/comments cannot pass silently.
            string[] Protected(TSqlFragment fragment)
            {
                var identifiers = new IdentifierTokens();
                fragment.Accept(identifiers);
                return fragment.ScriptTokenStream.Select((token, index) => new { token, index })
                .Where(item => identifiers.Indices.Contains(item.index) || IsProtected(item.token))
                .Select(item => item.token.TokenType + ":" + item.token.Text).ToArray();
            }
            bool IsProtected(TSqlParserToken t) => t.TokenType == TSqlTokenType.QuotedIdentifier ||
                    t.TokenType == TSqlTokenType.Variable ||
                    t.TokenType == TSqlTokenType.AsciiStringLiteral || t.TokenType == TSqlTokenType.UnicodeStringLiteral ||
                    t.TokenType == TSqlTokenType.Integer || t.TokenType == TSqlTokenType.Numeric ||
                    t.TokenType == TSqlTokenType.Real || t.TokenType == TSqlTokenType.HexLiteral ||
                    t.TokenType == TSqlTokenType.SingleLineComment || t.TokenType == TSqlTokenType.MultilineComment;
            if (!Protected(original).SequenceEqual(Protected(regenerated)))
                throw new InvalidOperationException("Formatter changed a protected token; original text retained.");
            var canonical = new Sql170ScriptGenerator(new SqlScriptGeneratorOptions());
            canonical.GenerateScript(original, out var originalShape);
            canonical.GenerateScript(regenerated, out var formattedShape);
            if (originalShape != formattedShape)
                throw new InvalidOperationException("Formatter changed SQL structure; original text retained.");
            return formatted;
        }

        public enum FileStatus { Unchanged, Changed, Failed }

        /// <summary>
        /// Formats .sql files. With <paramref name="write"/> false nothing is written (preview). Files keep their encoding
        /// (BOM or BOM-less UTF-8) and newline style; files that are not valid UTF-8/UTF-16, too large, or unparsable are
        /// reported and left untouched. Writes go through a temporary file and File.Replace.
        /// </summary>
        public static IReadOnlyList<(string Path, FileStatus Status, string? Message)> FormatFiles(
            IEnumerable<string> paths, FormattingStyle? style, bool write, CancellationToken cancellation = default)
        {
            var results = new List<(string, FileStatus, string?)>();
            foreach (string path in paths)
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    byte[] bytes = File.ReadAllBytes(path);
                    if (bytes.Length > 2_000_000) { results.Add((path, FileStatus.Failed, "Larger than 2 MB.")); continue; }
                    Encoding encoding;
                    int preamble;
                    if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { encoding = new UTF8Encoding(true, true); preamble = 3; }
                    else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { encoding = new UnicodeEncoding(false, true, true); preamble = 2; }
                    else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { encoding = new UnicodeEncoding(true, true, true); preamble = 2; }
                    else { encoding = new UTF8Encoding(false, true); preamble = 0; }
                    string text;
                    try { text = encoding.GetString(bytes, preamble, bytes.Length - preamble); }
                    catch (DecoderFallbackException) { results.Add((path, FileStatus.Failed, "Not UTF-8 or UTF-16 text; save it as UTF-8 first.")); continue; }
                    string formatted = Format(text, style, cancellation);
                    string newline = text.Contains("\r\n") ? "\r\n" : "\n";
                    formatted = Regex.Replace(formatted, "\r?\n", newline);
                    if (formatted == text) { results.Add((path, FileStatus.Unchanged, null)); continue; }
                    if (write)
                    {
                        string temp = path + ".qwtmp";
                        var output = encoding.GetPreamble().Concat(encoding.GetBytes(formatted)).ToArray();
                        File.WriteAllBytes(temp, output);
                        try { File.Replace(temp, path, null); }
                        finally { if (File.Exists(temp)) File.Delete(temp); }
                    }
                    results.Add((path, FileStatus.Changed, null));
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is FormatException
                    || error is InvalidOperationException || error is ArgumentException || error is NotSupportedException)
                {
                    results.Add((path, FileStatus.Failed, error.Message));
                }
            }
            return results;
        }

        private sealed class IdentifierTokens : TSqlFragmentVisitor
        {
            internal readonly HashSet<int> Indices = new HashSet<int>();
            public override void Visit(Identifier node) { Indices.Add(node.FirstTokenIndex); }
            // Built-in datatype names can follow keyword casing; user-defined datatype names cannot.
            public override void ExplicitVisit(SqlDataTypeReference node) { }
        }
    }
}

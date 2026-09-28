using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlWorkbench.Core
{
    public sealed class FormattingStyle
    {
        public int IndentSize { get; set; } = 4;
        public bool LowercaseKeywords { get; set; }
        public bool LeadingCommas { get; set; }
        public bool MultilineColumns { get; set; } = true;
        public bool NewLineBeforeFrom { get; set; } = true;
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
                NewLineBeforeFromClause = style.NewLineBeforeFrom
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

        private sealed class IdentifierTokens : TSqlFragmentVisitor
        {
            internal readonly HashSet<int> Indices = new HashSet<int>();
            public override void Visit(Identifier node) { Indices.Add(node.FirstTokenIndex); }
            // Built-in datatype names can follow keyword casing; user-defined datatype names cannot.
            public override void ExplicitVisit(SqlDataTypeReference node) { }
        }
    }
}

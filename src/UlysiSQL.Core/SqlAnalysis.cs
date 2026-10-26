using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace UlysiSQL.Core
{
    /// <summary>
    /// ScriptDom parses and visits recursively, and a stack overflow cannot be caught: it ends the SSMS process.
    /// Every parse goes through <see cref="ParseSafe"/>, which reports too-deeply nested SQL as a parse error instead.
    /// </summary>
    internal static class SafeParsing
    {
        /// <summary>Nesting limit, well under the ~500 levels that overflow a 1 MB thread stack.</summary>
        internal const int MaxDepth = 200;
        /// <summary>Binary operators (AND, OR, +, UNION...) per statement; each nests the tree one level. ~4,000 overflow the formatter.</summary>
        internal const int MaxChain = 2000;
        private static readonly HashSet<TSqlTokenType> Operators = new HashSet<TSqlTokenType> { TSqlTokenType.And, TSqlTokenType.Or, TSqlTokenType.Union,
            TSqlTokenType.Except, TSqlTokenType.Intersect, TSqlTokenType.Plus, TSqlTokenType.Minus, TSqlTokenType.Star, TSqlTokenType.Divide,
            TSqlTokenType.PercentSign, TSqlTokenType.Ampersand, TSqlTokenType.VerticalLine, TSqlTokenType.Circumflex };
        private static readonly HashSet<TSqlTokenType> StatementStarts = new HashSet<TSqlTokenType> { TSqlTokenType.Semicolon, TSqlTokenType.Insert,
            TSqlTokenType.Update, TSqlTokenType.Delete, TSqlTokenType.Merge, TSqlTokenType.Declare, TSqlTokenType.Set, TSqlTokenType.If, TSqlTokenType.While,
            TSqlTokenType.Exec, TSqlTokenType.Execute, TSqlTokenType.Create, TSqlTokenType.Alter, TSqlTokenType.Drop, TSqlTokenType.Print, TSqlTokenType.Return };
        private static readonly HashSet<string> NotBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "TRAN", "TRANSACTION", "DISTRIBUTED", "DIALOG", "CONVERSATION" };
        /// <summary>Statements that end a run of bare IF/WHILE bodies; a condition holds none of these outside parentheses.</summary>
        private static readonly HashSet<TSqlTokenType> BodyStarts = new HashSet<TSqlTokenType>(StatementStarts.Where(t => t != TSqlTokenType.If && t != TSqlTokenType.While))
        {
            TSqlTokenType.Select, TSqlTokenType.Begin, TSqlTokenType.With, TSqlTokenType.Raiserror, TSqlTokenType.GoTo, TSqlTokenType.Break, TSqlTokenType.Continue,
            TSqlTokenType.WaitFor, TSqlTokenType.Truncate, TSqlTokenType.Commit, TSqlTokenType.Rollback, TSqlTokenType.Save, TSqlTokenType.Fetch, TSqlTokenType.Open,
            TSqlTokenType.Close, TSqlTokenType.Deallocate, TSqlTokenType.Use, TSqlTokenType.Grant, TSqlTokenType.Revoke, TSqlTokenType.Deny,
        };

        internal static TSqlFragment ParseSafe(this TSqlParser parser, string sql, out IList<ParseError> errors)
        {
            if (TooDeep(sql))
            {
                errors = new[] { new ParseError(0, 0, 1, 1, "SQL is nested too deeply to parse safely (over " + MaxDepth + " levels, or " + MaxChain + " operators in one statement).") };
                return new TSqlScript();
            }
            return parser.Parse(new StringReader(sql), out errors);
        }

        /// <summary>
        /// Counts open parentheses, CASE and BEGIN blocks per batch, operator chains per statement, runs of prefix operators (NOT, ~, unary + and -),
        /// plus ELSE IF chains and bare IF/WHILE bodies that hold another IF or WHILE (each nests the next statement, but costs less stack than a
        /// parenthesis, hence the quarter weight). Lexing is iterative, so this is safe on any input.
        /// </summary>
        internal static bool TooDeep(string sql)
        {
            var tokens = new TSql170Parser(true).GetTokenStream(new StringReader(sql), out _);
            int depth = 0, elseIf = 0, chain = 0, prefix = 0, bodies = 0, bodiesDepth = 0;
            TSqlParserToken? previous = null;
            foreach (var token in tokens)
            {
                switch (token.TokenType)
                {
                    case TSqlTokenType.WhiteSpace: case TSqlTokenType.SingleLineComment: case TSqlTokenType.MultilineComment: continue;
                }
                if (token.TokenType == TSqlTokenType.If || token.TokenType == TSqlTokenType.While)
                {
                    if (bodies > 0 && depth == bodiesDepth) bodies++;
                    else { bodies = 1; bodiesDepth = depth; }
                }
                else if (depth == bodiesDepth && BodyStarts.Contains(token.TokenType)) bodies = 0;
                prefix = token.TokenType == TSqlTokenType.Not || token.TokenType == TSqlTokenType.Tilde
                    || token.TokenType == TSqlTokenType.Plus || token.TokenType == TSqlTokenType.Minus ? prefix + 1 : 0;
                switch (token.TokenType)
                {
                    case TSqlTokenType.Go: depth = 0; elseIf = 0; chain = 0; bodies = 0; break;
                    case TSqlTokenType.LeftParenthesis: case TSqlTokenType.Case: case TSqlTokenType.Begin: depth++; break;
                    case TSqlTokenType.RightParenthesis: case TSqlTokenType.End: if (depth > 0) depth--; break;
                    case TSqlTokenType.If: if (previous?.TokenType == TSqlTokenType.Else) elseIf++; break;
                    default:
                        // BEGIN TRAN / DISTRIBUTED / DIALOG / CONVERSATION open no block.
                        if (previous?.TokenType == TSqlTokenType.Begin && depth > 0 && NotBlocks.Contains(token.Text)) depth--;
                        break;
                }
                if (Operators.Contains(token.TokenType)) chain++;
                else if (StatementStarts.Contains(token.TokenType)) chain = 0;
                if (depth + prefix + (elseIf + bodies) / 4 > MaxDepth || chain > MaxChain) return true;
                previous = token;
            }
            return false;
        }
    }
    public sealed class SqlDiagnostic
    {
        public string Rule { get; }
        public string Message { get; }
        public int Offset { get; }
        public int Length { get; }
        public int Line { get; }
        public int Column { get; }

        internal SqlDiagnostic(string rule, string message, int offset, int length, int line, int column)
        {
            Rule = rule; Message = message; Offset = offset; Length = length; Line = line; Column = column;
        }
    }

    public sealed class AnalysisResult
    {
        public bool Parsed { get; }
        public IReadOnlyList<SqlDiagnostic> Diagnostics { get; }
        internal AnalysisResult(bool parsed, IEnumerable<SqlDiagnostic> diagnostics)
        {
            Parsed = parsed;
            Diagnostics = diagnostics.OrderBy(d => d.Offset).ThenBy(d => d.Rule).ToArray();
        }
    }

    public static class SqlAnalysis
    {
        public static AnalysisResult Analyze(string sql, CancellationToken cancellationToken = default, WorkbenchSettings? settings = null)
        {
            if (sql == null) throw new ArgumentNullException(nameof(sql));
            if (sql.Length > 1_000_000) throw new ArgumentException("Analysis input exceeds 1,000,000 characters.", nameof(sql));
            cancellationToken.ThrowIfCancellationRequested();
            // ponytail: SQL Server 2025 grammar with QUOTED_IDENTIFIER ON; add per-connection dialect settings before live analysis.
            var parser = new TSql170Parser(true);
            var fragment = parser.ParseSafe(sql, out var errors);
            cancellationToken.ThrowIfCancellationRequested();
            if (errors.Count > 0)
                return new AnalysisResult(false, errors.Select(e => new SqlDiagnostic("PARSE" + e.Number,
                    e.Message, e.Offset, e.Offset < sql.Length ? 1 : 0, e.Line, e.Column)));
            var visitor = new Rules(cancellationToken);
            fragment.Accept(visitor);
            visitor.Finish();
            foreach (var token in fragment.ScriptTokenStream ?? Array.Empty<TSqlParserToken>())
                if ((token.TokenType == TSqlTokenType.SingleLineComment || token.TokenType == TSqlTokenType.MultilineComment) && Marker.IsMatch(token.Text))
                    visitor.Diagnostics.Add(new SqlDiagnostic("SW065", "Comment marks unfinished work (TODO, FIXME, BUG or HACK).", token.Offset, token.Text.Length, token.Line, token.Column));
            var suppression = new Suppression(fragment.ScriptTokenStream);
            return new AnalysisResult(true, visitor.Diagnostics.Where(d =>
                (settings == null || settings.Severity(d.Rule) != RuleSeverity.Disabled) && !suppression.Suppressed(d)));
        }

        /// <summary>Targets of DELETE/UPDATE statements without WHERE, e.g. "DELETE LC.StatusHdr"; with <paramref name="schema"/> also every other DROP and ALTER statement (first line).
        /// Empty when none or the SQL does not parse.</summary>
        public static IReadOnlyList<string> UnfilteredChanges(string sql, bool unfiltered = true, bool dropTruncate = false, bool schema = false)
        {
            var fragment = new TSql170Parser(true).ParseSafe(sql ?? "", out var errors);
            if (errors.Count > 0) return Array.Empty<string>();
            var found = new Unfiltered(sql!, unfiltered, dropTruncate, schema);
            fragment.Accept(found);
            return found.Targets;
        }

        /// <summary>"USE [Db]" statements in a script that also changes data or drops tables, so the changes land in another database than the window's. Empty when none or the SQL does not parse.</summary>
        public static IReadOnlyList<string> DatabaseSwitchChanges(string sql)
        {
            var fragment = new TSql170Parser(true).ParseSafe(sql ?? "", out var errors);
            if (errors.Count > 0) return Array.Empty<string>();
            var found = new Switches();
            fragment.Accept(found);
            return found.Changes ? found.Uses : (IReadOnlyList<string>)Array.Empty<string>();
        }

        private sealed class Switches : TSqlFragmentVisitor
        {
            internal readonly List<string> Uses = new List<string>();
            internal bool Changes;
            public override void Visit(UseStatement node) => Uses.Add("USE " + node.DatabaseName.Value);
            public override void Visit(InsertStatement node) => Changes = true;
            public override void Visit(UpdateStatement node) => Changes = true;
            public override void Visit(DeleteStatement node) => Changes = true;
            public override void Visit(MergeStatement node) => Changes = true;
            public override void Visit(TruncateTableStatement node) => Changes = true;
            public override void Visit(DropTableStatement node) => Changes = true;
        }

        private sealed class Unfiltered : TSqlFragmentVisitor
        {
            private readonly string sql;
            internal readonly List<string> Targets = new List<string>();
            private readonly List<TSqlFragment> modules = new List<TSqlFragment>();
            private readonly bool unfiltered, dropTruncate, schema;
            internal Unfiltered(string sql, bool unfiltered, bool dropTruncate, bool schema) { this.sql = sql; this.unfiltered = unfiltered; this.dropTruncate = dropTruncate; this.schema = schema; }
            // Statements inside a procedure, function or trigger body are stored, not run; ScriptDom visits the module before its body.
            public override void Visit(ProcedureStatementBodyBase node) => modules.Add(node);
            public override void Visit(TriggerStatementBody node) => modules.Add(node);
            private bool Stored(TSqlFragment node) => modules.Any(m => m != node && node.StartOffset >= m.StartOffset && node.StartOffset < m.StartOffset + m.FragmentLength);
            // #temp tables belong to this session, so dropping or emptying them is never a surprise.
            private static bool Temp(TSqlFragment? target) => ((target as NamedTableReference)?.SchemaObject ?? target as SchemaObjectName)?.BaseIdentifier?.Value.StartsWith("#", StringComparison.Ordinal) == true;
            private void Add(string verb, TSqlFragment target) { if (!Stored(target) && !Temp(target)) Targets.Add(verb + " " + sql.Substring(target.StartOffset, target.FragmentLength)); }
            public override void Visit(DeleteSpecification node) { if (unfiltered && node.WhereClause == null && node.Target != null && !(node.Target is VariableTableReference)) Add("DELETE", node.Target); }
            public override void Visit(UpdateSpecification node) { if (unfiltered && node.WhereClause == null && node.Target != null && !(node.Target is VariableTableReference)) Add("UPDATE", node.Target); }
            public override void Visit(TruncateTableStatement node) { if (dropTruncate && node.TableName != null) Add("TRUNCATE TABLE", node.TableName); }
            public override void Visit(DropTableStatement node) { if (dropTruncate) foreach (var name in node.Objects) Add("DROP TABLE", name); }
            // ponytail: ScriptDom names every DROP/ALTER statement type Drop*/Alter*, so the type name covers them all.
            public override void Visit(TSqlStatement node)
            {
                string type = node.GetType().Name;
                if (!schema || node is DropTableStatement || Stored(node) || Temp((node as AlterTableStatement)?.SchemaObjectName) || !(type.StartsWith("Drop", StringComparison.Ordinal) || type.StartsWith("Alter", StringComparison.Ordinal))) return;
                string text = sql.Substring(node.StartOffset, node.FragmentLength).Split('\n')[0].Trim().TrimEnd(';');
                Targets.Add(text.Length > 80 ? text.Substring(0, 77) + "..." : text);
            }
        }

        private static readonly Regex Marker = new Regex(@"\b(?:TODO|FIXME|BUG|HACK)\b", RegexOptions.CultureInvariant);

        /// <summary>Rules with a mechanical fix.</summary>
        public static readonly IReadOnlyCollection<string> FixableRules = new[] { "SW001", "SW003", "SW004", "SW009", "SW010", "SW015", "SW016", "SW017", "SW040",
            "SW048", "SW049", "SW050", "SW051" };

        /// <summary>The edit that resolves one diagnostic, or null when the rule has no safe fix here. The result must still parse.</summary>
        public static TextEdit? Fix(string sql, SqlDiagnostic diagnostic, IReadOnlyList<SchemaTable>? tables = null, string defaultSchema = "dbo")
        {
            if (sql == null) throw new ArgumentNullException(nameof(sql));
            if (diagnostic == null) throw new ArgumentNullException(nameof(diagnostic));
            if (!InRange(sql, diagnostic)) return null;
            var script = new TSql170Parser(true).ParseSafe(sql, out var errors);
            return errors.Count > 0 ? null : FixParsed(sql, script, diagnostic, tables, defaultSchema, out _);
        }

        private static bool InRange(string sql, SqlDiagnostic diagnostic) =>
            sql.Length <= 1_000_000 && diagnostic.Offset >= 0 && diagnostic.Offset + diagnostic.Length <= sql.Length;

        /// <summary><see cref="Fix"/> on text already parsed without errors. <paramref name="fixedScript"/> is the parse of the fixed
        /// text, which FixAll hands to the next fix instead of parsing the same text again.</summary>
        private static TextEdit? FixParsed(string sql, TSqlFragment script, SqlDiagnostic diagnostic, IReadOnlyList<SchemaTable>? tables, string defaultSchema,
            out TSqlFragment? fixedScript)
        {
            fixedScript = null;
            if (!InRange(sql, diagnostic)) return null;
            var nodes = new Spans(diagnostic.Offset, diagnostic.Length);
            script.Accept(nodes);
            string span = sql.Substring(diagnostic.Offset, diagnostic.Length);
            TextEdit? edit = null;
            switch (diagnostic.Rule)
            {
                case "SW001":
                    if (tables == null) return null;
                    try { edit = SqlCompletion.ExpandWildcard(sql, diagnostic.Offset + diagnostic.Length, tables, defaultSchema); }
                    catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is FormatException) { return null; }
                    break;
                case "SW003":
                    if (!(nodes.Comparison is BooleanComparisonExpression comparison)) return null;
                    string? test = comparison.ComparisonType == BooleanComparisonType.Equals ? " IS NULL"
                        : comparison.ComparisonType == BooleanComparisonType.NotEqualToBrackets || comparison.ComparisonType == BooleanComparisonType.NotEqualToExclamation ? " IS NOT NULL" : null;
                    var operand = Unwrap(comparison.FirstExpression) is NullLiteral ? comparison.SecondExpression : comparison.FirstExpression;
                    if (test == null || Unwrap(operand) is NullLiteral) return null;
                    edit = new TextEdit(diagnostic.Offset, diagnostic.Length, sql.Substring(operand.StartOffset, operand.FragmentLength) + test);
                    break;
                case "SW004":
                    if (nodes.Case == null) return null;
                    var endToken = script.ScriptTokenStream[nodes.Case.LastTokenIndex];
                    if (endToken.TokenType != TSqlTokenType.End) return null;
                    edit = new TextEdit(endToken.Offset, 0, "ELSE NULL ");
                    break;
                case "SW009":
                    edit = new TextEdit(diagnostic.Offset, diagnostic.Length, "SCOPE_IDENTITY()");
                    break;
                case "SW040":
                    int quote = span.IndexOf('\'');
                    if (quote < 0 || quote > 1 || span.Length < quote + 2 || span[span.Length - 1] != '\'') return null;
                    string alias = span.Substring(quote + 1, span.Length - quote - 2).Replace("''", "'");
                    if (alias.Length == 0 || alias.Length > 128) return null;
                    edit = new TextEdit(diagnostic.Offset, diagnostic.Length, "[" + alias.Replace("]", "]]") + "]");
                    break;
                case "SW048":
                    if (nodes.Comparison == null) return null;
                    var unequal = nodes.Comparison;
                    var between = Enumerable.Range(unequal.FirstExpression.LastTokenIndex + 1, Math.Max(0, unequal.SecondExpression.FirstTokenIndex - unequal.FirstExpression.LastTokenIndex - 1))
                        .Select(i => script.ScriptTokenStream[i]).ToArray();
                    var bang = between.FirstOrDefault(t => t.TokenType == TSqlTokenType.Bang);
                    var equals = between.FirstOrDefault(t => t.TokenType == TSqlTokenType.EqualsSign);
                    if (bang == null || equals == null || equals.Offset < bang.Offset) return null;
                    edit = new TextEdit(bang.Offset, equals.Offset + 1 - bang.Offset, "<>");
                    break;
                case "SW049":
                    if (nodes.Select?.ColumnName == null) return null;
                    edit = new TextEdit(diagnostic.Offset, diagnostic.Length, sql.Substring(nodes.Select.Expression.StartOffset, nodes.Select.Expression.FragmentLength) +
                        " AS " + sql.Substring(nodes.Select.ColumnName.StartOffset, nodes.Select.ColumnName.FragmentLength));
                    break;
                case "SW050":
                    if (nodes.Top?.Expression == null) return null;
                    var count = nodes.Top.Expression;
                    edit = new TextEdit(count.StartOffset, count.FragmentLength, "(" + sql.Substring(count.StartOffset, count.FragmentLength) + ")");
                    break;
                case "SW051":
                    var open = nodes.Hint == null ? null : HintParenthesis(nodes.Hint);
                    if (open == null) return null;
                    edit = new TextEdit(open.Offset, 0, "WITH ");
                    break;
                case "SW010":
                    string bare = span.Trim('[', ']', '"').ToLowerInvariant();
                    string? replacement = bare == "text" ? "varchar(max)" : bare == "ntext" ? "nvarchar(max)" : bare == "image" ? "varbinary(max)" : null;
                    if (replacement == null) return null;
                    edit = new TextEdit(diagnostic.Offset, diagnostic.Length, char.IsUpper(span.TrimStart('[', '"')[0]) ? replacement.ToUpperInvariant() : replacement);
                    break;
                case "SW015":
                    var statements = nodes.Procedure?.StatementList?.Statements;
                    if (statements != null && statements.Count > 0 && statements[0] is BeginEndBlockStatement block && block.StatementList.Statements.Count > 0)
                        statements = block.StatementList.Statements;
                    if (statements == null || statements.Count == 0) return null;
                    int first = statements[0].StartOffset, lineStart = sql.LastIndexOf('\n', Math.Max(0, first - 1)) + 1;
                    string lead = sql.Substring(lineStart, first - lineStart);
                    string indent = lead.Trim().Length == 0 ? lead : "";
                    string newline = sql.Contains("\r\n") ? "\r\n" : "\n";
                    edit = new TextEdit(first, 0, "SET NOCOUNT ON;" + (indent.Length > 0 || lead.Length == 0 ? newline + indent : " "));
                    break;
                case "SW016":
                    edit = RemoveDeclaration(sql, nodes);
                    break;
                case "SW017":
                    string schema = string.IsNullOrWhiteSpace(defaultSchema) ? "dbo" : defaultSchema;
                    edit = new TextEdit(diagnostic.Offset, 0, SqlCompletion.QuoteIfNeeded(schema) + ".");
                    break;
            }
            if (edit == null) return null;
            var parsed = new TSql170Parser(true).ParseSafe(Apply(sql, edit), out var after);
            if (after.Count > 0) return null;
            fixedScript = parsed;
            return edit;
        }

        /// <summary>Applies every available fix, re-analyzing between rounds; returns the new text and how many fixes were applied.</summary>
        public static (string Text, int Fixed) FixAll(string sql, IReadOnlyList<SchemaTable>? tables = null, string defaultSchema = "dbo", WorkbenchSettings? settings = null)
        {
            if (sql == null) throw new ArgumentNullException(nameof(sql));
            int count = 0;
            for (int round = 0; round < 10; round++)
            {
                var result = Analyze(sql, default, settings);
                if (!result.Parsed) break;
                int applied = 0, limit = int.MaxValue;
                // One parse per applied fix: each fix validates its result by parsing it, and that parse is what the next fix reads.
                TSqlFragment? script = null;
                // Back to front so earlier offsets stay valid; overlapping fixes wait for the next round.
                foreach (var diagnostic in result.Diagnostics.Where(d => FixableRules.Contains(d.Rule)).OrderByDescending(d => d.Offset))
                {
                    if (diagnostic.Offset + diagnostic.Length > limit) continue;
                    if (script == null)
                    {
                        script = new TSql170Parser(true).ParseSafe(sql, out var errors);
                        if (errors.Count > 0) break;
                    }
                    var edit = FixParsed(sql, script, diagnostic, tables, defaultSchema, out var fixedScript);
                    if (edit == null || fixedScript == null || edit.Start + edit.Length > limit) continue;
                    sql = Apply(sql, edit); script = fixedScript; limit = edit.Start; applied++;
                }
                if (applied == 0) break;
                count += applied;
            }
            return (sql, count);
        }

        // The ( that opens a table hint list, when no WITH precedes it; null when WITH is there or the hint is not parenthesized.
        private static TSqlParserToken? HintParenthesis(TableHint hint)
        {
            var tokens = hint.ScriptTokenStream;
            bool Trivia(int i) => tokens[i].TokenType == TSqlTokenType.WhiteSpace || tokens[i].TokenType == TSqlTokenType.SingleLineComment || tokens[i].TokenType == TSqlTokenType.MultilineComment;
            int index = hint.FirstTokenIndex - 1;
            while (index >= 0 && Trivia(index)) index--;
            if (index < 0 || tokens[index].TokenType != TSqlTokenType.LeftParenthesis) return null;
            int before = index - 1;
            while (before >= 0 && Trivia(before)) before--;
            return before >= 0 && tokens[before].TokenType == TSqlTokenType.With ? null : tokens[index];
        }

        private static string Apply(string sql, TextEdit edit) => sql.Substring(0, edit.Start) + edit.Text + sql.Substring(edit.Start + edit.Length);

        private static ScalarExpression Unwrap(ScalarExpression expression)
        {
            while (expression is ParenthesisExpression parenthesis) expression = parenthesis.Expression;
            return expression;
        }

        // Drops the whole DECLARE (and its line when nothing else is on it) or one element and its comma.
        private static TextEdit? RemoveDeclaration(string sql, Spans nodes)
        {
            if (nodes.Declaration == null) return null;
            if (nodes.Declaration is DeclareVariableStatement multi && multi.Declarations.Count > 1)
            {
                int index = multi.Declarations.IndexOf((DeclareVariableElement)nodes.Element!);
                if (index < 0) return null;
                var element = multi.Declarations[index];
                int from = index == 0 ? element.StartOffset : multi.Declarations[index - 1].StartOffset + multi.Declarations[index - 1].FragmentLength;
                int to = index == 0 ? multi.Declarations[1].StartOffset : element.StartOffset + element.FragmentLength;
                return new TextEdit(from, to - from, "");
            }
            var (start, length) = SqlNavigation.Span(nodes.Declaration);
            int end = start + length, lineStart = sql.LastIndexOf('\n', Math.Max(0, start - 1)) + 1, lineEnd = sql.IndexOf('\n', end);
            if (start > 0 && sql[start - 1] == '\n') lineStart = start;
            if (lineEnd < 0) lineEnd = sql.Length; else lineEnd++;
            if (sql.Substring(lineStart, start - lineStart).Trim().Length == 0 && sql.Substring(end, lineEnd - end).Trim().Length == 0)
                return new TextEdit(lineStart, lineEnd - lineStart, "");
            return new TextEdit(start, length, "");
        }

        private sealed class Spans : TSqlFragmentVisitor
        {
            private readonly int offset, length;
            internal BooleanComparisonExpression? Comparison;
            internal CaseExpression? Case;
            internal SelectScalarExpression? Select;
            internal TopRowFilter? Top;
            internal TableHint? Hint;
            internal ProcedureStatementBody? Procedure;
            internal TSqlStatement? Declaration;
            internal TSqlFragment? Element;
            private TSqlStatement? current;
            internal Spans(int offset, int length) { this.offset = offset; this.length = length; }
            private bool At(TSqlFragment node) => node.StartOffset == offset && node.FragmentLength == length;
            public override void Visit(BooleanComparisonExpression node) { if (At(node)) Comparison = node; }
            public override void Visit(CaseExpression node) { if (At(node)) Case = node; }
            public override void Visit(SelectScalarExpression node) { if (At(node)) Select = node; }
            public override void Visit(TopRowFilter node) { if (At(node)) Top = node; }
            public override void Visit(TableHint node) { if (At(node)) Hint = node; }
            public override void Visit(ProcedureStatementBody node)
            {
                if (node.ProcedureReference != null && At(node.ProcedureReference) || At(node)) Procedure = node;
            }
            public override void Visit(DeclareVariableStatement node) => current = node;
            public override void Visit(DeclareTableVariableStatement node) => current = node;
            public override void Visit(DeclareVariableElement node)
            {
                if (!(node is ProcedureParameter) && At(node.VariableName) && current != null && SqlNavigation.Contains(current, offset)) { Declaration = current; Element = node; }
            }
            public override void Visit(DeclareTableVariableBody node)
            {
                if (At(node.VariableName) && current != null && SqlNavigation.Contains(current, offset)) { Declaration = current; Element = node; }
            }
        }

        // Inline directives in comments: ulysisql-disable [IDs], ulysisql-enable [IDs], ulysisql-disable-next-line [IDs].
        // The ulysisql- prefix from before the rename works the same. No IDs means every rule.
        // Only rule diagnostics pass through here; parse errors return earlier.
        private sealed class Suppression
        {
            private static readonly Regex Directive = new Regex(@"^(?:--|/\*)\s*(?:ulysisql|querywright)-(disable-next-line|disable|enable)(?![\w-])(.*?)(?:\*/)?$",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
            private readonly List<(int Offset, bool Enable, string[] Rules)> toggles = new List<(int, bool, string[])>();
            private readonly List<(int Line, string[] Rules)> nextLines = new List<(int, string[])>();

            internal Suppression(IList<TSqlParserToken>? tokens)
            {
                foreach (var token in tokens ?? Array.Empty<TSqlParserToken>())
                {
                    if (token.TokenType != TSqlTokenType.SingleLineComment && token.TokenType != TSqlTokenType.MultilineComment) continue;
                    var match = Directive.Match(token.Text.TrimEnd());
                    if (!match.Success) continue;
                    var rules = match.Groups[2].Value.Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .TakeWhile(id => Regex.IsMatch(id, @"^SW\d+$", RegexOptions.IgnoreCase)).Select(id => id.ToUpperInvariant()).ToArray();
                    var kind = match.Groups[1].Value.ToLowerInvariant();
                    if (kind == "disable-next-line") nextLines.Add((token.Line + token.Text.Count(c => c == '\n') + 1, rules));
                    else toggles.Add((token.Offset, kind == "enable", rules));
                }
            }

            internal bool Suppressed(SqlDiagnostic diagnostic)
            {
                if (nextLines.Any(n => n.Line == diagnostic.Line && (n.Rules.Length == 0 || n.Rules.Contains(diagnostic.Rule)))) return true;
                bool all = false, off = false; // off: this rule is individually disabled (or re-enabled while all is set).
                foreach (var toggle in toggles.TakeWhile(t => t.Offset < diagnostic.Offset))
                {
                    if (toggle.Rules.Length == 0) { all = !toggle.Enable; off = false; }
                    else if (toggle.Rules.Contains(diagnostic.Rule)) off = all ? toggle.Enable : !toggle.Enable;
                }
                return all != off;
            }
        }

        private sealed class Rules : TSqlFragmentVisitor
        {
            internal readonly List<SqlDiagnostic> Diagnostics = new List<SqlDiagnostic>();
            private readonly CancellationToken cancellation;
            internal Rules(CancellationToken cancellation) { this.cancellation = cancellation; }
            private void Add(string rule, string message, TSqlFragment node)
            {
                cancellation.ThrowIfCancellationRequested();
                Diagnostics.Add(new SqlDiagnostic(rule, message, node.StartOffset, node.FragmentLength, node.StartLine, node.StartColumn));
            }
            public override void Visit(SelectStarExpression node)
            {
                Add("SW001", "Specify output columns instead of a wildcard.", node);
            }
            public override void Visit(InsertSpecification node)
            {
                if (node.Columns.Count == 0 && !(node.InsertSource is ValuesInsertSource values && values.IsDefaultValues))
                    Add("SW002", "Specify target columns so insertion does not depend on table column order.", node);
                if (node.InsertSource is SelectInsertSource source && source.Select is QuerySpecification query && query.OrderByClause != null && query.TopRowFilter == null && query.OffsetClause == null)
                    Add("SW062", "ORDER BY on INSERT ... SELECT does not order the stored rows; it only costs a sort.", query.OrderByClause);
            }
            public override void Visit(BooleanComparisonExpression node)
            {
                if (IsNull(node.FirstExpression) || IsNull(node.SecondExpression))
                    Add("SW003", "Use IS NULL or IS NOT NULL instead of comparing with NULL.", node);
                if (IsCountSubquery(node.FirstExpression) && IsZero(node.SecondExpression) || IsZero(node.FirstExpression) && IsCountSubquery(node.SecondExpression))
                    Add("SW028", "Comparing a COUNT subquery with 0 counts every row; use EXISTS or NOT EXISTS.", node);
                if (node.ComparisonType == BooleanComparisonType.NotLessThan || node.ComparisonType == BooleanComparisonType.NotGreaterThan)
                    Add("SW042", "!< and !> are nonstandard; use >= or <=.", node);
                if (node.ComparisonType == BooleanComparisonType.NotEqualToExclamation)
                    Add("SW048", "!= is not ISO SQL; use <>.", node);
            }
            private static bool IsZero(ScalarExpression expression) => Unwrap(expression) is IntegerLiteral literal && literal.Value == "0";
            private static bool IsAggregate(QueryExpression? query, params string[] names) =>
                query is QuerySpecification spec && spec.GroupByClause == null && spec.HavingClause == null && spec.SelectElements.Count == 1
                && spec.SelectElements[0] is SelectScalarExpression element && Unwrap(element.Expression) is FunctionCall call
                && names.Contains(call.FunctionName.Value, StringComparer.OrdinalIgnoreCase);
            private static bool IsCountSubquery(ScalarExpression expression) =>
                Unwrap(expression) is ScalarSubquery subquery && IsAggregate(subquery.QueryExpression, "COUNT", "COUNT_BIG");
            private static bool IsNull(ScalarExpression expression) => Unwrap(expression) is NullLiteral;
            public override void Visit(SearchedCaseExpression node)
            {
                if (node.ElseExpression == null)
                    Add("SW004", "CASE has no ELSE; unmatched rows produce NULL.", node);
            }
            public override void Visit(SimpleCaseExpression node)
            {
                if (node.ElseExpression == null)
                    Add("SW004", "CASE has no ELSE; unmatched values produce NULL.", node);
            }
            public override void Visit(DeleteSpecification node)
            {
                if (node.WhereClause == null) Add("SW005", "DELETE has no WHERE clause and removes every row.", node);
            }
            public override void Visit(UpdateSpecification node)
            {
                if (node.WhereClause == null) Add("SW006", "UPDATE has no WHERE clause and changes every row.", node);
            }
            public override void Visit(ExpressionWithSortOrder node)
            {
                if (node.Expression is Literal) Add("SW007", "ORDER BY a constant or column ordinal; name the column instead.", node);
            }
            public override void Visit(SqlDataTypeReference node)
            {
                switch (node.SqlDataTypeOption)
                {
                    case SqlDataTypeOption.Char: case SqlDataTypeOption.VarChar: case SqlDataTypeOption.NChar:
                    case SqlDataTypeOption.NVarChar: case SqlDataTypeOption.Binary: case SqlDataTypeOption.VarBinary:
                        if (node.Parameters.Count == 0) Add("SW008", "Specify a length; the default (1 or 30) silently truncates.", node);
                        else if ((node.SqlDataTypeOption == SqlDataTypeOption.VarChar || node.SqlDataTypeOption == SqlDataTypeOption.NVarChar || node.SqlDataTypeOption == SqlDataTypeOption.VarBinary) &&
                            node.Parameters[0] is IntegerLiteral size && (size.Value == "1" || size.Value == "2"))
                            Add("SW071", "A variable-length type of 1 or 2 costs more than the fixed-length type; use CHAR, NCHAR or BINARY.", node);
                        break;
                    case SqlDataTypeOption.Sql_Variant:
                        Add("SW055", "SQL_VARIANT hides the real type, blocks many index and client features, and compares by type family.", node);
                        break;
                    case SqlDataTypeOption.Text: case SqlDataTypeOption.NText: case SqlDataTypeOption.Image:
                        Add("SW010", "TEXT, NTEXT and IMAGE are deprecated; use VARCHAR(MAX), NVARCHAR(MAX) or VARBINARY(MAX).", node);
                        break;
                    case SqlDataTypeOption.Float: case SqlDataTypeOption.Real:
                        Add("SW037", "FLOAT and REAL are approximate; use DECIMAL for exact values.", node);
                        break;
                    case SqlDataTypeOption.Money: case SqlDataTypeOption.SmallMoney:
                        Add("SW038", "MONEY and SMALLMONEY round in division and multiplication; use DECIMAL.", node);
                        break;
                    case SqlDataTypeOption.Timestamp:
                        Add("SW039", "The TIMESTAMP synonym is deprecated; use ROWVERSION.", node);
                        break;
                    case SqlDataTypeOption.Decimal: case SqlDataTypeOption.Numeric:
                        if (node.Parameters.Count == 0) Add("SW045", "Specify precision and scale; the default DECIMAL(18, 0) drops fractions.", node);
                        break;
                }
            }
            public override void Visit(GlobalVariableExpression node)
            {
                if (string.Equals(node.Name, "@@IDENTITY", StringComparison.OrdinalIgnoreCase))
                    Add("SW009", "@@IDENTITY can return a trigger's identity; use SCOPE_IDENTITY() or OUTPUT.", node);
                if (string.Equals(node.Name, "@@ERROR", StringComparison.OrdinalIgnoreCase))
                    Add("SW031", "@@ERROR resets after every statement; use TRY...CATCH.", node);
            }
            public override void Visit(FromClause node)
            {
                if (node.TableReferences.Count > 1) Add("SW011", "Comma-separated tables are an old-style join; use explicit JOIN.", node);
            }
            public override void Visit(ProcedureStatementBody node)
            {
                var name = node.ProcedureReference?.Name?.BaseIdentifier?.Value;
                if (name != null && name.StartsWith("sp_", StringComparison.OrdinalIgnoreCase))
                    Add("SW012", "Procedure names starting with sp_ are looked up in master first.", node.ProcedureReference!);
                if (node.ProcedureReference?.Number != null)
                    Add("SW041", "Numbered procedures (name;n) are deprecated; give each procedure its own name.", node.ProcedureReference);
                if (node.StatementList == null) return; // CLR procedure.
                var body = new ProcedureBody();
                node.StatementList.Accept(body);
                if (!body.NoCount) Add("SW015", "Procedure lacks SET NOCOUNT ON; row-count messages add network chatter.", node.ProcedureReference ?? (TSqlFragment)node);
                foreach (var bare in body.BareReturns) Add("SW019", "RETURN without a value; return an explicit status code from procedures.", bare);
                foreach (var delay in body.Delays) Add("SW025", "WAITFOR DELAY in a procedure holds its connection and locks while waiting.", delay);
                foreach (var option in node.Options.Where(o => o.OptionKind == ProcedureOptionKind.Recompile))
                    Add("SW061", "WITH RECOMPILE compiles the whole procedure on every call; use OPTION (RECOMPILE) on the statement that needs it.", option);
            }
            public override void Visit(CursorDefinition node)
            {
                if (!node.Options.Any(o => o.OptionKind == CursorOptionKind.Local || o.OptionKind == CursorOptionKind.Global))
                    Add("SW018", "Declare the cursor LOCAL (or GLOBAL); the default scope depends on a database option.", node);
            }
            public override void Visit(CreateTableStatement node)
            {
                CheckNullability(node.Definition);
                if (node.Definition == null || node.SchemaObjectName?.BaseIdentifier?.Value.StartsWith("#", StringComparison.Ordinal) != true) return;
                var constraints = node.Definition.TableConstraints.Cast<ConstraintDefinition>()
                    .Concat(node.Definition.ColumnDefinitions.SelectMany(c => c.Constraints.Cast<ConstraintDefinition>()
                        .Concat(c.DefaultConstraint == null ? Enumerable.Empty<ConstraintDefinition>() : new[] { c.DefaultConstraint })));
                foreach (var constraint in constraints.Where(c => c.ConstraintIdentifier != null))
                    Add("SW036", "Named constraints on temporary tables collide when two sessions run this; omit the name.", constraint);
            }
            public override void Visit(DeclareTableVariableBody node) => CheckNullability(node.Definition);
            private void CheckNullability(TableDefinition? table)
            {
                if (table == null) return;
                var keys = new HashSet<string>(table.TableConstraints.OfType<UniqueConstraintDefinition>().Where(u => u.IsPrimaryKey)
                    .SelectMany(u => u.Columns).Select(c => c.Column.MultiPartIdentifier.Identifiers.Last().Value), StringComparer.OrdinalIgnoreCase);
                foreach (var column in table.ColumnDefinitions)
                    if (column.ComputedColumnExpression == null && column.IdentityOptions == null && !keys.Contains(column.ColumnIdentifier.Value) &&
                        !column.Constraints.Any(c => c is NullableConstraintDefinition || c is UniqueConstraintDefinition u && u.IsPrimaryKey))
                        Add("SW020", "Specify NULL or NOT NULL; the default depends on session ANSI_NULL_DFLT settings.", column);
            }
            public override void Visit(ReadTextStatement node) => Add("SW021", "READTEXT is deprecated; use SUBSTRING on VARCHAR(MAX)/VARBINARY(MAX).", node);
            public override void Visit(WriteTextStatement node) => Add("SW021", "WRITETEXT is deprecated; use UPDATE on VARCHAR(MAX)/VARBINARY(MAX).", node);
            public override void Visit(UpdateTextStatement node) => Add("SW021", "UPDATETEXT is deprecated; use UPDATE ... .WRITE on VARCHAR(MAX)/VARBINARY(MAX).", node);
            public override void Visit(AlterTableAddTableElementStatement node)
            {
                foreach (var column in node.Definition?.ColumnDefinitions ?? Enumerable.Empty<ColumnDefinition>())
                    if (column.DefaultConstraint == null && column.ComputedColumnExpression == null && column.IdentityOptions == null &&
                        column.Constraints.OfType<NullableConstraintDefinition>().Any(n => !n.Nullable))
                        Add("SW022", "Adding a NOT NULL column without DEFAULT fails on a table that has rows.", column);
            }
            public override void Visit(SetRowCountStatement node) => Add("SW023", "SET ROWCOUNT is deprecated for INSERT/UPDATE/DELETE; use TOP.", node);
            public override void Visit(TableHint node)
            {
                if (node.HintKind == TableHintKind.NoLock || node.HintKind == TableHintKind.ReadUncommitted)
                    Add("SW024", "NOLOCK/READUNCOMMITTED reads uncommitted data and can skip or double-count rows.", node);
                if (node is IndexTableHint)
                    Add("SW034", "Index hints override the optimizer and break when the index changes.", node);
            }
            private readonly HashSet<QueryExpression> existsQueries = new HashSet<QueryExpression>();
            public override void Visit(ExistsPredicate node)
            {
                if (node.Subquery?.QueryExpression == null) return;
                existsQueries.Add(node.Subquery.QueryExpression);
                if (IsAggregate(node.Subquery.QueryExpression, "COUNT", "COUNT_BIG", "SUM", "MIN", "MAX", "AVG"))
                    Add("SW044", "EXISTS over an aggregate without GROUP BY is always true; aggregates return one row even for no input.", node);
            }
            public override void Visit(QuerySpecification node)
            {
                if (node.TopRowFilter != null && node.OrderByClause == null && !existsQueries.Contains(node))
                    Add("SW026", "TOP without ORDER BY returns an arbitrary set of rows.", node.TopRowFilter);
                if (node.TopRowFilter != null && node.TopRowFilter.Percent && Unwrap(node.TopRowFilter.Expression) is IntegerLiteral hundred && hundred.Value == "100")
                    Add("SW043", "TOP 100 PERCENT does nothing; the optimizer ignores ORDER BY it was meant to keep.", node.TopRowFilter);
                if (node.TopRowFilter != null && !(node.TopRowFilter.Expression is ParenthesisExpression))
                    Add("SW050", "TOP without parentheses is the old syntax and only takes a constant; write TOP (n).", node.TopRowFilter);
            }
            public override void Visit(ExecuteSpecification node)
            {
                if (node.ExecutableEntity is ExecutableStringList)
                    Add("SW027", "EXECUTE(string) runs unparameterized SQL; use sp_executesql with parameters.", node);
            }
            public override void Visit(TSqlStatement node) => cancellation.ThrowIfCancellationRequested();
            public override void Visit(GoToStatement node) => Add("SW029", "GOTO makes control flow hard to follow; use structured blocks or TRY...CATCH.", node);
            public override void Visit(PredicateSetStatement node)
            {
                if (!node.IsOn && (node.Options & (SetOptions.AnsiNulls | SetOptions.AnsiPadding | SetOptions.ConcatNullYieldsNull)) != 0)
                    Add("SW030", "ANSI_NULLS, ANSI_PADDING and CONCAT_NULL_YIELDS_NULL OFF are deprecated and will always be ON.", node);
                if (node.IsOn && (node.Options & SetOptions.FmtOnly) != 0)
                    Add("SW035", "SET FMTONLY is deprecated; use sp_describe_first_result_set.", node);
                if (!node.IsOn && (node.Options & SetOptions.NoCount) != 0)
                    Add("SW059", "SET NOCOUNT OFF sends a row-count message after every statement.", node);
                if (node.IsOn && (node.Options & SetOptions.ForcePlan) != 0)
                    Add("SW060", "SET FORCEPLAN fixes join order and type for the session, whatever the data looks like.", node);
            }
            public override void Visit(SelectScalarExpression node)
            {
                if (node.ColumnName?.ValueExpression is StringLiteral)
                    Add("SW040", "String literals as column aliases are deprecated; use AS [alias].", node.ColumnName);
                if (node.ColumnName != null && node.ColumnName.StartOffset < node.Expression.StartOffset)
                    Add("SW049", "alias = expression reads like an assignment; write expression AS alias.", node);
                if (Unwrap(node.Expression) is ColumnReferenceExpression column && column.MultiPartIdentifier?.Identifiers.Count > 2)
                    Add("SW067", "Column references qualified with a schema or database are deprecated; alias the table and use alias.column.", column);
            }
            public override void Visit(LikePredicate node)
            {
                if (Unwrap(node.FirstExpression) is ColumnReferenceExpression && Unwrap(node.SecondExpression) is StringLiteral pattern && pattern.Value.StartsWith("%", StringComparison.Ordinal))
                    Add("SW033", "LIKE with a leading % cannot seek an index.", node);
            }
            private readonly HashSet<BooleanComparisonExpression> sargChecked = new HashSet<BooleanComparisonExpression>();
            public override void Visit(WhereClause node) => CheckSargable(node.SearchCondition);
            public override void Visit(QualifiedJoin node)
            {
                CheckSargable(node.SearchCondition);
                if (node.JoinHint != JoinHint.None) Add("SW058", "Join hints force the physical join and the join order of the whole query.", node);
            }
            private void CheckSargable(BooleanExpression? condition)
            {
                if (condition == null) return;
                var comparisons = new Comparisons();
                condition.Accept(comparisons);
                foreach (var comparison in comparisons.Items.Where(sargChecked.Add))
                    if (WrapsColumn(comparison.FirstExpression) && !HasColumn(comparison.SecondExpression) || WrapsColumn(comparison.SecondExpression) && !HasColumn(comparison.FirstExpression))
                        Add("SW032", "A function around the column prevents an index seek; move the work to the other side.", comparison);
            }
            private static bool WrapsColumn(ScalarExpression expression) =>
                Unwrap(expression) is FunctionCall call && call.CallTarget == null && call.Parameters.Any(p => Unwrap(p) is ColumnReferenceExpression c && c.ColumnType == ColumnType.Regular);
            private static bool HasColumn(ScalarExpression expression)
            {
                var columns = new Columns();
                expression.Accept(columns);
                return columns.Found;
            }
            private sealed class Comparisons : TSqlFragmentVisitor
            {
                internal readonly List<BooleanComparisonExpression> Items = new List<BooleanComparisonExpression>();
                public override void Visit(BooleanComparisonExpression node) => Items.Add(node);
            }
            private sealed class Columns : TSqlFragmentVisitor
            {
                internal bool Found;
                public override void Visit(ColumnReferenceExpression node) => Found = true;
            }
            public override void Visit(FunctionCall node)
            {
                if (string.Equals(node.FunctionName.Value, "ISNUMERIC", StringComparison.OrdinalIgnoreCase))
                    Add("SW013", "ISNUMERIC accepts values like '$' and '1e5'; use TRY_CONVERT.", node);
            }
            public override void Visit(ExecutableStringList node)
            {
                if (node.Strings.Count > 1 && node.Strings.Any(part => !(part is StringLiteral)))
                    Add("SW047", "EXEC of a concatenated string; use sp_executesql with parameters and QUOTENAME for object names to avoid injection and quoting bugs.", node);
            }
            public override void Visit(InPredicate node)
            {
                if (node.NotDefined && node.Subquery != null)
                    Add("SW014", "NOT IN with a subquery returns no rows if the subquery yields NULL; use NOT EXISTS.", node);
                else if (node.Subquery != null)
                    Add("SW070", "IN with a subquery; EXISTS states the intent and stops at the first match.", node);
            }
            public override void Visit(ExecutableProcedureReference node)
            {
                var name = node.ProcedureReference?.ProcedureReference?.Name;
                if (name != null && string.Equals(name.BaseIdentifier.Value, "xp_cmdshell", StringComparison.OrdinalIgnoreCase))
                    Add("SW046", "xp_cmdshell runs operating-system commands with the service account; avoid it.", name);
                if (name != null && name.SchemaIdentifier == null && !name.BaseIdentifier.Value.StartsWith("sp_", StringComparison.OrdinalIgnoreCase) &&
                    !name.BaseIdentifier.Value.StartsWith("#", StringComparison.Ordinal))
                    Add("SW017", "Schema-qualify the procedure name to avoid extra name resolution.", name);
            }
            public override void Visit(TSqlBatch node)
            {
                var usage = new VariableUsage();
                node.Accept(usage);
                foreach (var declared in usage.Declared.Where(d => !usage.Used.Contains(d.Value)))
                    Add("SW016", "Variable " + declared.Value + " is declared but never used.", declared);
            }
            public override void Visit(TriggerStatementBody node)
            {
                if (node.StatementList == null) return; // CLR trigger.
                var body = new TriggerBody();
                node.StatementList.Accept(body);
                foreach (var print in body.Prints) Add("SW063", "PRINT in a trigger sends a message on every firing; remove it once debugging is done.", print);
                foreach (var select in body.Selects) Add("SW064", "A trigger that returns a result set breaks callers that expect none.", select);
            }
            public override void Visit(GroupByClause node)
            {
                if (node.All) Add("SW052", "GROUP BY ALL is deprecated and returns groups the WHERE clause filtered out.", node);
            }
            public override void Visit(SetUserStatement node) => Add("SW053", "SETUSER is deprecated; use EXECUTE AS.", node);
            public override void Visit(DbccStatement node)
            {
                if (node.Command == DbccCommand.DBReindex || node.Command == DbccCommand.IndexDefrag || node.Command == DbccCommand.ShowContig)
                    Add("SW054", "DBCC DBREINDEX, INDEXDEFRAG and SHOWCONTIG are deprecated; use ALTER INDEX and sys.dm_db_index_physical_stats.", node);
            }
            public override void Visit(SelectStatement node)
            {
                if (node.Into != null) Add("SW056", "SELECT INTO copies no keys, indexes or constraints and guesses nullability; create the table explicitly.", node.Into);
            }
            public override void Visit(StatementWithCtesAndXmlNamespaces node)
            {
                // RECOMPILE and MAXRECURSION change how the statement runs, not which plan the optimizer may pick.
                var hint = node.OptimizerHints.FirstOrDefault(h => h.HintKind != OptimizerHintKind.Recompile && h.HintKind != OptimizerHintKind.MaxRecursion);
                if (hint != null) Add("SW057", "Query hints override the optimizer and outlive the data that justified them.", hint);
            }
            public override void Visit(DropIndexStatement node)
            {
                foreach (var clause in node.DropIndexClauses.OfType<BackwardsCompatibleDropIndexClause>())
                    Add("SW068", "DROP INDEX table.index is deprecated; use DROP INDEX index ON table.", clause);
            }
            public override void Visit(Permission node)
            {
                if (node.Identifiers.Count > 0 && string.Equals(node.Identifiers[0].Value, "ALL", StringComparison.OrdinalIgnoreCase))
                    Add("SW069", "ALL in GRANT, DENY and REVOKE is deprecated and does not cover every permission; list them.", node);
            }
            public override void Visit(CreateIndexStatement node)
            {
                if (node.Clustered == null) Add("SW072", "State CLUSTERED or NONCLUSTERED so the index type does not rest on the default.", node.Name ?? (TSqlFragment)node);
            }
            private static readonly HashSet<string> CompatibilityViews = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "sysobjects", "syscolumns", "sysindexes", "sysindexkeys", "sysusers", "sysdatabases", "syscomments", "sysprocesses", "sysfiles", "sysfilegroups",
                "systypes", "sysconstraints", "sysdepends", "sysforeignkeys", "sysreferences", "syslogins", "sysmembers", "syspermissions", "sysprotects",
                "sysservers", "sysaltfiles", "syslockinfo", "sysmessages", "sysperfinfo", "sysremotelogins", "sysoledbusers", "syscacheobjects", "sysdevices"
            };
            private readonly Dictionary<string, Identifier> cursors = new Dictionary<string, Identifier>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> deallocated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public override void Visit(DeclareCursorStatement node) { if (node.Name != null) cursors[node.Name.Value] = node.Name; }
            public override void Visit(DeallocateCursorStatement node) { if (node.Cursor?.Name?.Identifier != null) deallocated.Add(node.Cursor.Name.Identifier.Value); }
            public override void Visit(NamedTableReference node)
            {
                var name = node.SchemaObject;
                string? schema = name.SchemaIdentifier?.Value;
                if (CompatibilityViews.Contains(name.BaseIdentifier.Value) && (schema == null || schema.Equals("dbo", StringComparison.OrdinalIgnoreCase) || schema.Equals("sys", StringComparison.OrdinalIgnoreCase)))
                    Add("SW073", "This SQL Server 2000 system table is a deprecated compatibility view; use the sys catalog views.", name);
                if (node.TableHints.Count > 0 && HintParenthesis(node.TableHints[0]) != null)
                    Add("SW051", "Table hints without WITH are deprecated; write WITH (hint).", node.TableHints[0]);
            }
            // Cursors are collected while the script is walked; the caller asks once the walk is over.
            internal void Finish()
            {
                foreach (var cursor in cursors.Where(c => !deallocated.Contains(c.Key)))
                    Add("SW066", "Cursor " + cursor.Key + " is never deallocated; it holds its resources until the connection closes.", cursor.Value);
            }

            private sealed class TriggerBody : TSqlFragmentVisitor
            {
                internal readonly List<PrintStatement> Prints = new List<PrintStatement>();
                internal readonly List<SelectStatement> Selects = new List<SelectStatement>();
                public override void Visit(PrintStatement node) => Prints.Add(node);
                public override void Visit(SelectStatement node)
                {
                    if (node.Into == null && !(node.QueryExpression is QuerySpecification query && query.SelectElements.Any(e => e is SelectSetVariable))) Selects.Add(node);
                }
            }

            private sealed class ProcedureBody : TSqlFragmentVisitor
            {
                internal bool NoCount;
                internal readonly List<ReturnStatement> BareReturns = new List<ReturnStatement>();
                internal readonly List<WaitForStatement> Delays = new List<WaitForStatement>();
                public override void Visit(PredicateSetStatement node)
                {
                    if (node.IsOn && node.Options.HasFlag(SetOptions.NoCount)) NoCount = true;
                }
                public override void Visit(ReturnStatement node) { if (node.Expression == null) BareReturns.Add(node); }
                public override void Visit(WaitForStatement node) { if (node.WaitForOption == WaitForOption.Delay) Delays.Add(node); }
            }

            private sealed class VariableUsage : TSqlFragmentVisitor
            {
                internal readonly List<Identifier> Declared = new List<Identifier>();
                internal readonly HashSet<string> Used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                public override void Visit(DeclareVariableElement node) { if (!(node is ProcedureParameter)) Declared.Add(node.VariableName); }
                public override void Visit(DeclareTableVariableBody node) => Declared.Add(node.VariableName);
                public override void Visit(VariableReference node) => Used.Add(node.Name);
                public override void Visit(VariableTableReference node) => Used.Add(node.Variable.Name);
                // EXEC p @x = 1 names p's parameter, not a local; a TVF's RETURNS @t TABLE is its result, used by RETURN.
                public override void ExplicitVisit(ExecuteParameter node) { node.ParameterValue?.Accept(this); }
                public override void ExplicitVisit(TableValuedFunctionReturnType node) { }
            }
        }
    }
}

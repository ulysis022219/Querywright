using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Querywright.Core
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
        /// Counts open parentheses, CASE and BEGIN blocks per batch, operator chains per statement, plus ELSE IF chains (each nests the next IF,
        /// but costs less stack than a parenthesis, hence the quarter weight). Lexing is iterative, so this is safe on any input.
        /// </summary>
        internal static bool TooDeep(string sql)
        {
            var tokens = new TSql170Parser(true).GetTokenStream(new StringReader(sql), out _);
            int depth = 0, elseIf = 0, chain = 0;
            TSqlParserToken? previous = null;
            foreach (var token in tokens)
            {
                switch (token.TokenType)
                {
                    case TSqlTokenType.WhiteSpace: case TSqlTokenType.SingleLineComment: case TSqlTokenType.MultilineComment: continue;
                    case TSqlTokenType.Go: depth = 0; elseIf = 0; chain = 0; break;
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
                if (depth + elseIf / 4 > MaxDepth || chain > MaxChain) return true;
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

        /// <summary>Rules with a mechanical fix.</summary>
        public static readonly IReadOnlyCollection<string> FixableRules = new[] { "SW001", "SW003", "SW009", "SW010", "SW015", "SW016", "SW017" };

        /// <summary>The edit that resolves one diagnostic, or null when the rule has no safe fix here. The result must still parse.</summary>
        public static TextEdit? Fix(string sql, SqlDiagnostic diagnostic, IReadOnlyList<SchemaTable>? tables = null, string defaultSchema = "dbo")
        {
            if (sql == null) throw new ArgumentNullException(nameof(sql));
            if (diagnostic == null) throw new ArgumentNullException(nameof(diagnostic));
            if (sql.Length > 1_000_000 || diagnostic.Offset < 0 || diagnostic.Offset + diagnostic.Length > sql.Length) return null;
            var parser = new TSql170Parser(true);
            var script = parser.ParseSafe(sql, out var errors);
            if (errors.Count > 0) return null;
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
                case "SW009":
                    edit = new TextEdit(diagnostic.Offset, diagnostic.Length, "SCOPE_IDENTITY()");
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
            parser.ParseSafe(Apply(sql, edit), out var after);
            return after.Count == 0 ? edit : null;
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
                // Back to front so earlier offsets stay valid; overlapping fixes wait for the next round.
                foreach (var diagnostic in result.Diagnostics.Where(d => FixableRules.Contains(d.Rule)).OrderByDescending(d => d.Offset))
                {
                    if (diagnostic.Offset + diagnostic.Length > limit) continue;
                    var edit = Fix(sql, diagnostic, tables, defaultSchema);
                    if (edit == null || edit.Start + edit.Length > limit) continue;
                    string next = Apply(sql, edit);
                    new TSql170Parser(true).ParseSafe(next, out var errors);
                    if (errors.Count > 0) continue;
                    sql = next; limit = edit.Start; applied++;
                }
                if (applied == 0) break;
                count += applied;
            }
            return (sql, count);
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
            internal ProcedureStatementBody? Procedure;
            internal TSqlStatement? Declaration;
            internal TSqlFragment? Element;
            private TSqlStatement? current;
            internal Spans(int offset, int length) { this.offset = offset; this.length = length; }
            private bool At(TSqlFragment node) => node.StartOffset == offset && node.FragmentLength == length;
            public override void Visit(BooleanComparisonExpression node) { if (At(node)) Comparison = node; }
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

        // Inline directives in comments: querywright-disable [IDs], querywright-enable [IDs], querywright-disable-next-line [IDs].
        // No IDs means every rule. Only rule diagnostics pass through here; parse errors return earlier.
        private sealed class Suppression
        {
            private static readonly Regex Directive = new Regex(@"^(?:--|/\*)\s*querywright-(disable-next-line|disable|enable)(?![\w-])(.*?)(?:\*/)?$",
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
            }
            public override void Visit(BooleanComparisonExpression node)
            {
                if (IsNull(node.FirstExpression) || IsNull(node.SecondExpression))
                    Add("SW003", "Use IS NULL or IS NOT NULL instead of comparing with NULL.", node);
                if (IsCountSubquery(node.FirstExpression) && IsZero(node.SecondExpression) || IsZero(node.FirstExpression) && IsCountSubquery(node.SecondExpression))
                    Add("SW028", "Comparing a COUNT subquery with 0 counts every row; use EXISTS or NOT EXISTS.", node);
                if (node.ComparisonType == BooleanComparisonType.NotLessThan || node.ComparisonType == BooleanComparisonType.NotGreaterThan)
                    Add("SW042", "!< and !> are nonstandard; use >= or <=.", node);
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
            }
            public override void Visit(SelectScalarExpression node)
            {
                if (node.ColumnName?.ValueExpression is StringLiteral)
                    Add("SW040", "String literals as column aliases are deprecated; use AS [alias].", node.ColumnName);
            }
            public override void Visit(LikePredicate node)
            {
                if (Unwrap(node.FirstExpression) is ColumnReferenceExpression && Unwrap(node.SecondExpression) is StringLiteral pattern && pattern.Value.StartsWith("%", StringComparison.Ordinal))
                    Add("SW033", "LIKE with a leading % cannot seek an index.", node);
            }
            private readonly HashSet<BooleanComparisonExpression> sargChecked = new HashSet<BooleanComparisonExpression>();
            public override void Visit(WhereClause node) => CheckSargable(node.SearchCondition);
            public override void Visit(QualifiedJoin node) => CheckSargable(node.SearchCondition);
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
            }
        }
    }
}

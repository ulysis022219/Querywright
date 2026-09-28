using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Querywright.Core
{
    public sealed class SchemaTable
    {
        public string Schema { get; }
        public string Name { get; }
        public IReadOnlyList<string> Columns { get; }
        public SchemaTable(string schema, string name, params string[] columns)
        {
            if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(name) || columns == null || columns.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Schema, table and column names must be nonempty.");
            Schema = schema; Name = name; Columns = Array.AsReadOnly((string[])columns.Clone());
        }
    }

    public sealed class CompletionItem
    {
        public string Name { get; }
        public string InsertText { get; }
        public string Description { get; }
        internal CompletionItem(string name, string insert, string description)
        { Name = name; InsertText = insert; Description = description; }
    }

    public sealed class CompletionResult
    {
        public int Start { get; }
        public int Length { get; }
        public IReadOnlyList<CompletionItem> Items { get; }
        public string Limitation { get; }
        internal CompletionResult(int start, int length, IEnumerable<CompletionItem> items, string limitation = "")
        { Start = start; Length = length; Items = items.ToArray(); Limitation = limitation; }
    }

    public sealed class TextEdit
    {
        public int Start { get; }
        public int Length { get; }
        public string Text { get; }
        internal TextEdit(int start, int length, string text) { Start = start; Length = length; Text = text; }
    }

    public static class SqlCompletion
    {
        private const string Marker = "__QuerywrightCompletionMarker__";
        private static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

        public static CompletionResult Complete(string sql, int position, IReadOnlyList<SchemaTable> tables,
            string defaultSchema = "dbo", bool caseSensitive = false)
        {
            if (sql == null || tables == null) throw new ArgumentNullException(sql == null ? nameof(sql) : nameof(tables));
            if (position < 0 || position > sql.Length) throw new ArgumentOutOfRangeException(nameof(position));
            if (sql.Length > 1_000_000) throw new ArgumentException("Completion input exceeds 1,000,000 characters.");
            var parser = new TSql170Parser(true);
            var tokens = parser.GetTokenStream(new StringReader(sql), out var lexicalErrors);
            int start = position, end = position;
            string prefix = "";
            foreach (var token in tokens)
            {
                int tokenEnd = token.Offset + (token.Text?.Length ?? 0);
                if (token.Offset > position || tokenEnd < position) continue;
                if (token.TokenType == TSqlTokenType.SingleLineComment || token.TokenType == TSqlTokenType.MultilineComment ||
                    token.TokenType == TSqlTokenType.AsciiStringLiteral || token.TokenType == TSqlTokenType.UnicodeStringLiteral)
                {
                    if (position < tokenEnd || token.TokenType == TSqlTokenType.SingleLineComment)
                        return new CompletionResult(position, 0, Array.Empty<CompletionItem>(), "Completion is inactive inside literals and comments.");
                }
                if (token.TokenType == TSqlTokenType.Identifier)
                {
                    start = token.Offset; end = tokenEnd;
                    prefix = sql.Substring(start, position - start);
                    break;
                }
            }
            if (lexicalErrors.Count > 0 || sql.Contains(Marker))
                return new CompletionResult(start, end - start, Array.Empty<CompletionItem>(), "Lexical recovery required.");
            string repaired = sql.Substring(0, start) + Marker + sql.Substring(end);
            var fragment = parser.Parse(new StringReader(repaired), out var errors);
            if (errors.Count > 0)
                return new CompletionResult(start, end - start, Array.Empty<CompletionItem>(), "SQL outside the current identifier is incomplete or unsupported.");
            var visitor = new Resolver(tables, defaultSchema, caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
            fragment.Accept(visitor);
            return new CompletionResult(start, end - start, visitor.Items
                .Where(i => i.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .GroupBy(i => i.InsertText, StringComparer.Ordinal).Select(g => g.First())
                .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase));
        }

        public static TextEdit ExpandWildcard(string sql, int position, IReadOnlyList<SchemaTable> tables,
            string defaultSchema = "dbo", bool caseSensitive = false)
        {
            if (sql == null || tables == null) throw new ArgumentNullException(sql == null ? nameof(sql) : nameof(tables));
            if (position < 0 || position > sql.Length) throw new ArgumentOutOfRangeException(nameof(position));
            if (sql.Length > 1_000_000) throw new ArgumentException("Expansion input exceeds 1,000,000 characters.");
            var parser = new TSql170Parser(true);
            var fragment = parser.Parse(new StringReader(sql), out var errors);
            if (errors.Count > 0) throw new FormatException("Fix SQL syntax errors before expanding a wildcard.");
            var visitor = new Resolver(tables, defaultSchema, caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase, position);
            fragment.Accept(visitor);
            if (visitor.Expansion == null) throw new InvalidOperationException("Place the caret on * or alias.* in a SELECT list.");
            string result = sql.Substring(0, visitor.Expansion.Start) + visitor.Expansion.Text +
                sql.Substring(visitor.Expansion.Start + visitor.Expansion.Length);
            parser.Parse(new StringReader(result), out var finalErrors);
            if (finalErrors.Count > 0) throw new InvalidOperationException("Expansion produced invalid SQL; original text retained.");
            return visitor.Expansion;
        }

        private sealed class Resolver : TSqlFragmentVisitor
        {
            internal readonly List<CompletionItem> Items = new List<CompletionItem>();
            internal TextEdit? Expansion;
            private readonly int wildcardPosition = -1;
            private readonly IReadOnlyList<SchemaTable> tables;
            private readonly string defaultSchema;
            private readonly StringComparer names;
            private readonly List<QuerySpecification> scopes = new List<QuerySpecification>();
            private readonly Stack<int> scopeBoundaries = new Stack<int>();
            private readonly Stack<Dictionary<string, CommonTableExpression>> cteScopes = new Stack<Dictionary<string, CommonTableExpression>>();
            internal Resolver(IReadOnlyList<SchemaTable> tables, string defaultSchema, StringComparer names)
            { this.tables = tables; this.defaultSchema = defaultSchema; this.names = names; }
            internal Resolver(IReadOnlyList<SchemaTable> tables, string defaultSchema, StringComparer names, int wildcardPosition)
                : this(tables, defaultSchema, names) { this.wildcardPosition = wildcardPosition; }

            public override void Visit(SelectStarExpression node)
            {
                if (Expansion != null || node.StartOffset > wildcardPosition || wildcardPosition > node.StartOffset + node.FragmentLength) return;
                var ids = node.Qualifier?.Identifiers;
                if (ids != null && ids.Count > 1) throw new InvalidOperationException("Use an alias or table name qualifier, not schema.table.*.");
                var from = scopes.Count == 0 ? null : scopes[scopes.Count - 1].FromClause;
                if (from == null) throw new InvalidOperationException("Wildcard has no FROM clause to expand.");
                var references = from.TableReferences.SelectMany(Tables).ToArray();
                var parts = new List<string>();
                bool qualify = ids != null || references.Length > 1;
                foreach (var reference in references)
                {
                    string? alias = reference?.Alias?.Value ?? (reference as NamedTableReference)?.SchemaObject.BaseIdentifier.Value;
                    if (ids != null && (alias == null || !names.Equals(alias, ids[0].Value))) continue;
                    var columns = reference == null ? Array.Empty<string>() : Columns(reference).ToArray();
                    if (alias == null || columns.Length == 0)
                        throw new InvalidOperationException("Columns unknown for " + (alias ?? "a FROM source") + "; offline schema, CTE or derived column list required.");
                    parts.AddRange(columns.Select(c => qualify ? Quote(alias) + "." + Quote(c) : Quote(c)));
                }
                if (parts.Count == 0) throw new InvalidOperationException("Wildcard qualifier " + ids![0].Value + " is not in this FROM clause.");
                Expansion = new TextEdit(node.StartOffset, node.FragmentLength, string.Join(", ", parts));
            }

            public override void ExplicitVisit(SelectStatement node)
            {
                var ctes = new Dictionary<string, CommonTableExpression>(names);
                if (node.WithCtesAndXmlNamespaces != null)
                    foreach (var cte in node.WithCtesAndXmlNamespaces.CommonTableExpressions) ctes[cte.ExpressionName.Value] = cte;
                cteScopes.Push(ctes);
                base.ExplicitVisit(node);
                cteScopes.Pop();
            }

            public override void ExplicitVisit(QueryDerivedTable node)
            {
                // ponytail: derived tables isolate outer scopes; correlated APPLY binding requires join-side tracking.
                scopeBoundaries.Push(scopes.Count);
                base.ExplicitVisit(node);
                scopeBoundaries.Pop();
            }

            public override void ExplicitVisit(QuerySpecification node)
            {
                scopes.Add(node);
                base.ExplicitVisit(node);
                scopes.RemoveAt(scopes.Count - 1);
            }

            public override void Visit(NamedTableReference node)
            {
                if (node.SchemaObject.BaseIdentifier.Value != Marker) return;
                string? schema = node.SchemaObject.SchemaIdentifier?.Value;
                if (node.SchemaObject.DatabaseIdentifier != null || node.SchemaObject.ServerIdentifier != null) return;
                foreach (var table in tables.Where(t => schema == null || names.Equals(t.Schema, schema)))
                    Items.Add(new CompletionItem(table.Name, schema == null ? Quote(table.Schema) + "." + Quote(table.Name) : Quote(table.Name),
                        table.Schema + "." + table.Name));
            }

            public override void Visit(ColumnReferenceExpression node)
            {
                var ids = node.MultiPartIdentifier?.Identifiers;
                if (ids == null || ids.Count == 0 || ids[ids.Count - 1].Value != Marker || ids.Count > 2) return;
                string? qualifier = ids.Count == 2 ? ids[0].Value : null;
                var seen = new HashSet<string>(names);
                int boundary = scopeBoundaries.Count == 0 ? 0 : scopeBoundaries.Peek();
                for (int i = scopes.Count - 1; i >= boundary; i--)
                {
                    if (scopes[i].FromClause == null) continue;
                    foreach (var reference in scopes[i].FromClause.TableReferences.SelectMany(Tables))
                    {
                        string? alias = reference?.Alias?.Value ?? (reference as NamedTableReference)?.SchemaObject.BaseIdentifier.Value;
                        if (alias == null) continue;
                        if (!seen.Add(alias) || (qualifier != null && !names.Equals(alias, qualifier))) continue;
                        foreach (string column in Columns(reference!))
                                Items.Add(new CompletionItem(column, qualifier == null ? Quote(alias) + "." + Quote(column) : Quote(column),
                                    alias + "." + column));
                        if (qualifier != null) return; // Inner aliases shadow outer aliases, even when metadata is missing.
                    }
                }
            }

            private IEnumerable<string> Columns(TableReferenceWithAlias reference)
            {
                if (reference is QueryDerivedTable derived)
                    return derived.Columns.Count > 0 ? derived.Columns.Select(c => c.Value) : Projection(derived.QueryExpression);
                if (!(reference is NamedTableReference named)) return Array.Empty<string>();
                if (named.SchemaObject.SchemaIdentifier == null)
                    foreach (var scope in cteScopes)
                        if (scope.TryGetValue(named.SchemaObject.BaseIdentifier.Value, out var cte))
                            return cte.Columns.Count > 0 ? cte.Columns.Select(c => c.Value) : Projection(cte.QueryExpression);
                return Find(named)?.Columns ?? Array.Empty<string>();
            }

            private static IEnumerable<string> Projection(QueryExpression expression)
            {
                if (expression is QueryParenthesisExpression parenthesis) return Projection(parenthesis.QueryExpression);
                if (expression is BinaryQueryExpression binary) return Projection(binary.FirstQueryExpression);
                if (!(expression is QuerySpecification query)) return Array.Empty<string>();
                return query.SelectElements.OfType<SelectScalarExpression>().Select(column => column.ColumnName?.Value
                    ?? (column.Expression as ColumnReferenceExpression)?.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value)
                    .Where(name => name != null).Select(name => name!);
            }

            private SchemaTable? Find(NamedTableReference reference)
            {
                var name = reference.SchemaObject;
                if (name.DatabaseIdentifier != null || name.ServerIdentifier != null) return null;
                string schema = name.SchemaIdentifier?.Value ?? defaultSchema;
                return tables.FirstOrDefault(t => names.Equals(t.Schema, schema) && names.Equals(t.Name, name.BaseIdentifier.Value));
            }

            // Yields null for unsupported sources so wildcard expansion can reject rather than omit them.
            private static IEnumerable<TableReferenceWithAlias?> Tables(TableReference reference)
            {
                if (reference is TableReferenceWithAlias aliased) yield return aliased;
                else if (reference is JoinTableReference join)
                {
                    foreach (var table in Tables(join.FirstTableReference)) yield return table;
                    foreach (var table in Tables(join.SecondTableReference)) yield return table;
                }
                else if (reference is JoinParenthesisTableReference parenthesis)
                    foreach (var table in Tables(parenthesis.Join)) yield return table;
                else yield return null;
            }
        }
    }
}

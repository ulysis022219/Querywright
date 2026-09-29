using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Querywright.Core
{
    /// <summary>Either a span in the same script (Offset >= 0) or a database object name.</summary>
    public sealed class DefinitionTarget
    {
        public int Offset { get; }
        public int Length { get; }
        public string? Schema { get; }
        public string? Name { get; }
        /// <summary>Database part of a three-part name (OtherDb.dbo.Proc); null for the connected database.</summary>
        public string? Database { get; }
        internal DefinitionTarget(int offset, int length) { Offset = offset; Length = length; }
        internal DefinitionTarget(string? schema, string name, string? database = null) { Offset = -1; Schema = schema; Name = name; Database = database; }
    }

    public sealed class StatementSpan
    {
        public int Start { get; }
        public int Length { get; }
        internal StatementSpan(int start, int length) { Start = start; Length = length; }
    }

    public static class SqlNavigation
    {
        public static DefinitionTarget? FindDefinition(string sql, int position)
        {
            if (sql == null) throw new ArgumentNullException(nameof(sql));
            if (position < 0 || position > sql.Length) throw new ArgumentOutOfRangeException(nameof(position));
            if (sql.Length > 1_000_000) throw new ArgumentException("Navigation input exceeds 1,000,000 characters.");
            var script = (TSqlScript)new TSql170Parser(true).Parse(new StringReader(sql), out var errors);
            // Scripts being edited rarely parse; object names still resolve from the tokens around the caret.
            if (errors.Count != 0) return NameAt(sql, position);
            var batch = script.Batches.FirstOrDefault(b => Contains(b, position));
            if (batch == null) return null;
            var visitor = new Finder(position);
            batch.Accept(visitor);
            var names = StringComparer.OrdinalIgnoreCase;

            if (visitor.Variable != null)
            {
                var declaration = visitor.Declarations.FirstOrDefault(d => names.Equals(d.Value, visitor.Variable));
                return declaration == null ? null : new DefinitionTarget(declaration.StartOffset, declaration.FragmentLength);
            }
            if (visitor.Qualifier != null)
            {
                foreach (var scope in visitor.Sources.Where(s => Contains(s.Owner, position)).OrderBy(s => s.Owner.FragmentLength))
                    foreach (var source in scope.Tables)
                    {
                        if (source.Alias != null && names.Equals(source.Alias.Value, visitor.Qualifier))
                            return new DefinitionTarget(source.Alias.StartOffset, source.Alias.FragmentLength);
                        if (source.Alias == null && source is NamedTableReference named && names.Equals(named.SchemaObject.BaseIdentifier.Value, visitor.Qualifier))
                            return Object(named.SchemaObject, visitor, position);
                    }
                return null;
            }
            if (visitor.Function != null) return visitor.Function;
            return visitor.Object == null ? null : Object(visitor.Object, visitor, position);
        }

        /// <summary>The dotted object name at the caret from tokens alone (db.schema.name, schema.name, name); null on variables and keywords.</summary>
        private static DefinitionTarget? NameAt(string sql, int position)
        {
            var tokens = new TSql170Parser(true).GetTokenStream(new StringReader(sql), out _);
            bool IsName(TSqlParserToken t) => t.TokenType == TSqlTokenType.Identifier || t.TokenType == TSqlTokenType.QuotedIdentifier;
            int at = -1;
            for (int i = 0; i < tokens.Count; i++)
                if (IsName(tokens[i]) && tokens[i].Offset <= position && position <= tokens[i].Offset + tokens[i].Text.Length) { at = i; break; }
            if (at < 0) return null;
            bool Part(TSqlParserToken t) => IsName(t) || t.TokenType == TSqlTokenType.Dot;
            int first = at, last = at;
            while (first > 0 && Part(tokens[first - 1])) first--;
            while (last + 1 < tokens.Count && Part(tokens[last + 1])) last++;
            var parts = new List<string?>();
            string? part = null;
            for (int i = first; i <= last; i++)
                if (tokens[i].TokenType == TSqlTokenType.Dot) { parts.Add(part); part = null; }
                else part = Unquote(tokens[i].Text);
            parts.Add(part);
            if (parts.Count > 3 || parts[parts.Count - 1] == null) return null; // linked server or dangling dot
            return new DefinitionTarget(parts.Count > 1 ? parts[parts.Count - 2] : null, parts[parts.Count - 1]!, parts.Count > 2 ? parts[0] : null);
        }

        private static string Unquote(string text) =>
            text.Length > 1 && text[0] == '[' ? text.Substring(1, text.Length - 2).Replace("]]", "]")
            : text.Length > 1 && text[0] == '"' ? text.Substring(1, text.Length - 2).Replace("\"\"", "\"") : text;

        private static DefinitionTarget? Object(SchemaObjectName name, Finder visitor, int position)
        {
            if (name.SchemaIdentifier == null && name.DatabaseIdentifier == null)
            {
                var cte = visitor.Ctes.Where(c => Contains(c.Owner, position))
                    .SelectMany(c => c.Items).FirstOrDefault(c => StringComparer.OrdinalIgnoreCase.Equals(c.ExpressionName.Value, name.BaseIdentifier.Value));
                if (cte != null) return new DefinitionTarget(cte.ExpressionName.StartOffset, cte.ExpressionName.FragmentLength);
            }
            // ponytail: linked-server names are left to the host's native navigation.
            if (name.ServerIdentifier != null) return null;
            string? schema = string.IsNullOrEmpty(name.SchemaIdentifier?.Value) ? null : name.SchemaIdentifier!.Value; // OtherDb..Name
            return new DefinitionTarget(schema, name.BaseIdentifier.Value, name.DatabaseIdentifier?.Value);
        }

        public static StatementSpan? StatementAt(string sql, int position)
        {
            if (sql == null) throw new ArgumentNullException(nameof(sql));
            if (position < 0 || position > sql.Length) throw new ArgumentOutOfRangeException(nameof(position));
            if (sql.Length > 1_000_000) throw new ArgumentException("Navigation input exceeds 1,000,000 characters.");
            var parser = new TSql170Parser(true);
            int start = 0, end = sql.Length;
            foreach (var (lineStart, lineEnd) in GoLines(sql, parser))
            {
                if (lineEnd <= position) start = lineEnd;
                else { end = lineStart; break; }
            }
            string region = sql.Substring(start, end - start);
            var script = (TSqlScript)parser.Parse(new StringReader(region), out var errors);
            if (errors.Count == 0)
            {
                var statements = script.Batches.SelectMany(b => b.Statements).ToArray();
                var statement = statements.LastOrDefault(s => start + s.StartOffset <= position) ?? statements.FirstOrDefault();
                if (statement == null) return null;
                var (offset, length) = Span(statement);
                return new StatementSpan(start + offset, length);
            }
            string trimmed = region.Trim();
            return trimmed.Length == 0 ? null : new StatementSpan(start + region.IndexOf(trimmed, StringComparison.Ordinal), trimmed.Length);
        }

        /// <summary>Statement extent including a trailing semicolon the parser leaves outside (e.g. after a procedure body).</summary>
        internal static (int Start, int Length) Span(TSqlStatement statement)
        {
            var tokens = statement.ScriptTokenStream;
            int end = statement.StartOffset + statement.FragmentLength, i = statement.LastTokenIndex + 1;
            while (i < tokens.Count && tokens[i].TokenType == TSqlTokenType.WhiteSpace) i++;
            if (i < tokens.Count && tokens[i].TokenType == TSqlTokenType.Semicolon && tokens[statement.LastTokenIndex].TokenType != TSqlTokenType.Semicolon)
                end = tokens[i].Offset + 1;
            return (statement.StartOffset, end - statement.StartOffset);
        }

        /// <summary>GO separator lines as [line start, index after newline).</summary>
        internal static IEnumerable<(int Start, int End)> GoLines(string sql, TSql170Parser parser)
        {
            var tokens = parser.GetTokenStream(new StringReader(sql), out var errors);
            // ponytail: a script that cannot be tokenized (e.g. unterminated string) falls back to GO line matching.
            var starts = errors.Count == 0
                ? tokens.Where(t => t.TokenType == TSqlTokenType.Go).Select(t => t.Offset == 0 ? 0 : sql.LastIndexOf('\n', t.Offset - 1) + 1)
                : Regex.Matches(sql, @"^[ \t]*GO\b", RegexOptions.Multiline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)).Cast<Match>().Select(m => m.Index);
            foreach (int start in starts.Distinct().ToArray())
            {
                int newline = sql.IndexOf('\n', start);
                int end = newline < 0 ? sql.Length : newline + 1;
                if (Regex.IsMatch(sql.Substring(start, end - start).TrimEnd('\r', '\n'), @"^[ \t]*GO(?:[ \t]+[0-9]+)?[ \t]*(?:--[^\r\n]*)?$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                    yield return (start, end);
            }
        }

        internal static bool Contains(TSqlFragment node, int position) =>
            node.StartOffset <= position && position <= node.StartOffset + node.FragmentLength;

        private sealed class Scope<T>
        {
            internal readonly TSqlFragment Owner;
            internal readonly IReadOnlyList<T> Items;
            internal Scope(TSqlFragment owner, IReadOnlyList<T> items) { Owner = owner; Items = items; }
        }

        internal sealed class Sources
        {
            internal readonly TSqlFragment Owner;
            internal readonly List<TableReferenceWithAlias> Tables = new List<TableReferenceWithAlias>();
            internal Sources(TSqlFragment owner, FromClause from)
            {
                Owner = owner;
                foreach (var reference in from.TableReferences) Add(reference);
            }
            private void Add(TableReference reference)
            {
                if (reference is TableReferenceWithAlias aliased) Tables.Add(aliased);
                else if (reference is JoinTableReference join) { Add(join.FirstTableReference); Add(join.SecondTableReference); }
                else if (reference is JoinParenthesisTableReference parenthesis) Add(parenthesis.Join);
            }
        }

        private sealed class Finder : TSqlFragmentVisitor
        {
            private readonly int position;
            internal string? Variable, Qualifier;
            internal SchemaObjectName? Object;
            internal DefinitionTarget? Function;
            internal readonly List<Identifier> Declarations = new List<Identifier>();
            internal readonly List<Sources> Sources = new List<Sources>();
            internal readonly List<Scope<CommonTableExpression>> Ctes = new List<Scope<CommonTableExpression>>();
            internal Finder(int position) { this.position = position; }

            public override void Visit(VariableReference node) { if (Contains(node, position)) Variable = node.Name; }
            public override void Visit(DeclareVariableElement node)
            {
                Declarations.Add(node.VariableName);
                if (Contains(node.VariableName, position)) Variable = node.VariableName.Value;
            }
            public override void Visit(DeclareTableVariableBody node)
            {
                Declarations.Add(node.VariableName);
                if (Contains(node.VariableName, position)) Variable = node.VariableName.Value;
            }
            public override void Visit(ColumnReferenceExpression node)
            {
                var ids = node.MultiPartIdentifier?.Identifiers;
                if (ids != null && ids.Count >= 2 && Contains(node, position)) Qualifier = ids[ids.Count - 2].Value;
            }
            public override void Visit(SelectStarExpression node)
            {
                var ids = node.Qualifier?.Identifiers;
                if (ids != null && Contains(node, position)) Qualifier = ids[ids.Count - 1].Value;
            }
            // dbo.fn(x) and db.dbo.fn(x) parse as calls, not object names.
            public override void Visit(FunctionCall node)
            {
                if (!Contains(node.FunctionName, position) || !(node.CallTarget is MultiPartIdentifierCallTarget target)) return;
                var ids = target.MultiPartIdentifier.Identifiers;
                if (ids.Count > 2) return;
                Function = new DefinitionTarget(ids[ids.Count - 1].Value, node.FunctionName.Value, ids.Count == 2 ? ids[0].Value : null);
            }
            public override void Visit(SchemaObjectName node)
            {
                if (Contains(node, position) && (Object == null || node.FragmentLength < Object.FragmentLength)) Object = node;
            }
            public override void Visit(QuerySpecification node) { if (node.FromClause != null) Sources.Add(new Sources(node, node.FromClause)); }
            public override void Visit(UpdateSpecification node) { if (node.FromClause != null) Sources.Add(new Sources(node, node.FromClause)); }
            public override void Visit(DeleteSpecification node) { if (node.FromClause != null) Sources.Add(new Sources(node, node.FromClause)); }
            public override void Visit(StatementWithCtesAndXmlNamespaces node)
            {
                if (node.WithCtesAndXmlNamespaces != null)
                    Ctes.Add(new Scope<CommonTableExpression>(node, node.WithCtesAndXmlNamespaces.CommonTableExpressions.ToArray()));
            }
        }
    }
}

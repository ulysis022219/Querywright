using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        internal DefinitionTarget(int offset, int length) { Offset = offset; Length = length; }
        internal DefinitionTarget(string? schema, string name) { Offset = -1; Schema = schema; Name = name; }
    }

    public static class SqlNavigation
    {
        public static DefinitionTarget? FindDefinition(string sql, int position)
        {
            if (sql == null) throw new ArgumentNullException(nameof(sql));
            if (position < 0 || position > sql.Length) throw new ArgumentOutOfRangeException(nameof(position));
            if (sql.Length > 1_000_000) throw new ArgumentException("Navigation input exceeds 1,000,000 characters.");
            var script = (TSqlScript)new TSql170Parser(true).Parse(new StringReader(sql), out var errors);
            if (errors.Count != 0) throw new FormatException("Fix SQL syntax errors before navigating.");
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
            return visitor.Object == null ? null : Object(visitor.Object, visitor, position);
        }

        private static DefinitionTarget Object(SchemaObjectName name, Finder visitor, int position)
        {
            if (name.SchemaIdentifier == null && name.DatabaseIdentifier == null)
            {
                var cte = visitor.Ctes.Where(c => Contains(c.Owner, position))
                    .SelectMany(c => c.Items).FirstOrDefault(c => StringComparer.OrdinalIgnoreCase.Equals(c.ExpressionName.Value, name.BaseIdentifier.Value));
                if (cte != null) return new DefinitionTarget(cte.ExpressionName.StartOffset, cte.ExpressionName.FragmentLength);
            }
            // ponytail: database/server parts are left to the host's native navigation.
            return new DefinitionTarget(name.SchemaIdentifier?.Value, name.BaseIdentifier.Value);
        }

        private static bool Contains(TSqlFragment node, int position) =>
            node.StartOffset <= position && position <= node.StartOffset + node.FragmentLength;

        private sealed class Scope<T>
        {
            internal readonly TSqlFragment Owner;
            internal readonly IReadOnlyList<T> Items;
            internal Scope(TSqlFragment owner, IReadOnlyList<T> items) { Owner = owner; Items = items; }
        }

        private sealed class Sources
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

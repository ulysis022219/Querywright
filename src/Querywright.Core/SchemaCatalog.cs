using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Querywright.Core
{
    public static class SchemaCatalog
    {
        private sealed class Draft
        {
            internal string Schema = "", Name = "";
            internal string[] Columns = Array.Empty<string>();
            internal string?[] Types = Array.Empty<string?>();
            internal bool[] Generated = Array.Empty<bool>();
            internal string?[]? Notes;
            internal string[] PrimaryKey = Array.Empty<string>();
            internal readonly List<(string[] Columns, SchemaObjectName Target, string[] Referenced)> Keys = new List<(string[], SchemaObjectName, string[])>();
        }

        // Only what the column definition states; a table-level PRIMARY KEY is not folded into NOT NULL.
        private static string? Note(ColumnDefinition column, string ddl)
        {
            bool? nullable = column.Constraints.OfType<NullableConstraintDefinition>().Select(n => (bool?)n.Nullable).FirstOrDefault();
            if (column.Constraints.OfType<UniqueConstraintDefinition>().Any(u => u.IsPrimaryKey)) nullable = false;
            var value = column.DefaultConstraint?.Expression;
            string note = (nullable == null ? "" : nullable.Value ? "NULL" : "NOT NULL") +
                (value == null ? "" : " DEFAULT " + ddl.Substring(value.StartOffset, value.FragmentLength));
            return note.Length == 0 ? null : note.TrimStart();
        }

        public static IReadOnlyList<SchemaTable> FromDdl(string ddl)
        {
            if (ddl == null) throw new ArgumentNullException(nameof(ddl));
            if (ddl.Length > 1_000_000) throw new ArgumentException("Schema input exceeds 1,000,000 characters.");
            var fragment = new TSql170Parser(true).ParseSafe(ddl, out var errors);
            if (errors.Count > 0) throw new FormatException("Schema SQL has syntax errors; no partial catalog loaded.");
            var drafts = new List<Draft>();
            var alters = new List<AlterTableAddTableElementStatement>();
            foreach (var statement in ((TSqlScript)fragment).Batches.SelectMany(batch => batch.Statements))
            {
                if (statement is AlterTableAddTableElementStatement alter && alter.Definition.ColumnDefinitions.Count == 0 &&
                    (alter.Definition.Indexes?.Count ?? 0) == 0 && alter.Definition.TableConstraints.Count > 0 &&
                    alter.Definition.TableConstraints.All(c => c is ForeignKeyConstraintDefinition))
                {
                    Local(alter.SchemaObjectName);
                    alters.Add(alter);
                    continue;
                }
                if (!(statement is CreateTableStatement node))
                    throw new FormatException("Offline schema input must contain only CREATE TABLE statements, ALTER TABLE ... ADD FOREIGN KEY, comments and GO separators.");
                Local(node.SchemaObjectName);
                var draft = new Draft
                {
                    Schema = node.SchemaObjectName.SchemaIdentifier?.Value ?? "dbo", Name = node.SchemaObjectName.BaseIdentifier.Value,
                    Columns = node.Definition.ColumnDefinitions.Select(c => c.ColumnIdentifier.Value).ToArray(),
                    Types = node.Definition.ColumnDefinitions.Select(c => TypeName(c.DataType)).ToArray(),
                    Generated = node.Definition.ColumnDefinitions.Select(c => c.IdentityOptions != null || c.ComputedColumnExpression != null ||
                        c.DataType is SqlDataTypeReference t && (t.SqlDataTypeOption == SqlDataTypeOption.Timestamp || t.SqlDataTypeOption == SqlDataTypeOption.Rowversion)).ToArray(),
                    Notes = node.Definition.ColumnDefinitions.Select(c => Note(c, ddl)).ToArray()
                };
                foreach (var column in node.Definition.ColumnDefinitions)
                {
                    foreach (var key in column.Constraints.OfType<ForeignKeyConstraintDefinition>())
                        draft.Keys.Add((new[] { column.ColumnIdentifier.Value }, key.ReferenceTableName, key.ReferencedTableColumns.Select(c => c.Value).ToArray()));
                    if (column.Constraints.OfType<UniqueConstraintDefinition>().Any(u => u.IsPrimaryKey)) draft.PrimaryKey = new[] { column.ColumnIdentifier.Value };
                }
                AddConstraints(draft, node.Definition.TableConstraints);
                drafts.Add(draft);
            }
            if (drafts.GroupBy(t => t.Schema + "\0" + t.Name, StringComparer.Ordinal).Any(g => g.Count() > 1))
                throw new FormatException("Schema SQL contains duplicate table definitions.");
            foreach (var alter in alters)
            {
                var name = alter.SchemaObjectName;
                var draft = drafts.FirstOrDefault(d => Same(d, name.SchemaIdentifier?.Value ?? "dbo", name.BaseIdentifier.Value))
                    ?? throw new FormatException("ALTER TABLE " + name.BaseIdentifier.Value + " targets a table not defined in the schema SQL.");
                AddConstraints(draft, alter.Definition.TableConstraints);
            }
            return drafts.Select(d => new SchemaTable(d.Schema, d.Name, d.Columns, d.Types, d.Keys.Select(k => Key(d, k, drafts)).Where(k => k != null).Select(k => k!).ToArray(), d.Generated, false, d.Notes)).ToArray();
        }

        private static bool Same(Draft draft, string schema, string name) =>
            string.Equals(draft.Schema, schema, StringComparison.OrdinalIgnoreCase) && string.Equals(draft.Name, name, StringComparison.OrdinalIgnoreCase);

        private static void Local(SchemaObjectName name)
        {
            if (name.DatabaseIdentifier != null || name.ServerIdentifier != null)
                throw new FormatException("Offline schema SQL must describe one database without cross-database table names.");
        }

        private static void AddConstraints(Draft draft, IEnumerable<ConstraintDefinition> constraints)
        {
            foreach (var constraint in constraints)
            {
                if (constraint is ForeignKeyConstraintDefinition key)
                {
                    Local(key.ReferenceTableName);
                    draft.Keys.Add((key.Columns.Select(c => c.Value).ToArray(), key.ReferenceTableName, key.ReferencedTableColumns.Select(c => c.Value).ToArray()));
                }
                else if (constraint is UniqueConstraintDefinition unique && unique.IsPrimaryKey)
                    draft.PrimaryKey = unique.Columns.Select(c => c.Column.MultiPartIdentifier.Identifiers.Last().Value).ToArray();
            }
        }

        // REFERENCES without a column list targets the referenced table's primary key; keys that cannot be resolved are skipped.
        private static SchemaForeignKey? Key(Draft owner, (string[] Columns, SchemaObjectName Target, string[] Referenced) key, List<Draft> drafts)
        {
            string schema = key.Target.SchemaIdentifier?.Value ?? "dbo", table = key.Target.BaseIdentifier.Value;
            var referenced = key.Referenced.Length > 0 ? key.Referenced
                : drafts.FirstOrDefault(d => Same(d, schema, table))?.PrimaryKey ?? Array.Empty<string>();
            if (referenced.Length == 0) return null;
            if (referenced.Length != key.Columns.Length || key.Columns.Length == 0) throw new FormatException("Foreign key on " + owner.Name + " has mismatched or unknown columns.");
            if (key.Columns.Any(c => !owner.Columns.Contains(c, StringComparer.OrdinalIgnoreCase)))
                throw new FormatException("Foreign key on " + owner.Name + " names a column the table does not define.");
            return new SchemaForeignKey(key.Columns, schema, table, referenced);
        }

        internal static string? TypeName(DataTypeReference? type)
        {
            if (type == null) return null;
            string name = string.Join(".", type.Name.Identifiers.Select(i => i.Value));
            if (!(type is SqlDataTypeReference sql)) return name;
            name = name.ToLowerInvariant();
            return sql.Parameters.Count == 0 ? name : name + "(" + string.Join(",", sql.Parameters.Select(p => p is MaxLiteral ? "max" : p.Value)) + ")";
        }
    }
}

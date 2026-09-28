using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlWorkbench.Core
{
    public static class SchemaCatalog
    {
        public static IReadOnlyList<SchemaTable> FromDdl(string ddl)
        {
            if (ddl == null) throw new ArgumentNullException(nameof(ddl));
            if (ddl.Length > 1_000_000) throw new ArgumentException("Schema input exceeds 1,000,000 characters.");
            var fragment = new TSql170Parser(true).Parse(new StringReader(ddl), out var errors);
            if (errors.Count > 0) throw new FormatException("Schema SQL has syntax errors; no partial catalog loaded.");
            var items = new List<SchemaTable>();
            foreach (var statement in ((TSqlScript)fragment).Batches.SelectMany(batch => batch.Statements))
            {
                if (!(statement is CreateTableStatement node))
                    throw new FormatException("Offline schema input must contain only CREATE TABLE statements, comments and GO separators.");
                if (node.SchemaObjectName.DatabaseIdentifier != null || node.SchemaObjectName.ServerIdentifier != null)
                    throw new FormatException("Offline schema SQL must describe one database without cross-database table names.");
                items.Add(new SchemaTable(node.SchemaObjectName.SchemaIdentifier?.Value ?? "dbo",
                    node.SchemaObjectName.BaseIdentifier.Value, node.Definition.ColumnDefinitions.Select(c => c.ColumnIdentifier.Value).ToArray()));
            }
            if (items.GroupBy(t => t.Schema + "\0" + t.Name, StringComparer.Ordinal).Any(g => g.Count() > 1))
                throw new FormatException("Schema SQL contains duplicate table definitions.");
            return items.ToArray();
        }
    }
}

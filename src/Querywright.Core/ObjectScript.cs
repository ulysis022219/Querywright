using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Querywright.Core
{
    /// <summary>A table column as read from sys.columns, for the hover Script and Summary tabs.</summary>
    public sealed class ScriptColumn
    {
        public string Name { get; }
        public string Type { get; }
        /// <summary>Length, precision or scale without parentheses, e.g. "50", "max", "18,2"; null when the type takes none.</summary>
        public string? Size { get; }
        public bool Nullable { get; }
        public string? Collation { get; set; }
        /// <summary>"seed, increment" for an identity column.</summary>
        public string? Identity { get; set; }
        /// <summary>Computed column expression as stored, e.g. "([a]+[b])".</summary>
        public string? Computed { get; set; }
        public bool Persisted { get; set; }
        public string? DefaultName { get; set; }
        public string? Default { get; set; }

        public ScriptColumn(string name, string type, string? size, bool nullable)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type)) throw new ArgumentException("Column name and type must be nonempty.");
            Name = name; Type = type; Size = string.IsNullOrEmpty(size) ? null : size; Nullable = nullable;
        }

        /// <summary>Summary tab data type, e.g. varchar(50).</summary>
        public string DataType => Computed != null ? "computed" : Type + (Size == null ? "" : "(" + Size + ")");
    }

    /// <summary>CREATE TABLE script for the hover popup: columns, then constraints as ALTER TABLE ... ADD, each batch ended by GO.</summary>
    public static class ObjectScript
    {
        public static string Bracket(string name) => "[" + name.Replace("]", "]]") + "]";

        private static string List(IEnumerable<string> columns) => "(" + string.Join(", ", columns.Select(Bracket)) + ")";

        public static string Key(string name, bool primary, bool clustered, IEnumerable<(string Column, bool Descending)> columns, string? filegroup) =>
            "CONSTRAINT " + Bracket(name) + (primary ? " PRIMARY KEY " : " UNIQUE ") + (clustered ? "CLUSTERED" : "NONCLUSTERED") +
            " (" + string.Join(", ", columns.Select(c => Bracket(c.Column) + (c.Descending ? " DESC" : ""))) + ")" +
            (filegroup == null ? "" : " ON " + Bracket(filegroup));

        /// <summary>Actions as in sys.foreign_keys, e.g. NO_ACTION, CASCADE, SET_NULL.</summary>
        public static string ForeignKey(string name, IEnumerable<string> columns, string refSchema, string refTable, IEnumerable<string> refColumns,
            string? onDelete = null, string? onUpdate = null)
        {
            string Action(string verb, string? action) =>
                action == null || action == "NO_ACTION" ? "" : " ON " + verb + " " + action.Replace('_', ' ');
            return "CONSTRAINT " + Bracket(name) + " FOREIGN KEY " + List(columns) + " REFERENCES " + Bracket(refSchema) + "." + Bracket(refTable) +
                " " + List(refColumns) + Action("DELETE", onDelete) + Action("UPDATE", onUpdate);
        }

        public static string Check(string name, string definition) => "CONSTRAINT " + Bracket(name) + " CHECK " + definition;

        /// <summary>Scripts match apart from line endings and trailing whitespace.</summary>
        public static bool SameScript(string? a, string? b)
        {
            string Normal(string? s) => string.Join("\n", (s ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd())).TrimEnd();
            return Normal(a) == Normal(b);
        }

        public static string CreateTable(string schema, string name, IReadOnlyList<ScriptColumn> columns, string? filegroup,
            IEnumerable<string>? constraints = null, string newline = "\r\n")
        {
            if (columns == null || columns.Count == 0) throw new ArgumentException("A table needs at least one column.");
            string table = Bracket(schema) + "." + Bracket(name);
            var builder = new StringBuilder("CREATE TABLE ").Append(table).Append(newline).Append('(').Append(newline);
            for (int i = 0; i < columns.Count; i++)
            {
                var c = columns[i];
                builder.Append(Bracket(c.Name)).Append(' ');
                if (c.Computed != null) builder.Append("AS ").Append(c.Computed).Append(c.Persisted ? " PERSISTED" : "");
                else
                {
                    builder.Append(Bracket(c.Type)).Append(c.Size == null ? "" : " (" + c.Size + ")")
                        .Append(c.Collation == null ? "" : " COLLATE " + c.Collation)
                        .Append(c.Nullable ? " NULL" : " NOT NULL")
                        .Append(c.Identity == null ? "" : " IDENTITY(" + c.Identity + ")")
                        .Append(c.Default == null ? "" : (c.DefaultName == null ? "" : " CONSTRAINT " + Bracket(c.DefaultName)) + " DEFAULT " + c.Default);
                }
                builder.Append(i < columns.Count - 1 ? "," : "").Append(newline);
            }
            builder.Append(')').Append(filegroup == null ? "" : " ON " + Bracket(filegroup)).Append(newline).Append("GO").Append(newline);
            // ponytail: indexes, triggers, permissions and extended properties are left out.
            foreach (var constraint in constraints ?? Enumerable.Empty<string>())
                builder.Append("ALTER TABLE ").Append(table).Append(" ADD ").Append(constraint).Append(newline).Append("GO").Append(newline);
            return builder.ToString();
        }
    }
}

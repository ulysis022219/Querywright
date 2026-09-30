using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Querywright.Core
{
    /// <summary>
    /// Text form of loaded tables for the on-disk schema cache: names, types, nullability/defaults and foreign keys only.
    /// Never credentials, query text or row data. Read is forgiving: anything unreadable yields null.
    /// </summary>
    public static class SchemaDiskCache
    {
        private const string Header = "querywright-schema 1";

        public static void Write(TextWriter writer, IEnumerable<SchemaTable> tables)
        {
            writer.WriteLine(Header);
            foreach (var t in tables)
            {
                writer.WriteLine(Line("T", t.Schema, t.Name, t.IsView ? "1" : "0"));
                for (int i = 0; i < t.Columns.Count; i++)
                    writer.WriteLine(Line("C", t.Columns[i], t.ColumnTypes?[i], t.Generated != null && t.Generated[i] ? "1" : "0", t.ColumnNotes?[i]));
                foreach (var k in t.ForeignKeys)
                    writer.WriteLine(Line(new[] { "F", k.ReferencedSchema, k.ReferencedTable }.Concat(k.Columns.Zip(k.ReferencedColumns, (c, r) => new[] { c, r }).SelectMany(p => p)).ToArray()));
            }
        }

        public static SchemaTable[]? Read(TextReader reader)
        {
            try
            {
                if (reader.ReadLine() != Header) return null;
                var columns = new List<(string?, string?, string?, string?, bool, bool, string?)>();
                var keys = new List<(int, string?, string?, string?, string?, string?, string?)>();
                string? schema = null, table = null; bool view = false; int id = 0;
                for (string? line; (line = reader.ReadLine()) != null;)
                {
                    var f = line.Split('\t').Select(v => v.Length == 0 ? null : Uri.UnescapeDataString(v)).ToArray();
                    if (f[0] == "T") { schema = f[1]; table = f[2]; view = f[3] == "1"; }
                    else if (f[0] == "C") columns.Add((schema, table, f[1], f[2], f[3] == "1", view, f[4]));
                    else if (f[0] == "F") { id++; for (int i = 3; i + 1 < f.Length; i += 2) keys.Add((id, schema, table, f[i], f[1], f[2], f[i + 1])); }
                    else return null;
                }
                return CatalogAssembler.Tables(columns, keys).Tables;
            }
            catch (Exception error) when (error is IndexOutOfRangeException || error is UriFormatException || error is IOException) { return null; }
        }

        // ponytail: percent-escaping keeps tabs and newlines in names reversible; empty field means null.
        private static string Line(params string?[] fields) =>
            string.Join("\t", fields.Select(v => v == null ? "" : Uri.EscapeDataString(v)));
    }
}

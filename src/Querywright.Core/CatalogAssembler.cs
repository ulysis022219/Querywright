using System;
using System.Collections.Generic;
using System.Linq;

namespace Querywright.Core
{
    /// <summary>
    /// Turns raw catalog rows into schema objects. Pure and forgiving: NULL or blank names, duplicates, orphan foreign keys
    /// and odd objects are dropped one by one instead of failing the whole load. Names only; no connection, no logging.
    /// </summary>
    public static class CatalogAssembler
    {
        private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

        /// <returns>Tables sorted by schema then name, and how many objects were skipped as unusable.</returns>
        public static (SchemaTable[] Tables, int Skipped) Tables(
            IEnumerable<(string? Schema, string? Table, string? Column, string? Type, bool Generated, bool View)> columns,
            IEnumerable<(int Id, string? Schema, string? Table, string? Column, string? RefSchema, string? RefTable, string? RefColumn)> keys)
        {
            var foreignKeys = new Dictionary<(string, string), List<SchemaForeignKey>>();
            foreach (var group in (keys ?? Enumerable.Empty<(int, string?, string?, string?, string?, string?, string?)>()).GroupBy(k => k.Id))
            {
                var rows = group.ToList();
                var first = rows[0];
                if (string.IsNullOrWhiteSpace(first.Schema) || string.IsNullOrWhiteSpace(first.Table) ||
                    rows.Any(r => string.IsNullOrWhiteSpace(r.Column) || string.IsNullOrWhiteSpace(r.RefColumn))) continue;
                SchemaForeignKey key;
                try { key = new SchemaForeignKey(rows.Select(r => r.Column!).ToArray(), first.RefSchema ?? "", first.RefTable ?? "", rows.Select(r => r.RefColumn!).ToArray()); }
                catch (ArgumentException) { continue; }
                if (!foreignKeys.TryGetValue((first.Schema!, first.Table!), out var list)) foreignKeys[(first.Schema!, first.Table!)] = list = new List<SchemaForeignKey>();
                list.Add(key);
            }

            int skipped = 0;
            var tables = new List<SchemaTable>();
            var usable = (columns ?? Enumerable.Empty<(string?, string?, string?, string?, bool, bool)>())
                .Where(c => !string.IsNullOrWhiteSpace(c.Schema) && !string.IsNullOrWhiteSpace(c.Table));
            foreach (var group in usable.GroupBy(c => (c.Schema!, c.Table!)))
            {
                // A blank or repeated column name drops that column; the table stays.
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var cols = group.Where(c => !string.IsNullOrWhiteSpace(c.Column) && seen.Add(c.Column!)).ToList();
                if (cols.Count == 0) { skipped++; continue; }
                var names = cols.Select(c => c.Column!).ToArray();
                var fks = foreignKeys.TryGetValue(group.Key, out var candidates)
                    ? candidates.Where(k => k.Columns.All(c => names.Contains(c, Names))).ToArray() : Array.Empty<SchemaForeignKey>();
                try { tables.Add(new SchemaTable(group.Key.Item1, group.Key.Item2, names, cols.Select(c => c.Type).ToArray(), fks, cols.Select(c => c.Generated).ToArray(), cols[0].View)); }
                catch (ArgumentException) { skipped++; }
            }
            return (tables.OrderBy(t => t.Schema, Names).ThenBy(t => t.Name, Names).ToArray(), skipped);
        }

        /// <summary>Procedures sorted by schema then name; a procedure with no parameters row still appears.</summary>
        public static SchemaProcedure[] Procedures(
            IEnumerable<(string? Schema, string? Procedure, string? Name, string? Type, bool Output, bool Default)> parameters)
        {
            var result = new List<SchemaProcedure>();
            var usable = (parameters ?? Enumerable.Empty<(string?, string?, string?, string?, bool, bool)>())
                .Where(p => !string.IsNullOrWhiteSpace(p.Schema) && !string.IsNullOrWhiteSpace(p.Procedure));
            foreach (var group in usable.GroupBy(p => (p.Schema!, p.Procedure!)))
            {
                var list = new List<SchemaParameter>();
                foreach (var p in group)
                {
                    if (string.IsNullOrWhiteSpace(p.Name) || p.Name![0] != '@') continue;
                    list.Add(new SchemaParameter(p.Name, p.Type, p.Output, p.Default));
                }
                try { result.Add(new SchemaProcedure(group.Key.Item1, group.Key.Item2, list.ToArray())); }
                catch (ArgumentException) { }
            }
            return result.OrderBy(p => p.Schema, Names).ThenBy(p => p.Name, Names).ToArray();
        }
    }
}

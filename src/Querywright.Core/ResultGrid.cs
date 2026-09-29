using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Querywright.Core
{
    /// <summary>
    /// Pure transforms over results-grid cells (display text; null = SQL NULL). Nothing here runs SQL or logs data.
    /// </summary>
    public static class ResultGrid
    {
        private static readonly Regex TypeShape = new Regex(@"^\s*([A-Za-z][A-Za-z0-9_ ]*?)\s*(\(\s*(max|\d+)\s*(,\s*\d+\s*)?\))?\s*$", RegexOptions.CultureInvariant);
        private static readonly Regex SqlDateTime = new Regex(@"^(\d{4}-\d{2}-\d{2}) (\d{2}:\d{2}:\d{2}(\.\d+)?)$", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> NumericTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "bit", "tinyint", "smallint", "int", "bigint", "decimal", "numeric", "float", "real", "money", "smallmoney" };
        private static readonly HashSet<string> BinaryTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "binary", "varbinary", "image", "timestamp", "rowversion", "geography", "geometry", "hierarchyid" };

        /// <summary>A plain decimal number (no leading zeros, so codes like 007 stay strings).</summary>
        public static bool IsNumber(string? value) =>
            value != null && Regex.IsMatch(value, @"^-?(0|[1-9]\d*)(\.\d+)?([eE][-+]?\d+)?$", RegexOptions.CultureInvariant);

        public static string Quote(string value) => "N'" + value.Replace("'", "''") + "'";

        /// <summary>SQL Prompt's "Copy as IN clause": distinct values, numbers bare when every value is numeric, NULLs dropped.</summary>
        public static string InClause(IEnumerable<string?> values)
        {
            // ponytail: NULL never matches IN, so it is dropped rather than emitted.
            var distinct = values.Where(v => v != null).Select(v => v!).Distinct(StringComparer.Ordinal).ToList();
            if (distinct.Count == 0) throw new InvalidOperationException("The selection holds no non-NULL values.");
            bool numeric = distinct.All(IsNumber);
            return "(" + string.Join(", ", distinct.Select(v => numeric ? v : Quote(v))) + ")";
        }

        /// <summary>Unique, non-empty column names; SSMS shows "(No column name)" for unnamed expressions.</summary>
        public static IReadOnlyList<string> ColumnNames(IReadOnlyList<string?> headers)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            for (int i = 0; i < headers.Count; i++)
            {
                string name = headers[i]?.Trim() ?? "";
                if (name.Length == 0 || name == "(No column name)") name = "Column" + (i + 1);
                if (name.Length > 120) name = name.Substring(0, 120);
                string unique = name;
                for (int n = 2; !used.Add(unique); n++) unique = name + "_" + n;
                names.Add(unique);
            }
            return names;
        }

        private static string Bracket(string name) => "[" + name.Replace("]", "]]") + "]";

        private static string? BaseType(string? type)
        {
            var match = type == null ? null : TypeShape.Match(type);
            return match != null && match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
        }

        private static string ColumnType(string? reported, IEnumerable<string?> values)
        {
            string? baseType = BaseType(reported);
            if (baseType == "timestamp" || baseType == "rowversion") return "binary(8)"; // not insertable as-is
            if (baseType == "text") return "varchar(max)";
            if (baseType == "ntext") return "nvarchar(max)";
            if (baseType == "image") return "varbinary(max)";
            if (baseType != null) return reported!.Trim().ToLowerInvariant();
            var present = values.Where(v => v != null).ToList();
            if (present.Count > 0 && present.All(IsNumber))
            {
                if (present.All(v => long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long n) && n >= int.MinValue && n <= int.MaxValue)) return "int";
                if (present.All(v => long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))) return "bigint";
                if (present.Any(v => v!.IndexOfAny(new[] { 'e', 'E' }) >= 0)) return "float";
                return "decimal(38, 10)";
            }
            int longest = present.Count == 0 ? 1 : present.Max(v => v!.Length);
            return longest <= 4000 ? "nvarchar(" + Math.Max(longest, 1) + ")" : "nvarchar(max)";
        }

        private static string Literal(string? value, string? baseType)
        {
            if (value == null) return "NULL";
            if (baseType == null ? IsNumber(value) : NumericTypes.Contains(baseType) && IsNumber(value)) return value;
            if (baseType != null && BinaryTypes.Contains(baseType) && Regex.IsMatch(value, "^0x[0-9A-Fa-f]*$")) return value;
            // Grid shows datetime as "yyyy-MM-dd HH:mm:ss.fff", which some DATEFORMAT settings misread; the T form is language-neutral.
            if ((baseType == "datetime" || baseType == "smalldatetime") && SqlDateTime.IsMatch(value))
                return "'" + SqlDateTime.Replace(value, "$1T$2") + "'";
            return Quote(value);
        }

        /// <summary>SQL Prompt's "Script as INSERT": DROP/CREATE TABLE #Results plus INSERT ... VALUES in batches of 1000 (SQL Server's row-constructor limit).</summary>
        public static string InsertScript(IReadOnlyList<string?> headers, IReadOnlyList<string?>? types, IReadOnlyList<string?[]> rows, string newline = "\r\n")
        {
            if (headers.Count == 0) throw new InvalidOperationException("The results have no columns.");
            if (rows.Any(r => r.Length != headers.Count)) throw new ArgumentException("Every row needs one value per column.");
            var names = ColumnNames(headers);
            var reported = Enumerable.Range(0, headers.Count).Select(c => types != null && c < types.Count && BaseType(types[c]) != null ? types[c] : null).ToList();
            var builder = new StringBuilder();
            builder.Append("-- Querywright: results scripted as INSERT. Values are the grid's display text (floats rounded, long text may be truncated).").Append(newline);
            builder.Append("DROP TABLE IF EXISTS #Results;").Append(newline);
            builder.Append("CREATE TABLE #Results").Append(newline).Append("(").Append(newline);
            for (int c = 0; c < names.Count; c++)
                builder.Append("    ").Append(Bracket(names[c])).Append(' ').Append(ColumnType(reported[c], rows.Select(r => r[c])))
                    .Append(" NULL").Append(c < names.Count - 1 ? "," : "").Append(newline);
            builder.Append(");").Append(newline);
            var kinds = Enumerable.Range(0, names.Count).Select(c => ColumnKind(BaseType(reported[c]), rows, c)).ToList();
            string columnList = string.Join(", ", names.Select(Bracket));
            for (int start = 0; start < rows.Count; start += 1000)
            {
                builder.Append(newline).Append(newline).Append("INSERT INTO #Results (").Append(columnList).Append(")").Append(newline).Append("VALUES");
                int end = Math.Min(start + 1000, rows.Count);
                for (int r = start; r < end; r++)
                {
                    builder.Append(newline).Append("    (");
                    for (int c = 0; c < names.Count; c++)
                        builder.Append(c > 0 ? ", " : "").Append(Literal(rows[r][c], kinds[c]));
                    builder.Append(r < end - 1 ? ")," : ");");
                }
            }
            builder.Append(newline).Append(newline).Append("SELECT * FROM #Results;").Append(newline);
            builder.Append(newline).Append("DROP TABLE #Results;").Append(newline);
            return builder.ToString();
        }

        // Without a reported type, a column is numeric only when every value is; a mixed column is all strings.
        private static string? ColumnKind(string? baseType, IReadOnlyList<string?[]> rows, int column) =>
            baseType ?? (rows.All(r => r[column] == null || IsNumber(r[column])) ? null : "nvarchar");

        /// <summary>Delimited text (CSV or tab) with RFC 4180 quoting and a spreadsheet formula-injection guard. NULL is an empty field.</summary>
        public static string Delimited(IReadOnlyList<string?> headers, IReadOnlyList<string?[]> rows, char separator, string newline = "\r\n")
        {
            var builder = new StringBuilder();
            void Line(IReadOnlyList<string?> cells)
            {
                for (int c = 0; c < cells.Count; c++)
                {
                    if (c > 0) builder.Append(separator);
                    string value = cells[c] ?? "";
                    // Cells a spreadsheet would evaluate as a formula get a leading apostrophe; plain numbers such as -5 are left alone.
                    if (value.Length > 0 && "=+-@\t\r".IndexOf(value[0]) >= 0 && !IsNumber(value)) value = "'" + value;
                    if (value.IndexOf(separator) >= 0 || value.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0)
                        value = "\"" + value.Replace("\"", "\"\"") + "\"";
                    builder.Append(value);
                }
                builder.Append(newline);
            }
            Line(ColumnNames(headers));
            foreach (var row in rows) Line(row);
            return builder.ToString();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using static Querywright.Core.ObjectScript;

namespace Querywright.Core
{
    /// <summary>
    /// Pure transforms over results-grid cells (display text; null = SQL NULL). Nothing here runs SQL or logs data.
    /// </summary>
    public static class ResultGrid
    {
        private static readonly Regex TypeShape = new Regex(@"^\s*([A-Za-z][A-Za-z0-9_ ]*?)\s*(\(\s*(max|\d+)\s*(,\s*\d+\s*)?\))?\s*\z", RegexOptions.CultureInvariant);
        private static readonly Regex SqlDateTime = new Regex(@"^(\d{4}-\d{2}-\d{2}) (\d{2}:\d{2}:\d{2}(\.\d+)?)\z", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> NumericTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "bit", "tinyint", "smallint", "int", "bigint", "decimal", "numeric", "float", "real", "money", "smallmoney" };
        private static readonly HashSet<string> BinaryTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "binary", "varbinary", "image", "timestamp", "rowversion", "geography", "geometry", "hierarchyid" };

        /// <summary>
        /// "Schema.Table_yyyyMMdd_HHmmss" from the first table in a FROM of <paramref name="sql"/> (the query
        /// that filled the grid), else "Results_yyyyMMdd_HHmmss". Invalid file-name characters become '_'.
        /// </summary>
        public static string FileName(string? sql, DateTime now)
        {
            string name = "Results";
            var fragment = new Microsoft.SqlServer.TransactSql.ScriptDom.TSql170Parser(true).Parse(new StringReader(sql ?? ""), out _);
            var finder = new FirstTable();
            fragment?.Accept(finder);
            if (finder.Name != null)
                name = (finder.Name.SchemaIdentifier?.Value ?? "dbo") + "." + finder.Name.BaseIdentifier.Value;
            foreach (char bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
            return name + "_" + now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        }

        private sealed class FirstTable : Microsoft.SqlServer.TransactSql.ScriptDom.TSqlFragmentVisitor
        {
            internal Microsoft.SqlServer.TransactSql.ScriptDom.SchemaObjectName? Name;
            public override void Visit(Microsoft.SqlServer.TransactSql.ScriptDom.NamedTableReference node)
            {
                // ponytail: first table in visit order, #temp skipped; a CTE body is visited before its outer query.
                if (Name == null && !node.SchemaObject.BaseIdentifier.Value.StartsWith("#", StringComparison.Ordinal)) Name = node.SchemaObject;
            }
        }

        /// <summary>A plain decimal number (no leading zeros, so codes like 007 stay strings).</summary>
        public static bool IsNumber(string? value) =>
            value != null && Regex.IsMatch(value, @"^-?(0|[1-9]\d*)(\.\d+)?([eE][-+]?\d+)?\z", RegexOptions.CultureInvariant);

        /// <summary>Status bar text for selected cells: count of non-NULL cells, and sum/avg/min/max over the numeric ones.</summary>
        public static string SelectionSummary(IEnumerable<string?> values)
        {
            int count = 0, numbers = 0;
            decimal sum = 0, min = 0, max = 0;
            bool overflow = false;
            foreach (var v in values)
            {
                if (v == null) continue;
                count++;
                if (!decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) continue;
                min = numbers == 0 ? d : Math.Min(min, d);
                max = numbers == 0 ? d : Math.Max(max, d);
                numbers++;
                try { if (!overflow) sum += d; } catch (OverflowException) { overflow = true; }
            }
            if (numbers == 0) return "Count: " + count;
            string F(decimal d) => d.ToString("0.##########", CultureInfo.InvariantCulture);
            return "Count: " + count + "    Sum: " + (overflow ? "overflow" : F(sum)) + "    Avg: " + (overflow ? "-" : F(Math.Round(sum / numbers, 10))) +
                "    Min: " + F(min) + "    Max: " + F(max);
        }

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
            if (baseType != null && BinaryTypes.Contains(baseType) && Regex.IsMatch(value, @"^0x[0-9A-Fa-f]*\z")) return value;
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

        /// <summary>"[schema].[table]" of the first table the query reads, else null.</summary>
        public static string? SourceTable(string? sql)
        {
            var fragment = new Microsoft.SqlServer.TransactSql.ScriptDom.TSql170Parser(true).Parse(new StringReader(sql ?? ""), out _);
            var finder = new FirstTable();
            fragment?.Accept(finder);
            return finder.Name == null ? null : Bracket(finder.Name.SchemaIdentifier?.Value ?? "dbo") + "." + Bracket(finder.Name.BaseIdentifier.Value);
        }

        private static void CheckShape(IReadOnlyList<string?> headers, IReadOnlyList<string?[]> rows)
        {
            if (headers.Count == 0) throw new InvalidOperationException("The results have no columns.");
            if (rows.Any(r => r.Length != headers.Count)) throw new ArgumentException("Every row needs one value per column.");
        }

        private static IReadOnlyList<string?> Reported(IReadOnlyList<string?>? types, int count) =>
            Enumerable.Range(0, count).Select(c => types != null && c < types.Count && BaseType(types[c]) != null ? types[c] : null).ToList();

        /// <summary>CREATE TABLE with the grid's column types; NOT NULL where the rows hold no NULL.</summary>
        public static string CreateTableScript(IReadOnlyList<string?> headers, IReadOnlyList<string?>? types, IReadOnlyList<string?[]> rows, string table = "#Results", string newline = "\r\n")
        {
            CheckShape(headers, rows);
            var names = ColumnNames(headers);
            var reported = Reported(types, names.Count);
            var builder = new StringBuilder("-- Querywright: table shaped like the results. NOT NULL where the rows had no NULL. Review, then execute. Nothing has been run.").Append(newline);
            builder.Append("CREATE TABLE ").Append(table).Append(newline).Append("(").Append(newline);
            for (int c = 0; c < names.Count; c++)
                builder.Append("    ").Append(Bracket(names[c])).Append(' ').Append(ColumnType(reported[c], rows.Select(r => r[c])))
                    .Append(rows.Count > 0 && rows.All(r => r[c] != null) ? " NOT NULL" : " NULL").Append(c < names.Count - 1 ? "," : "").Append(newline);
            return builder.Append(");").Append(newline).ToString();
        }

        /// <summary>One UPDATE per row, keyed on the first column; rowversion columns are skipped.</summary>
        public static string UpdateScript(IReadOnlyList<string?> headers, IReadOnlyList<string?>? types, IReadOnlyList<string?[]> rows, string table, string newline = "\r\n")
        {
            CheckShape(headers, rows);
            var names = ColumnNames(headers);
            var reported = Reported(types, names.Count);
            var kinds = Enumerable.Range(0, names.Count).Select(c => ColumnKind(BaseType(reported[c]), rows, c)).ToList();
            var set = Enumerable.Range(1, names.Count - 1).Where(c => !IsRowVersion(kinds[c])).ToList();
            if (set.Count == 0) throw new InvalidOperationException("Select a key column followed by at least one column to update.");
            var builder = new StringBuilder("-- Querywright: results scripted as UPDATE, keyed on the first column ").Append(Bracket(names[0]))
                .Append(". Review, then execute. Nothing has been run.").Append(newline);
            foreach (var row in rows)
            {
                builder.Append("UPDATE ").Append(table).Append(" SET ")
                    .Append(string.Join(", ", set.Select(c => Bracket(names[c]) + " = " + Literal(row[c], kinds[c]))))
                    .Append(" WHERE ").Append(Bracket(names[0])).Append(row[0] == null ? " IS NULL" : " = " + Literal(row[0], kinds[0])).Append(';').Append(newline);
            }
            return builder.ToString();
        }

        /// <summary>MERGE from a VALUES list, keyed on the first column: update matches, insert the rest. Never deletes.</summary>
        public static string MergeScript(IReadOnlyList<string?> headers, IReadOnlyList<string?>? types, IReadOnlyList<string?[]> rows, string table, string newline = "\r\n")
        {
            CheckShape(headers, rows);
            if (rows.Count == 0) throw new InvalidOperationException("The results grid has no rows.");
            var names = ColumnNames(headers);
            var reported = Reported(types, names.Count);
            var kinds = Enumerable.Range(0, names.Count).Select(c => ColumnKind(BaseType(reported[c]), rows, c)).ToList();
            var used = Enumerable.Range(0, names.Count).Where(c => c == 0 || !IsRowVersion(kinds[c])).ToList();
            string key = Bracket(names[0]);
            var builder = new StringBuilder("-- Querywright: results scripted as MERGE, keyed on the first column ").Append(key)
                .Append(". Matching rows are updated, others inserted, nothing deleted. Review, then execute. Nothing has been run.").Append(newline);
            builder.Append("MERGE INTO ").Append(table).Append(" AS target").Append(newline).Append("USING (VALUES");
            for (int r = 0; r < rows.Count; r++)
                builder.Append(newline).Append("    (").Append(string.Join(", ", used.Select(c => Literal(rows[r][c], kinds[c])))).Append(r < rows.Count - 1 ? ")," : ")");
            builder.Append(newline).Append(") AS source (").Append(string.Join(", ", used.Select(c => Bracket(names[c])))).Append(")").Append(newline);
            builder.Append("ON target.").Append(key).Append(" = source.").Append(key).Append(newline);
            if (used.Count > 1)
                builder.Append("WHEN MATCHED THEN UPDATE SET ").Append(string.Join(", ", used.Skip(1).Select(c => "target." + Bracket(names[c]) + " = source." + Bracket(names[c])))).Append(newline);
            builder.Append("WHEN NOT MATCHED BY TARGET THEN INSERT (").Append(string.Join(", ", used.Select(c => Bracket(names[c]))))
                .Append(") VALUES (").Append(string.Join(", ", used.Select(c => "source." + Bracket(names[c])))).Append(");").Append(newline);
            return builder.ToString();
        }

        private static bool IsRowVersion(string? kind) => kind == "timestamp" || kind == "rowversion";

        /// <summary>GitHub-flavored Markdown table. '|' is escaped, line breaks become &lt;br&gt;, NULL is shown as NULL.</summary>
        public static string Markdown(IReadOnlyList<string?> headers, IReadOnlyList<string?[]> rows, string newline = "\r\n")
        {
            CheckShape(headers, rows);
            string Cell(string? value) => value == null ? "NULL" : value.Replace("\\", "\\\\").Replace("|", "\\|").Replace("\r\n", "<br>").Replace("\n", "<br>").Replace("\r", "<br>");
            var builder = new StringBuilder();
            builder.Append("| ").Append(string.Join(" | ", ColumnNames(headers).Select(Cell))).Append(" |").Append(newline);
            builder.Append('|').Append(string.Join("|", headers.Select(_ => " --- "))).Append('|').Append(newline);
            foreach (var row in rows) builder.Append("| ").Append(string.Join(" | ", row.Select(Cell))).Append(" |").Append(newline);
            return builder.ToString();
        }

        /// <summary>JSON array of objects. Numeric columns are JSON numbers, NULL is null, everything else a string.</summary>
        public static string Json(IReadOnlyList<string?> headers, IReadOnlyList<string?>? types, IReadOnlyList<string?[]> rows, string newline = "\r\n")
        {
            CheckShape(headers, rows);
            var names = ColumnNames(headers);
            var reported = Reported(types, names.Count);
            var numeric = Enumerable.Range(0, names.Count).Select(c => ColumnKind(BaseType(reported[c]), rows, c) is var k && (k == null || NumericTypes.Contains(k))).ToList();
            string Value(string? value, int c) => value == null ? "null" : numeric[c] && IsNumber(value) ? value : JsonString(value);
            var builder = new StringBuilder("[");
            for (int r = 0; r < rows.Count; r++)
                builder.Append(newline).Append("  {").Append(string.Join(", ", names.Select((n, c) => JsonString(n) + ": " + Value(rows[r][c], c)))).Append(r < rows.Count - 1 ? "}," : "}");
            return builder.Append(rows.Count > 0 ? newline : "").Append(']').Append(newline).ToString();
        }

        private static string JsonString(string value)
        {
            var builder = new StringBuilder("\"");
            foreach (char ch in value)
            {
                if (ch == '"') builder.Append("\\\"");
                else if (ch == '\\') builder.Append("\\\\");
                else if (ch == '\n') builder.Append("\\n");
                else if (ch == '\r') builder.Append("\\r");
                else if (ch == '\t') builder.Append("\\t");
                else if (ch < ' ') builder.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                else builder.Append(ch);
            }
            return builder.Append('"').ToString();
        }

        // Without a reported type, a column is numeric only when every value is; a mixed column is all strings.
        private static string? ColumnKind(string? baseType, IReadOnlyList<string?[]> rows, int column) =>
            baseType ?? (rows.All(r => r[column] == null || IsNumber(r[column])) ? null : "nvarchar");

        /// <summary>Delimited text (CSV or tab) with RFC 4180 quoting and a spreadsheet formula-injection guard. NULL is an empty field.</summary>
        public static string Delimited(IReadOnlyList<string?> headers, IReadOnlyList<string?[]> rows, char separator, string newline = "\r\n", bool includeHeaders = true)
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
                    if (value.IndexOf(separator) >= 0 || value.IndexOfAny(CsvSpecial) >= 0)
                        value = "\"" + value.Replace("\"", "\"\"") + "\"";
                    builder.Append(value);
                }
                builder.Append(newline);
            }
            if (includeHeaders) Line(ColumnNames(headers));
            foreach (var row in rows) Line(row);
            return builder.ToString();
        }

        private static readonly char[] CsvSpecial = { '"', '\r', '\n' };
        private static readonly Regex PlainDecimal = new Regex(@"^-?(0|[1-9]\d*)(\.(\d+))?\z", RegexOptions.CultureInvariant);
        private static readonly Regex XmlInvalid = new Regex(@"[\x00-\x08\x0B\x0C\x0E-\x1F\uFFFE\uFFFF]", RegexOptions.CultureInvariant);

        private static string Xml(string value) => System.Security.SecurityElement.Escape(XmlInvalid.Replace(value, ""));

        private static string CellRef(int column, int row)
        {
            string letters = "";
            for (int c = column + 1; c > 0; c = (c - 1) / 26) letters = (char)('A' + (c - 1) % 26) + letters;
            return letters + row;
        }

        /// <summary>
        /// Minimal .xlsx with the grid's display text kept 1:1: every cell is Text-formatted, so Excel never re-parses
        /// dates, codes like 007 or long numbers. Only plain decimals in numeric columns become numbers, formatted with
        /// the grid's own decimal places. Strings are never evaluated, so there is no formula risk. NULL is an empty cell.
        /// </summary>
        public static void Xlsx(Stream output, IReadOnlyList<string?> headers, IReadOnlyList<string?>? types, IReadOnlyList<string?[]> rows)
        {
            if (headers.Count == 0) throw new InvalidOperationException("The results have no columns.");
            if (rows.Count >= 1048576) throw new InvalidOperationException("Excel holds at most 1,048,575 rows plus the header.");
            if (rows.Any(r => r.Length != headers.Count)) throw new ArgumentException("Every row needs one value per column.");
            var numeric = Enumerable.Range(0, headers.Count).Select(c => types != null && c < types.Count && BaseType(types[c]) is string t && NumericTypes.Contains(t)).ToList();
            // Style 0 default, 1 bold header, 2 text; 3+ numbers with 0..n decimals (numFmt 164+).
            var decimals = new List<int>();
            var names = ColumnNames(headers);
            var widths = names.Select(n => n.Length).ToArray();
            var sheet = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
            var data = new StringBuilder("<sheetData>");
            void Text(int c, int r, string value, int style) =>
                // ponytail: Excel's cell limit is 32,767 characters; longer grid text is cut there.
                data.Append("<c r=\"").Append(CellRef(c, r)).Append("\" s=\"").Append(style).Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                    .Append(Xml(value.Length > 32767 ? value.Substring(0, 32767) : value)).Append("</t></is></c>");
            data.Append("<row r=\"1\">");
            for (int c = 0; c < names.Count; c++) Text(c, 1, names[c], 1);
            data.Append("</row>");
            for (int r = 0; r < rows.Count; r++)
            {
                data.Append("<row r=\"").Append(r + 2).Append("\">");
                for (int c = 0; c < names.Count; c++)
                {
                    string? value = rows[r][c];
                    if (value == null) continue;
                    widths[c] = Math.Max(widths[c], Math.Min(value.Length, 100));
                    var match = numeric[c] ? PlainDecimal.Match(value) : Match.Empty;
                    // Doubles hold 15 significant digits; longer values stay text so no digit changes.
                    if (match.Success && value.Count(char.IsDigit) <= 15)
                    {
                        int places = match.Groups[3].Length;
                        int index = decimals.IndexOf(places);
                        if (index < 0) { decimals.Add(places); index = decimals.Count - 1; }
                        data.Append("<c r=\"").Append(CellRef(c, r + 2)).Append("\" s=\"").Append(3 + index).Append("\"><v>").Append(value).Append("</v></c>");
                    }
                    else Text(c, r + 2, value, 2);
                }
                data.Append("</row>");
            }
            data.Append("</sheetData>");
            sheet.Append("<cols>");
            for (int c = 0; c < names.Count; c++)
                sheet.Append("<col min=\"").Append(c + 1).Append("\" max=\"").Append(c + 1).Append("\" width=\"").Append(Math.Min(widths[c] + 2, 60)).Append("\" customWidth=\"1\"/>");
            sheet.Append("</cols>").Append(data).Append("</worksheet>");

            var styles = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            if (decimals.Count > 0)
            {
                styles.Append("<numFmts count=\"").Append(decimals.Count).Append("\">");
                for (int i = 0; i < decimals.Count; i++)
                    styles.Append("<numFmt numFmtId=\"").Append(164 + i).Append("\" formatCode=\"").Append(decimals[i] == 0 ? "0" : "0." + new string('0', decimals[i])).Append("\"/>");
                styles.Append("</numFmts>");
            }
            styles.Append("<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>")
                .Append("<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>")
                .Append("<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>")
                .Append("<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>")
                .Append("<cellXfs count=\"").Append(3 + decimals.Count).Append("\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>")
                .Append("<xf numFmtId=\"49\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyNumberFormat=\"1\"/>")
                .Append("<xf numFmtId=\"49\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>");
            for (int i = 0; i < decimals.Count; i++)
                styles.Append("<xf numFmtId=\"").Append(164 + i).Append("\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>");
            styles.Append("</cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");

            const string head = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";
            const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            var parts = new[]
            {
                ("[Content_Types].xml", head + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>"),
                ("_rels/.rels", head + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"" + rel + "/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"),
                ("xl/workbook.xml", head + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"" + rel + "\"><sheets><sheet name=\"Results\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
                ("xl/_rels/workbook.xml.rels", head + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"" + rel + "/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"" + rel + "/styles\" Target=\"styles.xml\"/></Relationships>"),
                ("xl/styles.xml", styles.ToString()),
                ("xl/worksheets/sheet1.xml", sheet.ToString()),
            };
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (name, content) in parts)
                    using (var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Fastest).Open(), new UTF8Encoding(false)))
                        writer.Write(content);
        }
    }
}

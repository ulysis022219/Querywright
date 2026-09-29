using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Querywright.Core
{
    public sealed class SchemaParameter
    {
        public string Name { get; }
        public string? Type { get; }
        public bool IsOutput { get; }
        public bool HasDefault { get; }
        public SchemaParameter(string name, string? type, bool isOutput, bool hasDefault)
        {
            if (string.IsNullOrWhiteSpace(name) || name[0] != '@') throw new ArgumentException("Parameter names start with @.");
            Name = name; Type = type; IsOutput = isOutput; HasDefault = hasDefault;
        }
    }

    public sealed class SchemaProcedure
    {
        public string Schema { get; }
        public string Name { get; }
        public IReadOnlyList<SchemaParameter> Parameters { get; }
        public SchemaProcedure(string schema, string name, SchemaParameter[] parameters)
        {
            if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(name) || parameters == null || parameters.Any(p => p == null))
                throw new ArgumentException("Procedures need a schema, a name and parameters.");
            Schema = schema; Name = name; Parameters = Array.AsReadOnly((SchemaParameter[])parameters.Clone());
        }
    }

    /// <summary>INSERT/EXEC fill and quick info; everything here only produces text.</summary>
    public static class SqlAssist
    {
        private const int MaxInput = 1_000_000, MaxInfoColumns = 50;
        private static readonly HashSet<string> InsertFollowers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "(", "VALUES", "SELECT", "DEFAULT", "OUTPUT", "EXEC", "EXECUTE", "WITH" };

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#' || c == '$';
        private static bool Trivia(TSqlParserToken t) => t.TokenType == TSqlTokenType.WhiteSpace || t.TokenType == TSqlTokenType.SingleLineComment ||
            t.TokenType == TSqlTokenType.MultilineComment || t.TokenType == TSqlTokenType.EndOfFile;
        private static bool IsName(TSqlParserToken t) => t.TokenType == TSqlTokenType.Identifier || t.TokenType == TSqlTokenType.QuotedIdentifier;
        private static bool Is(TSqlParserToken t, string word) => t.TokenType != TSqlTokenType.QuotedIdentifier && string.Equals(t.Text, word, StringComparison.OrdinalIgnoreCase);
        private static string Unquote(TSqlParserToken t) => t.TokenType != TSqlTokenType.QuotedIdentifier || t.Text.Length < 2 ? t.Text
            : t.Text.Substring(1, t.Text.Length - 2).Replace(t.Text[0] == '"' ? "\"\"" : "]]", t.Text[0] == '"' ? "\"" : "]");
        private static string OneLine(string text) => text.Replace("\r", " ").Replace("\n", " ");

        /// <summary>
        /// Tab after <c>INSERT [INTO] table</c> or <c>EXEC [@rc =] procedure</c>: the column list with a VALUES row, or named arguments.
        /// Returns null when the caret is not directly after such a name or the statement already continues.
        /// </summary>
        public static TextEdit? FillStatement(string sql, int position, IReadOnlyList<SchemaTable>? tables, IReadOnlyList<SchemaProcedure>? procedures,
            string defaultSchema = "dbo", DateTimeOffset? now = null)
        {
            if (sql == null) throw new ArgumentNullException(nameof(sql));
            if (position < 0 || position > sql.Length) throw new ArgumentOutOfRangeException(nameof(position));
            if (sql.Length > MaxInput || position == 0 || position < sql.Length && IsWordChar(sql[position])) return null;
            // EXEC p<space> or a Tab already typed after the procedure name: fill from the name and replace the trailing blanks.
            int caret = position;
            while (position > 0 && (sql[position - 1] == ' ' || sql[position - 1] == '\t')) position--;
            if (position == 0) return null;
            var parser = new TSql170Parser(true);
            var before = parser.GetTokenStream(new StringReader(sql.Substring(0, position)), out var errors).Where(t => t.TokenType != TSqlTokenType.EndOfFile).ToList();
            if (errors.Count > 0 || before.Count == 0 || !IsName(before[before.Count - 1])) return null;
            var code = before.Where(t => !Trivia(t)).ToList();
            int i = code.Count - 1;
            var parts = new List<string> { Unquote(code[i]) };
            while (i >= 2 && code[i - 1].TokenType == TSqlTokenType.Dot && IsName(code[i - 2]) && parts.Count < 3) { parts.Insert(0, Unquote(code[i - 2])); i -= 2; }
            if (i >= 1 && code[i - 1].TokenType == TSqlTokenType.Dot) return null;
            var keyword = i >= 1 ? code[i - 1] : null;
            if (keyword != null && Is(keyword, "INTO")) keyword = i >= 2 && Is(code[i - 2], "INSERT") ? code[i - 2] : null;
            else if (i >= 3 && code[i - 1].TokenType == TSqlTokenType.EqualsSign && code[i - 2].TokenType == TSqlTokenType.Variable) keyword = code[i - 3];
            if (keyword == null) return null;
            bool insert = Is(keyword, "INSERT");
            if (insert && caret != position) return null; // ponytail: blanks after an INSERT target stay an ordinary Tab.
            if (!insert && !Is(keyword, "EXEC") && !Is(keyword, "EXECUTE")) return null;

            var after = parser.GetTokenStream(new StringReader(sql.Substring(position)), out _).FirstOrDefault(t => !Trivia(t));
            if (after != null && after.TokenType != TSqlTokenType.Semicolon && after.TokenType != TSqlTokenType.Go)
            {
                bool laterLine = sql.IndexOf('\n', position, after.Offset) >= 0;
                if (insert && (after.TokenType == TSqlTokenType.LeftParenthesis || InsertFollowers.Contains(after.Text ?? ""))) return null;
                if (!insert && (after.TokenType == TSqlTokenType.Variable || after.TokenType == TSqlTokenType.Integer ||
                    after.TokenType == TSqlTokenType.AsciiStringLiteral || after.TokenType == TSqlTokenType.UnicodeStringLiteral)) return null;
                if (!laterLine) return null;
            }

            string newline = sql.Contains("\r\n") ? "\r\n" : "\n";
            int lineStart = sql.LastIndexOf('\n', position - 1) + 1;
            string indent = new string(sql.Substring(lineStart, position - lineStart).TakeWhile(c => c == ' ' || c == '\t').ToArray());
            if (insert)
            {
                var table = Find(tables ?? Array.Empty<SchemaTable>(), t => t.Schema, t => t.Name, parts, defaultSchema);
                if (table == null) return null;
                var columns = Enumerable.Range(0, table.Columns.Count).Where(c => table.Generated == null || !table.Generated[c])
                    .Select(c => (Name: table.Columns[c], Type: table.ColumnTypes?[c])).ToList();
                if (columns.Count == 0) return new TextEdit(position, caret - position, " DEFAULT VALUES");
                string inner = indent + "    ";
                var text = new StringBuilder();
                text.Append(newline).Append(indent).Append('(').Append(newline);
                text.Append(string.Join("," + newline, columns.Select(c => inner + SqlCompletion.QuoteIfNeeded(c.Name))));
                text.Append(newline).Append(indent).Append(')').Append(newline).Append(indent).Append("VALUES").Append(newline).Append(indent).Append('(').Append(newline);
                text.Append(Aligned(columns.Select(c => (Placeholder(c.Type), OneLine(c.Name) + (c.Type == null ? "" : " - " + c.Type))).ToList(), inner, newline));
                text.Append(newline).Append(indent).Append(')');
                return new TextEdit(position, caret - position, text.ToString());
            }
            var procedure = Find(procedures ?? Array.Empty<SchemaProcedure>(), p => p.Schema, p => p.Name, parts, defaultSchema);
            if (procedure == null || procedure.Parameters.Count == 0) return null;
            // Continuation lines line up under the first argument; tabs in the line prefix are kept so the column matches.
            string hang = new string(sql.Substring(lineStart, position - lineStart).Select(c => c == '\t' ? '\t' : ' ').ToArray()) + " ";
            var arguments = procedure.Parameters.Select(p => (p.Name + " = " + (p.IsOutput ? p.Name + " OUTPUT" : p.HasDefault ? "DEFAULT" : ExecPlaceholder(p.Type, now ?? DateTimeOffset.Now)),
                p.Type ?? "")).ToList();
            string lines = Aligned(arguments, hang, newline);
            return new TextEdit(position, caret - position, " " + lines.Substring(hang.Length));
        }

        // value, -- comment lines with the comments aligned; the last value has no comma.
        private static string Aligned(IReadOnlyList<(string Value, string Comment)> rows, string indent, string newline)
        {
            int width = rows.Max(r => r.Value.Length) + 1;
            return string.Join(newline, rows.Select((r, n) =>
            {
                string value = (r.Value + (n < rows.Count - 1 ? "," : "")).PadRight(width);
                return (indent + value + (r.Comment.Length == 0 ? "" : " -- " + r.Comment)).TrimEnd();
            }));
        }

        // EXEC arguments must be constants or variables, so dates are the current time as literals (SQL Prompt style), not GETDATE().
        private static string ExecPlaceholder(string? type, DateTimeOffset now)
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            switch ((type ?? "").Split('(')[0].Trim().ToLowerInvariant())
            {
                case "date": return now.ToString("\\'yyyy-MM-dd\\'", c);
                case "time": return now.ToString("\\'HH:mm:ss\\'", c);
                case "datetime": case "datetime2": case "smalldatetime": return now.ToString("\\'yyyy-MM-dd HH:mm:ss\\'", c);
                case "datetimeoffset": return now.ToString("\\'yyyy-MM-dd HH:mm:ss zzz\\'", c);
                case "uniqueidentifier": return "'" + Guid.NewGuid().ToString().ToUpperInvariant() + "'";
                default: return Placeholder(type);
            }
        }

        internal static string Placeholder(string? type)
        {
            string name = (type ?? "").Split('(')[0].Trim().ToLowerInvariant();
            switch (name)
            {
                case "char": case "varchar": case "text": return "''";
                case "nchar": case "nvarchar": case "ntext": case "sysname": case "xml": return "N''";
                case "bit": case "tinyint": case "smallint": case "int": case "bigint": case "decimal": case "numeric":
                case "money": case "smallmoney": case "float": case "real": return "0";
                case "date": case "time": case "datetime": case "datetime2": case "smalldatetime": return "GETDATE()";
                case "datetimeoffset": return "SYSDATETIMEOFFSET()";
                case "uniqueidentifier": return "NEWID()";
                case "binary": case "varbinary": case "image": return "0x";
                default: return "NULL";
            }
        }

        // schema.name, or a bare name in the default schema, else the only object with that name; database parts are ignored.
        private static T? Find<T>(IReadOnlyList<T> items, Func<T, string> schema, Func<T, string> name, List<string> parts, string defaultSchema) where T : class
        {
            var names = StringComparer.OrdinalIgnoreCase;
            string target = parts[parts.Count - 1];
            if (parts.Count >= 2) return items.FirstOrDefault(t => names.Equals(schema(t), parts[parts.Count - 2]) && names.Equals(name(t), target));
            var matches = items.Where(t => names.Equals(name(t), target)).ToList();
            return matches.FirstOrDefault(t => names.Equals(schema(t), defaultSchema)) ?? (matches.Select(schema).Distinct(names).Count() == 1 ? matches[0] : null);
        }

        /// <summary>CREATE/ALTER PROCEDURE headers in the script; batches that do not parse are skipped.</summary>
        public static IReadOnlyList<SchemaProcedure> ProceduresFromScript(string sql, string defaultSchema = "dbo")
        {
            if (sql == null) throw new ArgumentNullException(nameof(sql));
            if (sql.Length > MaxInput) return Array.Empty<SchemaProcedure>();
            var result = new List<SchemaProcedure>();
            // Every procedure header spells PROC, so other batches cannot add one; the rest are cached since typing changes one batch at a time.
            foreach (var batch in Batches(sql))
                if (batch.IndexOf("PROC", StringComparison.OrdinalIgnoreCase) >= 0) result.AddRange(BatchProcedures(batch, defaultSchema));
            return result;
        }

        private static readonly Dictionary<string, SchemaProcedure[]> batchProcedures = new Dictionary<string, SchemaProcedure[]>();
        private static long batchProcedureChars;

        private static SchemaProcedure[] BatchProcedures(string batch, string defaultSchema)
        {
            string key = defaultSchema + "\0" + batch;
            lock (batchProcedures) if (batchProcedures.TryGetValue(key, out var cached)) return cached;
            var found = new List<SchemaProcedure>();
            var fragment = new TSql170Parser(true).Parse(new StringReader(batch), out var errors);
            if (errors.Count == 0)
                foreach (var body in ((TSqlScript)fragment).Batches.SelectMany(b => b.Statements).OfType<ProcedureStatementBody>())
                {
                    var name = body.ProcedureReference?.Name;
                    if (name == null || name.DatabaseIdentifier != null) continue;
                    found.Add(new SchemaProcedure(name.SchemaIdentifier?.Value ?? defaultSchema, name.BaseIdentifier.Value,
                        body.Parameters.Select(p => new SchemaParameter(p.VariableName.Value, SchemaCatalog.TypeName(p.DataType),
                            p.Modifier == ParameterModifier.Output, p.Value != null)).ToArray()));
                }
            var procedures = found.ToArray();
            lock (batchProcedures)
            {
                // ponytail: dropping everything at the cap is simpler than LRU; one keystroke refills it.
                if (batchProcedureChars + key.Length > 4_000_000) { batchProcedures.Clear(); batchProcedureChars = 0; }
                if (!batchProcedures.ContainsKey(key)) batchProcedureChars += key.Length;
                batchProcedures[key] = procedures;
            }
            return procedures;
        }

        private static IEnumerable<string> Batches(string sql)
        {
            int start = 0;
            foreach (var (lineStart, lineEnd) in SqlNavigation.GoLines(sql, new TSql170Parser(true)))
            {
                yield return sql.Substring(start, lineStart - start);
                start = lineEnd;
            }
            yield return sql.Substring(start);
        }

        /// <summary>Quick info for the word at the caret: a variable's type, a procedure's parameters, a table's columns, or a column/alias.</summary>
        public static string? Describe(string sql, int position, IReadOnlyList<SchemaTable>? tables, IReadOnlyList<SchemaProcedure>? procedures,
            string defaultSchema = "dbo")
        {
            if (sql == null) throw new ArgumentNullException(nameof(sql));
            if (position < 0 || position > sql.Length) throw new ArgumentOutOfRangeException(nameof(position));
            if (sql.Length > MaxInput) return null;
            int start = position, end = position;
            while (start > 0 && IsWordChar(sql[start - 1])) start--;
            while (end < sql.Length && IsWordChar(sql[end])) end++;
            if (start == end) return null;
            string word = sql.Substring(start, end - start);
            var names = StringComparer.OrdinalIgnoreCase;
            if (word.StartsWith("@@", StringComparison.Ordinal)) return null;
            if (word[0] == '@') return VariableType(sql, start, word);

            CompletionResult completion;
            try { completion = SqlCompletion.Complete(sql, end, tables, defaultSchema); }
            catch (ArgumentException) { return null; }
            string[] order = { "alias", "cte", "column", "table" };
            var item = completion.Items.Where(c => names.Equals(c.Name, word))
                .Select(c => (Item: c, Rank: Array.FindIndex(order, o => c.Description.StartsWith(o + " ", StringComparison.Ordinal) || c.Description == o)))
                .Where(c => c.Rank >= 0).OrderBy(c => c.Rank).Select(c => c.Item).FirstOrDefault();
            bool qualified = start > 0 && sql[start - 1] == '.';
            var procedure = qualified || item == null || item.Description.StartsWith("table ", StringComparison.Ordinal)
                ? Find(procedures ?? Array.Empty<SchemaProcedure>(), p => p.Schema, p => p.Name, QualifiedName(sql, start, end), defaultSchema) : null;
            if (procedure != null && (item == null || !item.Description.StartsWith("column", StringComparison.Ordinal)))
                return "procedure " + procedure.Schema + "." + procedure.Name + (procedure.Parameters.Count == 0 ? "" : Environment.NewLine +
                    string.Join(Environment.NewLine, procedure.Parameters.Select(p => "  " + p.Name + " " + (p.Type ?? "?") + (p.IsOutput ? " OUTPUT" : "") + (p.HasDefault ? " = default" : ""))));
            if (item == null) return null;
            if (!item.Description.StartsWith("table ", StringComparison.Ordinal)) return item.Name + ": " + item.Description;
            var table = (tables ?? Array.Empty<SchemaTable>()).FirstOrDefault(t => "table " + t.Schema + "." + t.Name == item.Description);
            if (table == null) return item.Description;
            var lines = table.Columns.Take(MaxInfoColumns).Select((c, n) => "  " + OneLine(c) + (table.ColumnTypes?[n] is string type ? " " + type : ""));
            return item.Description + Environment.NewLine + string.Join(Environment.NewLine, lines) +
                (table.Columns.Count > MaxInfoColumns ? Environment.NewLine + "  ... " + (table.Columns.Count - MaxInfoColumns) + " more" : "");
        }

        // The dotted name around [start, end), e.g. dbo.Proc when the caret is on either part; bracketed parts are not unquoted.
        private static List<string> QualifiedName(string sql, int start, int end)
        {
            var parts = new List<string> { sql.Substring(start, end - start) };
            int s = start;
            while (s > 1 && sql[s - 1] == '.' && IsWordChar(sql[s - 2]) && parts.Count < 3)
            {
                int e = s - 1; s = e;
                while (s > 0 && IsWordChar(sql[s - 1])) s--;
                parts.Insert(0, sql.Substring(s, e - s));
            }
            return parts;
        }

        private static string? VariableType(string sql, int offset, string name)
        {
            // The batch holding the caret, or its text up to the caret's line when the whole batch does not parse yet.
            int start = 0, end = sql.Length;
            foreach (var (lineStart, lineEnd) in SqlNavigation.GoLines(sql, new TSql170Parser(true)))
            {
                if (lineEnd <= offset) start = lineEnd;
                else { end = lineStart; break; }
            }
            int lineBegin = sql.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
            foreach (string text in new[] { sql.Substring(start, end - start), sql.Substring(start, Math.Max(0, lineBegin - start)) })
            {
                var fragment = new TSql170Parser(true).Parse(new StringReader(text), out var errors);
                if (errors.Count > 0) continue;
                var declarations = new Declarations();
                fragment.Accept(declarations);
                if (declarations.Types.TryGetValue(name, out var type)) return name + ": " + type;
                return null;
            }
            return null;
        }

        internal sealed class Declarations : TSqlFragmentVisitor
        {
            internal readonly Dictionary<string, string> Types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> Tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public override void Visit(DeclareVariableElement node)
            {
                if (Types.ContainsKey(node.VariableName.Value)) return;
                string type = SchemaCatalog.TypeName(node.DataType) ?? "?";
                Types[node.VariableName.Value] = (node is ProcedureParameter ? "parameter " : "variable ") + type;
            }
            public override void Visit(DeclareTableVariableBody node)
            {
                Tables.Add(node.VariableName.Value);
                if (!Types.ContainsKey(node.VariableName.Value)) Types[node.VariableName.Value] = "table variable";
            }
        }
    }
}

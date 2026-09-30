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
        /// <summary>A user-defined function: shown in parameter hints for name(, never offered after EXEC.</summary>
        public bool IsFunction { get; }
        public SchemaProcedure(string schema, string name, SchemaParameter[] parameters, bool isFunction = false)
        {
            if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(name) || parameters == null || parameters.Any(p => p == null))
                throw new ArgumentException("Procedures need a schema, a name and parameters.");
            Schema = schema; Name = name; Parameters = Array.AsReadOnly((SchemaParameter[])parameters.Clone()); IsFunction = isFunction;
        }
    }

    /// <summary>Signature shown while typing arguments; Current is the argument at the caret, -1 when past the last one.</summary>
    public sealed class ParameterHint
    {
        public string Name { get; }
        public IReadOnlyList<string> Parameters { get; }
        public int Current { get; }
        internal ParameterHint(string name, IReadOnlyList<string> parameters, int current) { Name = name; Parameters = parameters; Current = current; }
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
            var procedure = Find((procedures ?? Array.Empty<SchemaProcedure>()).Where(p => !p.IsFunction).ToList(), p => p.Schema, p => p.Name, parts, defaultSchema);
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

        /// <summary>Lines of a module definition containing <paramref name="text"/> (case-insensitive; whole word when it is a plain name), 1-based, trimmed to 200 chars.</summary>
        public static IReadOnlyList<(int Line, string Text)> MatchingLines(string definition, string text, int max = 50)
        {
            if (definition == null || string.IsNullOrEmpty(text)) return Array.Empty<(int, string)>();
            bool word = text.All(IsWordChar);
            var pattern = new System.Text.RegularExpressions.Regex((word ? @"(?<![\w@#$])" : "") + System.Text.RegularExpressions.Regex.Escape(text) + (word ? @"(?![\w@#$])" : ""),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            var result = new List<(int, string)>();
            string[] lines = definition.Split('\n');
            for (int i = 0; i < lines.Length && result.Count < max; i++)
                if (pattern.IsMatch(lines[i]))
                {
                    string line = lines[i].Trim();
                    result.Add((i + 1, line.Length > 200 ? line.Substring(0, 200) + "..." : line));
                }
            return result;
        }

        /// <summary>
        /// Parameters of the procedure after EXEC (caret past the name) or of the function whose ( is still open at the caret.
        /// Null when neither applies or the name is unknown.
        /// </summary>
        public static ParameterHint? ParameterHintAt(string sql, int position, IReadOnlyList<SchemaProcedure>? procedures, string defaultSchema = "dbo")
        {
            if (sql == null) throw new ArgumentNullException(nameof(sql));
            if (position < 0 || position > sql.Length) throw new ArgumentOutOfRangeException(nameof(position));
            if (procedures == null || procedures.Count == 0) return null;
            // ponytail: only the 4000 characters before the caret are looked at; longer argument lists get no hint.
            int from = Math.Max(0, position - 4000);
            string before = sql.Substring(from, position - from);
            var tokens = new TSql170Parser(true).GetTokenStream(new StringReader(before), out _).Where(t => !Trivia(t)).ToList();
            bool endsInWord = before.Length > 0 && IsWordChar(before[before.Length - 1]);
            int depth = 0, commas = 0;
            for (int i = tokens.Count - 1; i >= 0; i--)
            {
                var t = tokens[i];
                if (t.TokenType == TSqlTokenType.RightParenthesis) depth++;
                else if (t.TokenType == TSqlTokenType.LeftParenthesis)
                {
                    if (depth-- > 0) continue;
                    var parts = NameBefore(tokens, i - 1);
                    var function = parts == null ? null : Find(procedures.Where(p => p.IsFunction).ToList(), p => p.Schema, p => p.Name, parts, defaultSchema);
                    return function == null ? null : Hint(function, commas, null);
                }
                else if (depth > 0) continue;
                else if (t.TokenType == TSqlTokenType.Comma) commas++;
                else if (t.TokenType == TSqlTokenType.Semicolon || t.TokenType == TSqlTokenType.Go) return null;
                else if (t.Text.Equals("EXEC", StringComparison.OrdinalIgnoreCase) || t.Text.Equals("EXECUTE", StringComparison.OrdinalIgnoreCase))
                {
                    int n = i + 1;
                    if (n + 1 < tokens.Count && tokens[n].TokenType == TSqlTokenType.Variable && tokens[n + 1].TokenType == TSqlTokenType.EqualsSign) n += 2;
                    var parts = new List<string>();
                    while (n < tokens.Count && (tokens[n].TokenType == TSqlTokenType.Identifier || tokens[n].TokenType == TSqlTokenType.QuotedIdentifier))
                    {
                        parts.Add(Unquote(tokens[n].Text));
                        if (n + 1 < tokens.Count && tokens[n + 1].TokenType == TSqlTokenType.Dot) n += 2; else { n++; break; }
                    }
                    // Still typing the name: completion's job, not a hint.
                    if (parts.Count == 0 || (n == tokens.Count && endsInWord)) return null;
                    var procedure = Find(procedures.Where(p => !p.IsFunction).ToList(), p => p.Schema, p => p.Name, parts, defaultSchema);
                    if (procedure == null) return null;
                    // A named argument (@x = ...) picks its parameter; otherwise the comma count does.
                    int last = tokens.FindLastIndex(k => k.TokenType == TSqlTokenType.Comma);
                    int argStart = last > n ? last + 1 : n;
                    string? named = argStart + 1 < tokens.Count && tokens[argStart].TokenType == TSqlTokenType.Variable && tokens[argStart + 1].TokenType == TSqlTokenType.EqualsSign
                        ? tokens[argStart].Text : null;
                    return Hint(procedure, commas, named);
                }
                else if (t.TokenType != TSqlTokenType.Identifier && t.TokenType != TSqlTokenType.QuotedIdentifier && t.TokenType != TSqlTokenType.Variable &&
                    t.TokenType != TSqlTokenType.Dot && t.TokenType != TSqlTokenType.EqualsSign && t.TokenType != TSqlTokenType.Integer &&
                    t.TokenType != TSqlTokenType.Numeric && t.TokenType != TSqlTokenType.Real && t.TokenType != TSqlTokenType.Money &&
                    t.TokenType != TSqlTokenType.AsciiStringLiteral && t.TokenType != TSqlTokenType.UnicodeStringLiteral &&
                    t.TokenType != TSqlTokenType.Minus && t.TokenType != TSqlTokenType.Null && t.TokenType != TSqlTokenType.Default &&
                    !t.Text.Equals("OUTPUT", StringComparison.OrdinalIgnoreCase) && !t.Text.Equals("OUT", StringComparison.OrdinalIgnoreCase))
                    return null; // another statement or clause: not in an EXEC argument list
            }
            return null;
        }

        private static string Unquote(string part) =>
            part.Length >= 2 && part[0] == '[' && part[part.Length - 1] == ']' ? part.Substring(1, part.Length - 2).Replace("]]", "]") : part;

        // schema.name ending at token index i (identifiers and dots), or null.
        private static List<string>? NameBefore(List<TSqlParserToken> tokens, int i)
        {
            var parts = new List<string>();
            while (i >= 0 && (tokens[i].TokenType == TSqlTokenType.Identifier || tokens[i].TokenType == TSqlTokenType.QuotedIdentifier))
            {
                parts.Insert(0, Unquote(tokens[i].Text));
                if (i > 0 && tokens[i - 1].TokenType == TSqlTokenType.Dot) i -= 2; else break;
            }
            return parts.Count == 0 ? null : parts;
        }

        private static ParameterHint Hint(SchemaProcedure p, int commas, string? named)
        {
            var parameters = p.Parameters.Select(x => x.Name + " " + (x.Type ?? "?") + (x.HasDefault ? " = default" : "") + (x.IsOutput ? " OUTPUT" : "")).ToList();
            int current = named != null ? p.Parameters.ToList().FindIndex(x => x.Name.Equals(named, StringComparison.OrdinalIgnoreCase))
                : commas < parameters.Count ? commas : -1;
            return new ParameterHint(p.Schema + "." + p.Name, parameters, current);
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
                return (procedure.IsFunction ? "function " : "procedure ") + procedure.Schema + "." + procedure.Name + (procedure.Parameters.Count == 0 ? "" : Environment.NewLine +
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

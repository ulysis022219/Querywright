using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Querywright.Core
{
    public sealed class SchemaForeignKey
    {
        public IReadOnlyList<string> Columns { get; }
        public string ReferencedSchema { get; }
        public string ReferencedTable { get; }
        public IReadOnlyList<string> ReferencedColumns { get; }
        public SchemaForeignKey(string[] columns, string referencedSchema, string referencedTable, string[] referencedColumns)
        {
            if (columns == null || referencedColumns == null || columns.Length == 0 || columns.Length != referencedColumns.Length ||
                columns.Concat(referencedColumns).Any(string.IsNullOrWhiteSpace) ||
                string.IsNullOrWhiteSpace(referencedSchema) || string.IsNullOrWhiteSpace(referencedTable))
                throw new ArgumentException("Foreign keys need nonempty names and matching column counts.");
            Columns = Array.AsReadOnly((string[])columns.Clone()); ReferencedSchema = referencedSchema;
            ReferencedTable = referencedTable; ReferencedColumns = Array.AsReadOnly((string[])referencedColumns.Clone());
        }
    }

    public sealed class SchemaTable
    {
        public string Schema { get; }
        public string Name { get; }
        public IReadOnlyList<string> Columns { get; }
        /// <summary>Data types parallel to <see cref="Columns"/>; null (or a null entry) means unknown.</summary>
        public IReadOnlyList<string?>? ColumnTypes { get; }
        public IReadOnlyList<SchemaForeignKey> ForeignKeys { get; }
        public SchemaTable(string schema, string name, params string[] columns) : this(schema, name, columns, null, null) { }
        public SchemaTable(string schema, string name, string[] columns, SchemaForeignKey[]? foreignKeys) : this(schema, name, columns, null, foreignKeys) { }
        /// <summary>Identity, computed and rowversion columns parallel to <see cref="Columns"/>; INSERT fill skips them.</summary>
        public IReadOnlyList<bool>? Generated { get; }
        /// <summary>A view rather than a table (hover label only).</summary>
        public bool IsView { get; }
        public SchemaTable(string schema, string name, string[] columns, string?[]? columnTypes, SchemaForeignKey[]? foreignKeys, bool[]? generated = null, bool isView = false)
        {
            if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(name) || columns == null || columns.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Schema, table and column names must be nonempty.");
            if (columnTypes != null && columnTypes.Length != columns.Length)
                throw new ArgumentException("Column types must parallel the column names.");
            if (generated != null && generated.Length != columns.Length)
                throw new ArgumentException("Generated flags must parallel the column names.");
            if (foreignKeys != null && foreignKeys.Any(k => k == null || k.Columns.Any(c => !columns.Contains(c, StringComparer.OrdinalIgnoreCase))))
                throw new ArgumentException("Foreign key columns must belong to the table.");
            Schema = schema; Name = name; IsView = isView; Columns = Array.AsReadOnly((string[])columns.Clone());
            ColumnTypes = columnTypes == null ? null : Array.AsReadOnly((string?[])columnTypes.Clone());
            Generated = generated == null ? null : Array.AsReadOnly((bool[])generated.Clone());
            ForeignKeys = Array.AsReadOnly(foreignKeys == null ? Array.Empty<SchemaForeignKey>() : (SchemaForeignKey[])foreignKeys.Clone());
        }
        internal string? TypeOf(string column)
        {
            if (ColumnTypes == null) return null;
            for (int i = 0; i < Columns.Count; i++) if (string.Equals(Columns[i], column, StringComparison.OrdinalIgnoreCase)) return ColumnTypes[i];
            return null;
        }
    }

    public sealed class CompletionItem
    {
        public string Name { get; }
        public string InsertText { get; }
        public string Description { get; }
        internal CompletionItem(string name, string insert, string description)
        { Name = name; InsertText = insert; Description = description; }
    }

    public sealed class CompletionResult
    {
        public int Start { get; }
        public int Length { get; }
        public IReadOnlyList<CompletionItem> Items { get; }
        public string Limitation { get; }
        internal CompletionResult(int start, int length, IEnumerable<CompletionItem> items, string limitation = "")
        { Start = start; Length = length; Items = items.ToArray(); Limitation = limitation; }
    }

    public sealed class TextEdit
    {
        public int Start { get; }
        public int Length { get; }
        public string Text { get; }
        internal TextEdit(int start, int length, string text) { Start = start; Length = length; Text = text; }
    }

    public static class SqlCompletion
    {
        private const string Marker = "__QuerywrightCompletionMarker__";
        private const int MaxItems = 200;
        private static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";
        private static string ColumnDescription(string? type, string source) => "column " + (type == null ? "" : type + " ") + source;

        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ADD", "ALL", "ALTER", "AND", "ANY", "APPLY", "AS", "ASC", "BEGIN", "BETWEEN", "BREAK", "BY", "CASCADE", "CASE", "CATCH",
            "CHECK", "CLOSE", "COLLATE", "COLUMN", "COMMIT", "CONSTRAINT", "CONTINUE", "CREATE", "CROSS", "CURSOR", "DEALLOCATE",
            "DECLARE", "DEFAULT", "DELETE", "DESC", "DISTINCT", "DROP", "ELSE", "END", "EXCEPT", "EXEC", "EXECUTE", "EXISTS", "FETCH",
            "FOR", "FOREIGN", "FROM", "FULL", "FUNCTION", "GO", "GOTO", "GROUP", "HAVING", "IDENTITY", "IF", "IN", "INDEX", "INNER",
            "INSERT", "INTERSECT", "INTO", "IS", "JOIN", "KEY", "LEFT", "LIKE", "MERGE", "NEXT", "NOCOUNT", "NOT", "NULL", "OFFSET",
            "ON", "OPEN", "OPTION", "OR", "ORDER", "OUTER", "OUTPUT", "OVER", "PARTITION", "PERCENT", "PIVOT", "PRIMARY", "PRINT",
            "PROCEDURE", "RAISERROR", "REFERENCES", "RETURN", "RETURNS", "RIGHT", "ROLLBACK", "ROWS", "SCHEMA", "SELECT", "SET",
            "TABLE", "THEN", "THROW", "TIES", "TOP", "TRAN", "TRANSACTION", "TRIGGER", "TRUNCATE", "TRY", "UNION", "UNIQUE",
            "UNPIVOT", "UPDATE", "USE", "USING", "VALUES", "VIEW", "WHEN", "WHERE", "WHILE", "WITH"
        };
        private static readonly string[] Functions =
        {
            "ABS", "AVG", "CAST", "CEILING", "CHARINDEX", "CHECKSUM", "CHOOSE", "COALESCE", "CONCAT", "CONCAT_WS", "CONVERT", "COUNT",
            "COUNT_BIG", "CUME_DIST", "DATALENGTH", "DATEADD", "DATEDIFF", "DATEFROMPARTS", "DATENAME", "DATEPART", "DATETRUNC", "DAY",
            "DENSE_RANK", "EOMONTH", "ERROR_MESSAGE", "ERROR_NUMBER", "EXP", "FIRST_VALUE", "FLOOR", "FORMAT", "GETDATE", "GETUTCDATE",
            "GREATEST", "IIF", "ISNULL", "JSON_QUERY", "JSON_VALUE", "LAG", "LAST_VALUE", "LEAD", "LEAST", "LEFT", "LEN", "LOG", "LOWER",
            "LTRIM", "MAX", "MIN", "MONTH", "NEWID", "NTILE", "NULLIF", "OBJECT_ID", "OPENJSON", "PARSE", "PATINDEX", "POWER", "QUOTENAME",
            "RANK", "REPLACE", "REPLICATE", "REVERSE", "RIGHT", "ROUND", "ROW_NUMBER", "RTRIM", "SCOPE_IDENTITY", "SIGN", "SPACE", "SQRT",
            "STRING_AGG", "STRING_SPLIT", "STUFF", "SUBSTRING", "SUM", "SYSDATETIME", "SYSDATETIMEOFFSET", "TRANSLATE", "TRIM",
            "TRY_CAST", "TRY_CONVERT", "TRY_PARSE", "UPPER", "YEAR"
        };
        private static readonly HashSet<string> SourceKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FROM", "JOIN", "UPDATE", "INTO", "USING", "APPLY", "MERGE" };
        private static readonly HashSet<string> NotAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "APPLY", "OUTPUT", "TABLESAMPLE", "UNPIVOT", "PIVOT", "WINDOW", "SET", "VALUES" };
        private static readonly HashSet<string> Clauses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT", "FROM", "JOIN", "WHERE", "ON", "HAVING", "BY", "SET", "INTO", "UPDATE", "VALUES", "USING", "APPLY", "DECLARE",
            "EXEC", "EXECUTE", "PRINT", "RETURN", "WHEN", "THEN", "ELSE", "AND", "OR", "CASE", "IF", "WHILE"
        };
        private static readonly HashSet<string> ColumnClauses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "SELECT", "WHERE", "ON", "HAVING", "BY", "SET", "VALUES", "WHEN", "THEN", "ELSE", "AND", "OR", "CASE", "PRINT", "RETURN", "IF", "WHILE" };
        private static readonly HashSet<string> DeclareEnds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "SELECT", "SET", "INSERT", "UPDATE", "DELETE", "IF", "WHILE", "EXEC", "EXECUTE", "RETURN", "BEGIN", "PRINT", "MERGE", "FOR" };

        private static readonly HashSet<string> StatementStarts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "WITH", "DECLARE", "SET", "EXEC", "EXECUTE", "IF", "WHILE", "PRINT", "RETURN", "TRUNCATE" };
        private static readonly HashSet<string> Continuations = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "UNION", "ALL", "EXCEPT", "INTERSECT", "THEN" };

        /// <summary>Start offset of the statement holding token index <paramref name="at"/>, for scripts that skip semicolons.</summary>
        // ponytail: keyword heuristic, used only to rank columns; a wrong split just orders them as before.
        private static (int Start, int End) StatementAround(List<Tok> seg, int at)
        {
            int start = 0, end = int.MaxValue, depth = 0;
            string kind = "";
            bool fed = false;
            for (int i = 0; i < seg.Count; i++)
            {
                if (seg[i].Type == TSqlTokenType.LeftParenthesis) depth++;
                else if (seg[i].Type == TSqlTokenType.RightParenthesis) depth = Math.Max(0, depth - 1);
                if (depth != 0 || !seg[i].IsAny(StatementStarts) || (seg[i].Is("WITH") && i + 1 < seg.Count && seg[i + 1].Type == TSqlTokenType.LeftParenthesis)) continue;
                bool continues = i > 0 && (seg[i - 1].IsAny(Continuations) || seg[i - 1].Type == TSqlTokenType.RightParenthesis && kind != "IF" && kind != "WHILE") ||
                    !fed && (kind == "INSERT" || kind == "WITH") && (seg[i].Is("SELECT") || seg[i].Is("EXEC") || seg[i].Is("EXECUTE") || kind == "WITH") ||
                    seg[i].Is("SET") && (kind == "UPDATE" || kind == "MERGE");
                if (continues) { fed = true; continue; }
                if (i >= at) { end = seg[i].Offset; break; }
                start = seg[i].Offset; kind = seg[i].Text.ToUpperInvariant(); fed = false;
            }
            return (start, end);
        }

        private enum Kind { Join, Column, Alias, Table, Variable, Function, Keyword }
        private enum Context { General, Column, Table }

        private readonly struct Tok
        {
            internal readonly TSqlTokenType Type;
            internal readonly int Offset;
            internal readonly string Text;
            internal Tok(TSqlTokenType type, int offset, string text) { Type = type; Offset = offset; Text = text; }
            internal bool IsName => Type == TSqlTokenType.Identifier || Type == TSqlTokenType.QuotedIdentifier;
            internal bool Is(string word) => Type != TSqlTokenType.QuotedIdentifier && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
            internal bool IsAny(HashSet<string> words) => Type != TSqlTokenType.QuotedIdentifier && words.Contains(Text);
            internal string Name => Type != TSqlTokenType.QuotedIdentifier || Text.Length < 2 ? Text
                : Text.Substring(1, Text.Length - 2).Replace(Text[0] == '"' ? "\"\"" : "]]", Text[0] == '"' ? "\"" : "]");
        }

        private sealed class Source
        {
            internal string Alias = "";
            internal bool Explicit;
            internal int Offset;
            internal SchemaTable? Table;
            internal IReadOnlyList<string> Columns = Array.Empty<string>();
            internal string Description = "";
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#' || c == '$';

        // Bracket only names the lexer would not read back as one plain identifier (reserved words, spaces, symbols).
        internal static string QuoteIfNeeded(string name)
        {
            if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_') || !name.All(c => char.IsLetterOrDigit(c) || c == '_')) return Quote(name);
            var tokens = new TSql170Parser(true).GetTokenStream(new StringReader(name), out var errors).Where(t => t.TokenType != TSqlTokenType.EndOfFile).ToList();
            return errors.Count == 0 && tokens.Count == 1 && tokens[0].TokenType == TSqlTokenType.Identifier ? name : Quote(name);
        }

        private static List<Tok> Tokenize(TSql170Parser parser, string text, int shift, out bool errors, out TSqlParserToken? last)
        {
            var result = new List<Tok>();
            last = null;
            var stream = parser.GetTokenStream(new StringReader(text), out var lexicalErrors);
            errors = lexicalErrors.Count > 0;
            foreach (var token in stream)
            {
                if (token.TokenType == TSqlTokenType.EndOfFile) continue;
                last = token;
                if (token.TokenType == TSqlTokenType.WhiteSpace || token.TokenType == TSqlTokenType.SingleLineComment ||
                    token.TokenType == TSqlTokenType.MultilineComment) continue;
                result.Add(new Tok(token.TokenType, token.Offset + shift, token.Text ?? ""));
            }
            return result;
        }

        public static CompletionResult Complete(string sql, int position, IReadOnlyList<SchemaTable>? tables,
            string defaultSchema = "dbo", bool caseSensitive = false, IReadOnlyList<string>? databases = null,
            IReadOnlyList<SchemaProcedure>? procedures = null, bool qualifySingleTable = true,
            Func<string, IReadOnlyList<SchemaTable>?>? otherDatabase = null)
        {
            if (sql == null) throw new ArgumentNullException(nameof(sql));
            if (position < 0 || position > sql.Length) throw new ArgumentOutOfRangeException(nameof(position));
            if (sql.Length > 1_000_000) throw new ArgumentException("Completion input exceeds 1,000,000 characters.");
            var catalog = tables ?? Array.Empty<SchemaTable>();
            var names = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            int start = position, end = position;
            while (start > 0 && IsWordChar(sql[start - 1])) start--;
            while (end < sql.Length && IsWordChar(sql[end])) end++;
            string prefix = sql.Substring(start, position - start);
            var parser = new TSql170Parser(true);
            // The word at the caret is excluded; the text before it must lex cleanly or the caret sits in a literal, comment or quoted name.
            var tokens = Tokenize(parser, sql.Substring(0, start), 0, out bool beforeErrors, out var last);
            if (beforeErrors || (last?.TokenType == TSqlTokenType.SingleLineComment && !last.Text.EndsWith("\n")))
                return new CompletionResult(position, 0, Array.Empty<CompletionItem>(), "Completion is inactive inside literals and comments.");
            int caret = tokens.Count;
            tokens.AddRange(Tokenize(parser, sql.Substring(end), end, out _, out _));
            int segStart = caret, segEnd = caret, batchStart = caret;
            while (segStart > 0 && tokens[segStart - 1].Type != TSqlTokenType.Semicolon && tokens[segStart - 1].Type != TSqlTokenType.Go) segStart--;
            while (segEnd < tokens.Count && tokens[segEnd].Type != TSqlTokenType.Semicolon && tokens[segEnd].Type != TSqlTokenType.Go) segEnd++;
            while (batchStart > 0 && tokens[batchStart - 1].Type != TSqlTokenType.Go) batchStart--;
            var segment = tokens.GetRange(segStart, segEnd - segStart);
            int at = caret - segStart;

            Resolver? resolver = null;
            if (!sql.Contains(Marker))
            {
                // Only the caret's batch is parsed: faster on long scripts, and syntax errors in other batches no longer turn resolution off.
                int batchEnd = caret;
                while (batchEnd < tokens.Count && tokens[batchEnd].Type != TSqlTokenType.Go) batchEnd++;
                int from = batchStart == 0 ? 0 : tokens[batchStart - 1].Offset + tokens[batchStart - 1].Text.Length;
                int to = batchEnd < tokens.Count ? tokens[batchEnd].Offset : sql.Length;
                var fragment = parser.Parse(new StringReader(sql.Substring(from, start - from) + Marker + sql.Substring(end, to - end)), out var errors);
                if (errors.Count == 0) { resolver = new Resolver(catalog, defaultSchema, names); fragment.Accept(resolver); }
            }
            var scan = new Scanner(sql, segment, catalog, defaultSchema, names);
            var items = new List<(CompletionItem Item, Kind Kind)>();
            var far = new HashSet<CompletionItem>(); // columns of other statements in the batch
            void Add(Kind kind, string name, string insert, string description) => items.Add((new CompletionItem(name, insert, description), kind));

            var context = Context.General;
            List<string>? qualifier = null;
            if (at > 0 && segment[at - 1].Type == TSqlTokenType.Dot && segment[at - 1].Offset == start - 1)
            {
                qualifier = new List<string>();
                for (int i = at - 2; i >= 0 && segment[i].IsName; i -= 2)
                {
                    qualifier.Insert(0, segment[i].Name);
                    if (i == 0 || segment[i - 1].Type != TSqlTokenType.Dot) break;
                }
            }
            // EXEC | , EXEC @rc = | and EXEC schema.| list procedures; committing one fills its parameters.
            int keywordAt = at - 1 - (qualifier == null ? 0 : 2 * qualifier.Count);
            if (keywordAt >= 2 && segment[keywordAt].Type == TSqlTokenType.EqualsSign && segment[keywordAt - 1].Type == TSqlTokenType.Variable) keywordAt -= 2;
            if (procedures != null && procedures.Count > 0 && keywordAt >= 0 && (segment[keywordAt].Is("EXEC") || segment[keywordAt].Is("EXECUTE")) &&
                (qualifier == null || qualifier.Count == 1))
            {
                context = Context.Table;
                foreach (var p in procedures.Where(p => qualifier == null || names.Equals(p.Schema, qualifier[0])))
                    Add(Kind.Table, p.Name, (qualifier == null ? QuoteIfNeeded(p.Schema) + "." : "") + QuoteIfNeeded(p.Name), "procedure " + p.Schema + "." + p.Name);
            }
            else if (qualifier != null && CrossDatabase(qualifier)) { }
            else if (qualifier != null)
            {
                bool alias = qualifier.Count == 1 && scan.Sources.Any(s => names.Equals(s.Alias, qualifier[0]));
                if (resolver != null && resolver.Handled && (alias || resolver.TableMarker || resolver.Items.Count > 0))
                    items.AddRange(resolver.Items.Select(i => (i, Kind.Column)));
                else if (qualifier.Count > 0) Qualified(qualifier);
            }
            else if (at > 0 && segment[at - 1].Is("USE"))
                foreach (var database in databases ?? Array.Empty<string>()) Add(Kind.Table, database, QuoteIfNeeded(database), "database");
            else
            {
                Tok? prev = at > 0 ? segment[at - 1] : (Tok?)null;
                string? clause = null;
                for (int i = at - 1, depth = 0; i >= 0 && clause == null; i--)
                {
                    if (segment[i].Type == TSqlTokenType.RightParenthesis) depth++;
                    else if (segment[i].Type == TSqlTokenType.LeftParenthesis) depth = Math.Max(0, depth - 1);
                    else if (depth == 0 && segment[i].IsAny(Clauses)) clause = segment[i].Text.ToUpperInvariant();
                }
                if (prev is Tok p && (p.IsAny(SourceKeywords) && !p.Is("APPLY") ||
                    p.Is("TABLE") && at > 1 && (segment[at - 2].Is("TRUNCATE") || segment[at - 2].Is("DROP") || segment[at - 2].Is("ALTER")) ||
                    p.Type == TSqlTokenType.Comma && clause == "FROM"))
                    context = Context.Table;
                else if (clause != null && ColumnClauses.Contains(clause) && prev is Tok o && ExpectsOperand(o, at > 1 ? segment[at - 2] : (Tok?)null))
                    context = Context.Column;

                var variables = Variables(tokens, batchStart, caret, names);
                foreach (var cte in scan.Ctes.Keys) Add(Kind.Alias, cte, QuoteIfNeeded(cte), "cte");
                foreach (var v in variables.Where(v => context != Context.Table || v.Value))
                    Add(Kind.Variable, v.Key, v.Key, v.Value ? "table variable" : "variable");
                foreach (var table in catalog)
                    Add(Kind.Table, table.Name, QuoteIfNeeded(table.Schema) + "." + QuoteIfNeeded(table.Name), "table " + table.Schema + "." + table.Name);
                // FROM | also offers databases, for OtherDb.schema.table.
                if (context == Context.Table)
                    foreach (var database in databases ?? Array.Empty<string>()) Add(Kind.Table, database, QuoteIfNeeded(database), "database");
                if (context != Context.Table)
                {
                    foreach (var join in Joins(segment, at, scan, catalog, defaultSchema, names)) items.Add((join, Kind.Join));
                    // ponytail: ORDER and GROUP are only ever followed by BY; name == insert text, as the SSMS list commits reliably then.
                    foreach (var k in Keywords) { string w = k.Equals("ORDER", StringComparison.OrdinalIgnoreCase) || k.Equals("GROUP", StringComparison.OrdinalIgnoreCase) ? k + " BY" : k; Add(Kind.Keyword, w, w, "keyword"); }
                    foreach (var f in Functions) Add(Kind.Function, f, f + "(", "function");
                    foreach (var s in scan.Sources.Where(s => s.Explicit)) Add(Kind.Alias, s.Alias, QuoteIfNeeded(s.Alias), "alias " + s.Description);
                    // Tables of the caret's own statement first; others in the same batch (no semicolons between) rank after them.
                    var (statementStart, statementEnd) = StatementAround(segment, at);
                    var near = scan.Sources.Where(s => s.Offset >= statementStart && s.Offset < statementEnd).ToList();
                    if (near.Count == 0 && context == Context.Column)
                        foreach (var table in catalog)
                            foreach (var column in table.Columns)
                                Add(Kind.Column, column, QuoteIfNeeded(column), ColumnDescription(table.TypeOf(column), table.Schema + "." + table.Name));
                    else if (resolver != null && resolver.Handled && !resolver.TableMarker)
                        items.AddRange(resolver.Items.Select(i => (resolver.Sources == 1 && !qualifySingleTable ? Bare(i) : i, Kind.Column)));
                    else
                    {
                        var seen = new HashSet<string>(names);
                        foreach (var s in near.Concat(scan.Sources.Except(near)).Where(s => seen.Add(s.Alias)))
                            foreach (var column in s.Columns)
                            {
                                var item = new CompletionItem(column, QuoteIfNeeded(s.Alias) + "." + QuoteIfNeeded(column), ColumnDescription(s.Table?.TypeOf(column), s.Alias + "." + column));
                                if (!near.Contains(s)) far.Add(item);
                                else if (near.Count == 1 && !qualifySingleTable) item = Bare(item);
                                items.Add((item, Kind.Column));
                            }
                    }
                }
            }

            // OtherDb. lists schemas, OtherDb.sch. tables, OtherDb.sch.tbl. columns; null catalog (still loading) offers nothing.
            bool CrossDatabase(List<string> parts)
            {
                if (otherDatabase == null || databases == null || parts.Count == 0 || parts.Count > 3) return false;
                var db = databases.FirstOrDefault(d => names.Equals(d, parts[0]));
                if (db == null) return false;
                if (parts.Count == 1 && (scan.Sources.Any(s => names.Equals(s.Alias, parts[0])) || catalog.Any(t => names.Equals(t.Schema, parts[0])))) return false;
                var other = otherDatabase(db) ?? Array.Empty<SchemaTable>();
                if (parts.Count == 1)
                    foreach (var schema in other.Select(t => t.Schema).Distinct(names)) Add(Kind.Table, schema, QuoteIfNeeded(schema), "schema in " + db);
                else if (parts.Count == 2)
                    foreach (var t in other.Where(t => names.Equals(t.Schema, parts[1]))) Add(Kind.Table, t.Name, QuoteIfNeeded(t.Name), "table " + db + "." + t.Schema + "." + t.Name);
                else
                    foreach (var t in other.Where(t => names.Equals(t.Schema, parts[1]) && names.Equals(t.Name, parts[2])))
                        foreach (var column in t.Columns) Add(Kind.Column, column, QuoteIfNeeded(column), ColumnDescription(t.TypeOf(column), db + "." + t.Schema + "." + t.Name + "." + column));
                return true;
            }

            void Qualified(List<string> parts)
            {
                var source = parts.Count == 1 ? scan.Sources.Where(s => names.Equals(s.Alias, parts[0])).OrderBy(s => s.Columns.Count == 0).FirstOrDefault() : null;
                var table = parts.Count > 2 ? null : catalog.FirstOrDefault(t => names.Equals(t.Name, parts[parts.Count - 1]) &&
                    names.Equals(t.Schema, parts.Count == 2 ? parts[0] : defaultSchema));
                if (source != null || table != null)
                {
                    string label = source?.Alias ?? table!.Name;
                    foreach (var column in source?.Columns ?? table!.Columns)
                        Add(Kind.Column, column, QuoteIfNeeded(column), ColumnDescription((source?.Table ?? table)?.TypeOf(column), label + "." + column));
                }
                else if (parts.Count == 1 && scan.Ctes.TryGetValue(parts[0], out var cteColumns))
                    foreach (var column in cteColumns) Add(Kind.Column, column, QuoteIfNeeded(column), ColumnDescription(null, parts[0] + "." + column));
                else if (parts.Count == 1)
                    foreach (var t in catalog.Where(t => names.Equals(t.Schema, parts[0])))
                        Add(Kind.Table, t.Name, QuoteIfNeeded(t.Name), "table " + t.Schema + "." + t.Name);
            }

            int Rank(Kind kind) => kind == Kind.Join ? -1 : context == Context.Table ? 0
                : context == Context.Column ? (kind == Kind.Column ? 0 : kind == Kind.Alias || kind == Kind.Table ? 1 : kind == Kind.Variable ? 2 : kind == Kind.Function ? 3 : 4)
                : (kind == Kind.Keyword ? 0 : kind == Kind.Variable ? 1 : kind == Kind.Alias || kind == Kind.Function ? 2 : 3);
            var ordered = items
                .Where(i => i.Item.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .GroupBy(i => i.Item.InsertText, StringComparer.Ordinal).Select(g => g.OrderBy(i => Rank(i.Kind)).First())
                .OrderBy(i => i.Kind == Kind.Join ? 0 : 1)
                .ThenBy(i => prefix.Length > 0 && string.Equals(i.Item.Name, prefix, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(i => Rank(i.Kind)).ThenBy(i => far.Contains(i.Item) ? 1 : 0).ThenBy(i => i.Item.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxItems).Select(i => i.Item);
            return new CompletionResult(start, end - start, ordered);
        }

        private static CompletionItem Bare(CompletionItem item) => new CompletionItem(item.Name, QuoteIfNeeded(item.Name), item.Description);

        // An operand is expected unless the previous token already completes one (name, literal, closing parenthesis, SELECT *).
        private static bool ExpectsOperand(Tok prev, Tok? before)
        {
            if (prev.IsName || prev.Type == TSqlTokenType.Variable || prev.Type == TSqlTokenType.RightParenthesis || prev.Is("NULL") ||
                prev.Type == TSqlTokenType.Integer || prev.Type == TSqlTokenType.Numeric || prev.Type == TSqlTokenType.Real ||
                prev.Type == TSqlTokenType.Money || prev.Type == TSqlTokenType.HexLiteral ||
                prev.Type == TSqlTokenType.AsciiStringLiteral || prev.Type == TSqlTokenType.UnicodeStringLiteral) return false;
            return !(prev.Type == TSqlTokenType.Star && before is Tok b && (b.Is("SELECT") || b.Type == TSqlTokenType.Comma || b.Type == TSqlTokenType.Dot));
        }

        // Variables declared by DECLARE or in a CREATE/ALTER PROCEDURE/FUNCTION header before the caret; value marks table variables.
        private static Dictionary<string, bool> Variables(List<Tok> tokens, int from, int to, StringComparer names)
        {
            var result = new Dictionary<string, bool>(names);
            int mode = 0, depth = 0;
            for (int i = from; i < to; i++)
            {
                var t = tokens[i];
                if (t.Type == TSqlTokenType.LeftParenthesis) depth++;
                else if (t.Type == TSqlTokenType.RightParenthesis) depth--;
                else if (t.Type == TSqlTokenType.Semicolon) mode = 0;
                else if (t.Is("DECLARE")) { mode = 1; depth = 0; }
                else if ((t.Is("PROCEDURE") || t.Is("PROC") || t.Is("FUNCTION")) && i > from && (tokens[i - 1].Is("CREATE") || tokens[i - 1].Is("ALTER")))
                { mode = 2; depth = 0; }
                else if (mode == 2 && depth == 0 && t.Is("AS")) mode = 0;
                else if (mode == 1 && depth == 0 && t.IsAny(DeclareEnds)) mode = 0;
                else if (t.Type == TSqlTokenType.Variable && (mode == 1 && depth == 0 && i > from && (tokens[i - 1].Is("DECLARE") || tokens[i - 1].Type == TSqlTokenType.Comma) ||
                    mode == 2 && depth <= 1))
                {
                    int n = i + 1 < to && tokens[i + 1].Is("AS") ? i + 2 : i + 1;
                    result[t.Text] = n < to && tokens[n].Is("TABLE");
                }
            }
            return result;
        }

        // Full join conditions when the caret follows JOIN <table> [[AS] alias] ON and a foreign key links it to an earlier source.
        private static IEnumerable<CompletionItem> Joins(List<Tok> seg, int at, Scanner scan, IReadOnlyList<SchemaTable> catalog, string defaultSchema, StringComparer names)
        {
            int k = at - 1;
            if (k < 2 || !seg[k].Is("ON")) yield break;
            k--;
            string? alias = null;
            if (seg[k].IsName && !seg[k].IsAny(NotAliases) && seg[k - 1].Type != TSqlTokenType.Dot) { alias = seg[k].Name; k--; }
            if (k >= 0 && seg[k].Is("AS")) k--;
            var parts = new List<string>();
            if (alias != null && k >= 0 && seg[k].Is("JOIN")) { parts.Add(alias); alias = null; }
            else
                for (; k >= 0 && seg[k].IsName; k -= 2)
                {
                    parts.Insert(0, seg[k].Name);
                    if (k == 0 || seg[k - 1].Type != TSqlTokenType.Dot) { k--; break; }
                }
            if (parts.Count == 0 || parts.Count > 2 || k < 0 || !seg[k].Is("JOIN")) yield break;
            var joined = catalog.FirstOrDefault(t => names.Equals(t.Name, parts[parts.Count - 1]) && names.Equals(t.Schema, parts.Count == 2 ? parts[0] : defaultSchema));
            if (joined == null) yield break;
            string left = QuoteIfNeeded(alias ?? joined.Name);
            bool Refers(SchemaForeignKey key, SchemaTable table) => names.Equals(key.ReferencedSchema, table.Schema) && names.Equals(key.ReferencedTable, table.Name);
            CompletionItem Item(IEnumerable<(string J, string S)> pairs, string right, SchemaTable from, SchemaTable to)
            {
                string text = string.Join(" AND ", pairs.Select(p => left + "." + QuoteIfNeeded(p.J) + " = " + right + "." + QuoteIfNeeded(p.S)));
                return new CompletionItem(text, text, "foreign key " + from.Schema + "." + from.Name + " -> " + to.Schema + "." + to.Name);
            }
            foreach (var source in scan.Sources.Where(s => s.Offset < seg[k].Offset && s.Table != null))
            {
                string right = QuoteIfNeeded(source.Alias);
                foreach (var key in joined.ForeignKeys.Where(f => Refers(f, source.Table!)))
                    yield return Item(key.Columns.Zip(key.ReferencedColumns, (c, r) => (c, r)), right, joined, source.Table!);
                foreach (var key in source.Table!.ForeignKeys.Where(f => Refers(f, joined)))
                    yield return Item(key.ReferencedColumns.Zip(key.Columns, (r, c) => (r, c)), right, source.Table!, joined);
            }
        }

        private static IEnumerable<string> BodyColumns(string body)
        {
            var fragment = new TSql170Parser(true).Parse(new StringReader(body), out var errors);
            var statement = errors.Count == 0 ? (fragment as TSqlScript)?.Batches.SelectMany(b => b.Statements).FirstOrDefault() as SelectStatement : null;
            return statement == null ? Array.Empty<string>() : Projection(statement.QueryExpression).ToArray();
        }

        private static IEnumerable<string> Projection(QueryExpression expression)
        {
            if (expression is QueryParenthesisExpression parenthesis) return Projection(parenthesis.QueryExpression);
            if (expression is BinaryQueryExpression binary) return Projection(binary.FirstQueryExpression);
            if (!(expression is QuerySpecification query)) return Array.Empty<string>();
            return query.SelectElements.OfType<SelectScalarExpression>().Select(column => column.ColumnName?.Value
                ?? (column.Expression as ColumnReferenceExpression)?.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value)
                .Where(name => name != null).Select(name => name!);
        }

        /// <summary>Token-level CTE and FROM-source discovery for one statement; works on SQL that does not parse.</summary>
        private sealed class Scanner
        {
            internal readonly Dictionary<string, IReadOnlyList<string>> Ctes;
            internal readonly List<Source> Sources = new List<Source>();
            private readonly string sql;
            private readonly List<Tok> seg;
            private readonly IReadOnlyList<SchemaTable> catalog;
            private readonly string defaultSchema;
            private readonly StringComparer names;

            internal Scanner(string sql, List<Tok> seg, IReadOnlyList<SchemaTable> catalog, string defaultSchema, StringComparer names)
            {
                this.sql = sql; this.seg = seg; this.catalog = catalog; this.defaultSchema = defaultSchema; this.names = names;
                Ctes = new Dictionary<string, IReadOnlyList<string>>(names);
                for (int i = 0; i + 1 < seg.Count; i++)
                    if (seg[i].Is("WITH") && seg[i + 1].IsName) ReadCtes(i + 1);
                for (int i = 0; i < seg.Count; i++)
                {
                    if (!seg[i].IsAny(SourceKeywords)) continue;
                    int j = i + 1;
                    while (true)
                    {
                        j = ReadSource(j, seg[i].Offset);
                        if (!seg[i].Is("FROM") || j >= seg.Count || seg[j].Type != TSqlTokenType.Comma) break;
                        j++;
                    }
                }
            }

            private int Match(int open)
            {
                for (int i = open, depth = 0; i < seg.Count; i++)
                {
                    if (seg[i].Type == TSqlTokenType.LeftParenthesis) depth++;
                    else if (seg[i].Type == TSqlTokenType.RightParenthesis && --depth == 0) return i;
                }
                return -1;
            }

            private bool Open(int i) => i < seg.Count && seg[i].Type == TSqlTokenType.LeftParenthesis;

            private string Body(int open, int close) => close <= open + 1 ? "" : sql.Substring(seg[open + 1].Offset, seg[close].Offset - seg[open + 1].Offset);

            private List<string> NameList(int open, int close) => seg.GetRange(open + 1, close - open - 1).Where(t => t.IsName).Select(t => t.Name).ToList();

            private void ReadCtes(int j)
            {
                while (j < seg.Count && seg[j].IsName)
                {
                    string name = seg[j++].Name;
                    List<string>? columns = null;
                    if (Open(j))
                    {
                        int close = Match(j);
                        if (close < 0) return;
                        columns = NameList(j, close); j = close + 1;
                    }
                    if (j >= seg.Count || !seg[j].Is("AS") || !Open(j + 1)) return;
                    int end = Match(j + 1);
                    Ctes[name] = columns ?? (end < 0 ? new List<string>() : BodyColumns(Body(j + 1, end)).ToList());
                    if (end < 0 || end + 1 >= seg.Count || seg[end + 1].Type != TSqlTokenType.Comma) return;
                    j = end + 2;
                }
            }

            private int ReadSource(int j, int offset)
            {
                if (j >= seg.Count) return j;
                var source = new Source { Offset = offset };
                List<string>? parts = null;
                bool derived = false;
                if (Open(j))
                {
                    int close = Match(j);
                    if (close < 0) return seg.Count;
                    source.Columns = BodyColumns(Body(j, close)).ToList(); source.Description = "derived table";
                    derived = true; j = close + 1;
                }
                else if (seg[j].Type == TSqlTokenType.Variable) { source.Alias = seg[j].Text; source.Description = "table variable"; j++; }
                else if (seg[j].IsName)
                {
                    parts = new List<string> { seg[j++].Name };
                    while (j + 1 < seg.Count && seg[j].Type == TSqlTokenType.Dot && seg[j + 1].IsName) { parts.Add(seg[j + 1].Name); j += 2; }
                    if (j < seg.Count && seg[j].Type == TSqlTokenType.Dot) return j; // Incomplete name at the caret.
                    if (Open(j)) { int close = Match(j); j = close < 0 ? seg.Count : close + 1; }
                    source.Alias = parts[parts.Count - 1];
                }
                else return j;
                if (j < seg.Count && seg[j].Is("AS")) j++;
                if (j < seg.Count && seg[j].IsName && !seg[j].IsAny(NotAliases))
                {
                    source.Alias = seg[j++].Name; source.Explicit = true;
                    if (derived && Open(j)) { int close = Match(j); if (close > 0) { source.Columns = NameList(j, close); j = close + 1; } }
                }
                if (source.Alias.Length == 0) return j;
                if (parts != null)
                {
                    if (parts.Count == 1 && Ctes.TryGetValue(parts[0], out var cte)) { source.Columns = cte; source.Description = "cte " + parts[0]; }
                    else
                    {
                        source.Table = parts.Count > 2 ? null : catalog.FirstOrDefault(t => names.Equals(t.Name, parts[parts.Count - 1]) &&
                            names.Equals(t.Schema, parts.Count == 2 ? parts[0] : defaultSchema));
                        source.Columns = source.Table?.Columns ?? Array.Empty<string>();
                        source.Description = source.Table == null ? string.Join(".", parts) : source.Table.Schema + "." + source.Table.Name;
                    }
                }
                Sources.Add(source);
                return j;
            }
        }

        public static TextEdit ExpandWildcard(string sql, int position, IReadOnlyList<SchemaTable> tables,
            string defaultSchema = "dbo", bool caseSensitive = false) => Expand(sql, position, tables, defaultSchema, caseSensitive).Expansion!;

        /// <summary>The span of * (or alias.*) at the caret and the column references it stands for, for a column picker.</summary>
        public static (TextEdit Wildcard, IReadOnlyList<string> Columns) WildcardColumns(string sql, int position, IReadOnlyList<SchemaTable> tables,
            string defaultSchema = "dbo", bool caseSensitive = false)
        {
            var visitor = Expand(sql, position, tables, defaultSchema, caseSensitive);
            var star = visitor.Expansion!;
            return (new TextEdit(star.Start, star.Length, sql.Substring(star.Start, star.Length)), visitor.Parts.ToArray());
        }

        /// <summary>Columns one per line, each aligned under the column where the list starts.</summary>
        public static string ColumnList(string sql, int start, IEnumerable<string> columns)
        {
            int lineStart = sql.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
            // Keep tabs so the alignment holds whatever the editor's tab size.
            string indent = new string(sql.Substring(lineStart, start - lineStart).Select(c => c == '\t' ? '\t' : ' ').ToArray());
            return string.Join("," + (sql.Contains("\r\n") || !sql.Contains("\n") ? "\r\n" : "\n") + indent, columns);
        }

        private static Resolver Expand(string sql, int position, IReadOnlyList<SchemaTable> tables, string defaultSchema, bool caseSensitive)
        {
            if (sql == null || tables == null) throw new ArgumentNullException(sql == null ? nameof(sql) : nameof(tables));
            if (position < 0 || position > sql.Length) throw new ArgumentOutOfRangeException(nameof(position));
            if (sql.Length > 1_000_000) throw new ArgumentException("Expansion input exceeds 1,000,000 characters.");
            var parser = new TSql170Parser(true);
            var fragment = parser.Parse(new StringReader(sql), out var errors);
            if (errors.Count > 0) throw new FormatException("Fix SQL syntax errors before expanding a wildcard.");
            var visitor = new Resolver(tables, defaultSchema, caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase, position);
            fragment.Accept(visitor);
            if (visitor.Expansion == null) throw new InvalidOperationException("Place the caret on * or alias.* in a SELECT list.");
            visitor.Expansion = new TextEdit(visitor.Expansion.Start, visitor.Expansion.Length, ColumnList(sql, visitor.Expansion.Start, visitor.Parts));
            string result = sql.Substring(0, visitor.Expansion.Start) + visitor.Expansion.Text +
                sql.Substring(visitor.Expansion.Start + visitor.Expansion.Length);
            parser.Parse(new StringReader(result), out var finalErrors);
            if (finalErrors.Count > 0) throw new InvalidOperationException("Expansion produced invalid SQL; original text retained.");
            return visitor;
        }

        private sealed class Resolver : TSqlFragmentVisitor
        {
            internal readonly List<CompletionItem> Items = new List<CompletionItem>();
            internal TextEdit? Expansion;
            internal readonly List<string> Parts = new List<string>();
            internal bool Handled, TableMarker;
            internal int Sources; // tables visible at an unqualified column marker
            private readonly int wildcardPosition = -1;
            private readonly IReadOnlyList<SchemaTable> tables;
            private readonly string defaultSchema;
            private readonly StringComparer names;
            private readonly List<QuerySpecification> scopes = new List<QuerySpecification>();
            private readonly Stack<int> scopeBoundaries = new Stack<int>();
            private readonly Stack<Dictionary<string, CommonTableExpression>> cteScopes = new Stack<Dictionary<string, CommonTableExpression>>();
            internal Resolver(IReadOnlyList<SchemaTable> tables, string defaultSchema, StringComparer names)
            { this.tables = tables; this.defaultSchema = defaultSchema; this.names = names; }
            internal Resolver(IReadOnlyList<SchemaTable> tables, string defaultSchema, StringComparer names, int wildcardPosition)
                : this(tables, defaultSchema, names) { this.wildcardPosition = wildcardPosition; }

            public override void Visit(SelectStarExpression node)
            {
                if (Expansion != null || node.StartOffset > wildcardPosition || wildcardPosition > node.StartOffset + node.FragmentLength) return;
                var ids = node.Qualifier?.Identifiers;
                if (ids != null && ids.Count > 1) throw new InvalidOperationException("Use an alias or table name qualifier, not schema.table.*.");
                var from = scopes.Count == 0 ? null : scopes[scopes.Count - 1].FromClause;
                if (from == null) throw new InvalidOperationException("Wildcard has no FROM clause to expand.");
                var references = from.TableReferences.SelectMany(Tables).ToArray();
                var parts = Parts;
                bool qualify = ids != null || references.Length > 1;
                foreach (var reference in references)
                {
                    string? alias = reference?.Alias?.Value ?? (reference as NamedTableReference)?.SchemaObject.BaseIdentifier.Value;
                    if (ids != null && (alias == null || !names.Equals(alias, ids[0].Value))) continue;
                    var columns = reference == null ? Array.Empty<string>() : Columns(reference).ToArray();
                    if (alias == null || columns.Length == 0)
                        throw new InvalidOperationException("Columns unknown for " + (alias ?? "a FROM source") + "; offline schema, CTE or derived column list required.");
                    parts.AddRange(columns.Select(c => qualify ? QuoteIfNeeded(alias) + "." + QuoteIfNeeded(c) : QuoteIfNeeded(c)));
                }
                if (parts.Count == 0) throw new InvalidOperationException("Wildcard qualifier " + ids![0].Value + " is not in this FROM clause.");
                Expansion = new TextEdit(node.StartOffset, node.FragmentLength, string.Join(", ", parts));
            }

            public override void ExplicitVisit(SelectStatement node)
            {
                var ctes = new Dictionary<string, CommonTableExpression>(names);
                if (node.WithCtesAndXmlNamespaces != null)
                    foreach (var cte in node.WithCtesAndXmlNamespaces.CommonTableExpressions) ctes[cte.ExpressionName.Value] = cte;
                cteScopes.Push(ctes);
                base.ExplicitVisit(node);
                cteScopes.Pop();
            }

            public override void ExplicitVisit(QueryDerivedTable node)
            {
                // ponytail: derived tables isolate outer scopes; correlated APPLY binding requires join-side tracking.
                scopeBoundaries.Push(scopes.Count);
                base.ExplicitVisit(node);
                scopeBoundaries.Pop();
            }

            public override void ExplicitVisit(QuerySpecification node)
            {
                scopes.Add(node);
                base.ExplicitVisit(node);
                scopes.RemoveAt(scopes.Count - 1);
            }

            public override void Visit(NamedTableReference node)
            {
                if (node.SchemaObject.BaseIdentifier.Value != Marker) return;
                string? schema = node.SchemaObject.SchemaIdentifier?.Value;
                if (node.SchemaObject.DatabaseIdentifier != null || node.SchemaObject.ServerIdentifier != null) return;
                Handled = TableMarker = true;
                foreach (var table in tables.Where(t => schema == null || names.Equals(t.Schema, schema)))
                    Items.Add(new CompletionItem(table.Name, schema == null ? QuoteIfNeeded(table.Schema) + "." + QuoteIfNeeded(table.Name) : QuoteIfNeeded(table.Name),
                        "table " + table.Schema + "." + table.Name));
            }

            public override void Visit(ColumnReferenceExpression node)
            {
                var ids = node.MultiPartIdentifier?.Identifiers;
                if (ids == null || ids.Count == 0 || ids[ids.Count - 1].Value != Marker || ids.Count > 2) return;
                Handled = true;
                string? qualifier = ids.Count == 2 ? ids[0].Value : null;
                var seen = new HashSet<string>(names);
                int boundary = scopeBoundaries.Count == 0 ? 0 : scopeBoundaries.Peek();
                for (int i = scopes.Count - 1; i >= boundary; i--)
                {
                    if (scopes[i].FromClause == null) continue;
                    foreach (var reference in scopes[i].FromClause.TableReferences.SelectMany(Tables))
                    {
                        string? alias = reference?.Alias?.Value ?? (reference as NamedTableReference)?.SchemaObject.BaseIdentifier.Value;
                        if (alias == null) continue;
                        if (!seen.Add(alias) || (qualifier != null && !names.Equals(alias, qualifier))) continue;
                        var table = reference is NamedTableReference named && !IsCte(named) ? Find(named) : null;
                        foreach (string column in Columns(reference!))
                            Items.Add(new CompletionItem(column, qualifier == null ? QuoteIfNeeded(alias) + "." + QuoteIfNeeded(column) : QuoteIfNeeded(column),
                                ColumnDescription(table?.TypeOf(column), alias + "." + column)));
                        Sources++;
                        if (qualifier != null) return; // Inner aliases shadow outer aliases, even when metadata is missing.
                    }
                }
            }

            private IEnumerable<string> Columns(TableReferenceWithAlias reference)
            {
                if (reference is QueryDerivedTable derived)
                    return derived.Columns.Count > 0 ? derived.Columns.Select(c => c.Value) : Projection(derived.QueryExpression);
                if (!(reference is NamedTableReference named)) return Array.Empty<string>();
                if (named.SchemaObject.SchemaIdentifier == null)
                    foreach (var scope in cteScopes)
                        if (scope.TryGetValue(named.SchemaObject.BaseIdentifier.Value, out var cte))
                            return cte.Columns.Count > 0 ? cte.Columns.Select(c => c.Value) : Projection(cte.QueryExpression);
                return Find(named)?.Columns ?? Array.Empty<string>();
            }

            private bool IsCte(NamedTableReference named) =>
                named.SchemaObject.SchemaIdentifier == null && cteScopes.Any(scope => scope.ContainsKey(named.SchemaObject.BaseIdentifier.Value));

            private SchemaTable? Find(NamedTableReference reference)
            {
                var name = reference.SchemaObject;
                if (name.DatabaseIdentifier != null || name.ServerIdentifier != null) return null;
                string schema = name.SchemaIdentifier?.Value ?? defaultSchema;
                return tables.FirstOrDefault(t => names.Equals(t.Schema, schema) && names.Equals(t.Name, name.BaseIdentifier.Value));
            }

            // Yields null for unsupported sources so wildcard expansion can reject rather than omit them.
            private static IEnumerable<TableReferenceWithAlias?> Tables(TableReference reference)
            {
                if (reference is TableReferenceWithAlias aliased) yield return aliased;
                else if (reference is JoinTableReference join)
                {
                    foreach (var table in Tables(join.FirstTableReference)) yield return table;
                    foreach (var table in Tables(join.SecondTableReference)) yield return table;
                }
                else if (reference is JoinParenthesisTableReference parenthesis)
                    foreach (var table in Tables(parenthesis.Join)) yield return table;
                else yield return null;
            }
        }
    }
}

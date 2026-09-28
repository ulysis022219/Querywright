using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Querywright.Core
{
    public sealed class RenameResult
    {
        public string Text { get; }
        public string OldName { get; }
        public int Changes { get; }
        internal RenameResult(string text, string oldName, int changes)
        { Text = text; OldName = oldName; Changes = changes; }
    }

    public sealed class OutlineItem
    {
        public int Offset { get; }
        public int Length { get; }
        public int Line { get; }
        public string Kind { get; }
        public string Target { get; }
        internal OutlineItem(int offset, int length, int line, string kind, string target)
        { Offset = offset; Length = length; Line = line; Kind = kind; Target = target; }
    }

    public static class SqlRefactoring
    {
        public static RenameResult RenameLocalVariable(string sql, int position, string newName)
        {
            if (sql == null) throw new ArgumentNullException(nameof(sql));
            if (position < 0 || position > sql.Length) throw new ArgumentOutOfRangeException(nameof(position));
            if (sql.Length > 1_000_000) throw new ArgumentException("Refactoring input exceeds 1,000,000 characters.");
            var parser = new TSql170Parser(true);
            if (string.IsNullOrWhiteSpace(newName) || !newName.StartsWith("@", StringComparison.Ordinal) || newName.StartsWith("@@", StringComparison.Ordinal) || newName.Length > 128)
                throw new ArgumentException("Enter a local variable name beginning with one @, at most 128 characters.", nameof(newName));
            var nameScript = (TSqlScript)parser.Parse(new StringReader("DECLARE " + newName + " int;"), out var nameErrors);
            if (nameErrors.Count != 0 || nameScript.Batches.Count != 1 || nameScript.Batches[0].Statements.Count != 1 ||
                !(nameScript.Batches[0].Statements[0] is DeclareVariableStatement declaration) || declaration.Declarations.Count != 1 ||
                declaration.Declarations[0].VariableName.Value != newName)
                throw new ArgumentException("Invalid local variable name.", nameof(newName));
            var script = (TSqlScript)parser.Parse(new StringReader(sql), out var errors);
            if (errors.Count != 0) throw new FormatException("Fix SQL syntax errors before refactoring.");
            var batch = script.Batches.FirstOrDefault(b => b.StartOffset <= position && position <= b.StartOffset + b.FragmentLength);
            if (batch == null) throw new InvalidOperationException("Place the caret on a declared local variable.");
            var variables = new Variables();
            batch.Accept(variables);
            var selected = variables.References.FirstOrDefault(v => v.StartOffset <= position && position <= v.StartOffset + v.FragmentLength);
            if (selected == null) throw new InvalidOperationException("Place the caret on a declared local variable, not a formal EXEC parameter.");
            string oldName = sql.Substring(selected.StartOffset, selected.FragmentLength);
            var comparer = StringComparer.OrdinalIgnoreCase;
            if (variables.Declarations.Count(v => comparer.Equals(v.Value, oldName)) != 1 ||
                variables.Parameters.Any(p => comparer.Equals(p, oldName)))
                throw new InvalidOperationException("Rename requires one local declaration. Public procedure/function parameters are not renamed by this command.");
            if (!comparer.Equals(oldName, newName) && (variables.References.Any(v => comparer.Equals(sql.Substring(v.StartOffset, v.FragmentLength), newName)) ||
                variables.Parameters.Any(p => comparer.Equals(p, newName))))
                throw new InvalidOperationException("New name collides with another variable or parameter in this batch.");
            var matches = variables.References.Where(v => comparer.Equals(sql.Substring(v.StartOffset, v.FragmentLength), oldName))
                .GroupBy(v => v.StartOffset).Select(g => g.First()).OrderByDescending(v => v.StartOffset).ToArray();
            var output = new StringBuilder(sql);
            foreach (var reference in matches) output.Remove(reference.StartOffset, reference.FragmentLength).Insert(reference.StartOffset, newName);
            string result = output.ToString();
            parser.Parse(new StringReader(result), out var finalErrors);
            if (finalErrors.Count > 0) throw new InvalidOperationException("Rename produced invalid SQL; original text retained.");
            return new RenameResult(result, oldName, matches.Length);
        }

        public static string AddSemicolons(string sql)
        {
            if (sql == null) throw new ArgumentNullException(nameof(sql));
            if (sql.Length > 1_000_000) throw new ArgumentException("Refactoring input exceeds 1,000,000 characters.");
            var parser = new TSql170Parser(true);
            var script = parser.Parse(new StringReader(sql), out var errors);
            if (errors.Count != 0) throw new FormatException("Fix SQL syntax errors before inserting semicolons.");
            var statements = new Statements();
            script.Accept(statements);
            var tokens = script.ScriptTokenStream;
            var offsets = new SortedSet<int>(statements.Items
                .Where(s => s.LastTokenIndex >= 0 && tokens[s.LastTokenIndex].TokenType != TSqlTokenType.Semicolon)
                .Select(s => tokens[s.LastTokenIndex].Offset + tokens[s.LastTokenIndex].Text.Length));
            var output = new StringBuilder(sql);
            foreach (int offset in offsets.Reverse()) output.Insert(offset, ';');
            string result = output.ToString();
            // Guard: the only token change allowed is added semicolons.
            var after = parser.GetTokenStream(new StringReader(result), out var finalErrors);
            if (finalErrors.Count > 0 || parser.Parse(new StringReader(result), out finalErrors) == null || finalErrors.Count > 0 ||
                !Significant(after).SequenceEqual(Significant(tokens)))
                throw new InvalidOperationException("Semicolon insertion changed SQL structure; original text retained.");
            return result;
        }

        public static string ApplyCasing(string sql, bool upperKeywords = true)
        {
            var parser = new TSql170Parser(true);
            var script = Parse(parser, sql, "changing case");
            var names = new CasingNames();
            script.Accept(names);
            var tokens = script.ScriptTokenStream;
            var edits = new SortedDictionary<int, (TSqlTokenType Type, string Text)>();
            for (int i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.TokenType == TSqlTokenType.QuotedIdentifier || token.TokenType == TSqlTokenType.Variable || token.Text == null ||
                    !Word.IsMatch(token.Text) || (names.Names.Contains(i) && !names.BuiltIns.Contains(i))) continue;
                string text = upperKeywords ? token.Text.ToUpperInvariant() : token.Text.ToLowerInvariant();
                if (text != token.Text) edits[i] = (token.TokenType, text);
            }
            return Rewrite(parser, sql, script, edits, StringComparer.OrdinalIgnoreCase, "Casing changed SQL structure; original text retained.");
        }

        public static string AddBrackets(string sql)
        {
            var parser = new TSql170Parser(true);
            var script = Parse(parser, sql, "adding brackets");
            var names = new ObjectNames();
            script.Accept(names);
            var tokens = script.ScriptTokenStream;
            var edits = new SortedDictionary<int, (TSqlTokenType Type, string Text)>();
            foreach (var name in names.Items.Where(n => n.QuoteType == QuoteType.NotQuoted && n.FirstTokenIndex == n.LastTokenIndex))
            {
                var token = tokens[name.FirstTokenIndex];
                if (token.TokenType == TSqlTokenType.Identifier && !token.Text.StartsWith("#", StringComparison.Ordinal))
                    edits[name.FirstTokenIndex] = (TSqlTokenType.QuotedIdentifier, "[" + token.Text.Replace("]", "]]") + "]");
            }
            return Rewrite(parser, sql, script, edits, StringComparer.Ordinal, "Adding brackets changed SQL structure; original text retained.");
        }

        public static string RemoveBrackets(string sql)
        {
            var parser = new TSql170Parser(true);
            var script = Parse(parser, sql, "removing brackets");
            var identifiers = new AllIdentifiers();
            script.Accept(identifiers);
            var edits = new SortedDictionary<int, (TSqlTokenType Type, string Text)>();
            foreach (var name in identifiers.Items.Where(n => n.QuoteType == QuoteType.SquareBracket && n.FirstTokenIndex == n.LastTokenIndex))
                if (IsRegularIdentifier(parser, name.Value)) edits[name.FirstTokenIndex] = (TSqlTokenType.Identifier, name.Value);
            return Rewrite(parser, sql, script, edits, StringComparer.Ordinal, "Removing brackets changed SQL structure; original text retained.");
        }

        public static string QualifyObjectNames(string sql, string defaultSchema = "dbo")
        {
            if (string.IsNullOrWhiteSpace(defaultSchema) || defaultSchema.Length > 128)
                throw new ArgumentException("Enter a schema name of at most 128 characters.", nameof(defaultSchema));
            var parser = new TSql170Parser(true);
            var script = Parse(parser, sql, "qualifying object names");
            var objects = new UnqualifiedObjects();
            script.Accept(objects);
            var names = StringComparer.OrdinalIgnoreCase;
            string prefix = (IsRegularIdentifier(parser, defaultSchema) ? defaultSchema : "[" + defaultSchema.Replace("]", "]]") + "]") + ".";
            var offsets = new SortedSet<int>(objects.Names
                .Where(n => n.Identifiers.Count == 1 && !n.BaseIdentifier.Value.StartsWith("#", StringComparison.Ordinal) &&
                    !objects.Ctes.Any(c => SqlNavigation.Contains(c.Owner, n.StartOffset) && c.Names.Contains(n.BaseIdentifier.Value, names)))
                .Select(n => n.StartOffset));
            var output = new StringBuilder(sql);
            foreach (int offset in offsets.Reverse()) output.Insert(offset, prefix);
            string result = output.ToString();
            parser.Parse(new StringReader(result), out var finalErrors);
            if (finalErrors.Count > 0) throw new InvalidOperationException("Qualifying produced invalid SQL; original text retained.");
            return result;
        }

        public static RenameResult RenameAlias(string sql, int position, string newName)
        {
            if (sql == null) throw new ArgumentNullException(nameof(sql));
            if (position < 0 || position > sql.Length) throw new ArgumentOutOfRangeException(nameof(position));
            var parser = new TSql170Parser(true);
            const string probe = "SELECT 1 FROM t AS ";
            var nameScript = string.IsNullOrWhiteSpace(newName) || newName.Length > 260 ? null
                : (TSqlScript)parser.Parse(new StringReader(probe + newName + ";"), out var nameErrors) is var parsed && nameErrors.Count == 0 ? parsed : null;
            var parsedName = nameScript != null && nameScript.Batches.Count == 1 && nameScript.Batches[0].Statements.Count == 1 &&
                nameScript.Batches[0].Statements[0] is SelectStatement { QueryExpression: QuerySpecification { FromClause: { TableReferences: { Count: 1 } refs } } } &&
                refs[0] is NamedTableReference { Alias: { } alias } && alias.StartOffset == probe.Length && alias.FragmentLength == newName.Length ? alias : null;
            if (parsedName == null || parsedName.Value.Length == 0 || parsedName.Value.Length > 128 || "#@".IndexOf(parsedName.Value[0]) >= 0)
                throw new ArgumentException("Invalid alias name.", nameof(newName));
            var script = Parse(parser, sql, "refactoring");
            var batch = script.Batches.FirstOrDefault(b => SqlNavigation.Contains(b, position));
            if (batch == null) throw new InvalidOperationException("Place the caret on a table alias.");
            var aliases = new Aliases();
            batch.Accept(aliases);
            var comparer = StringComparer.OrdinalIgnoreCase;
            SqlNavigation.Sources? scope = null;
            TableReferenceWithAlias? table = null;
            foreach (var candidate in aliases.Scopes)
                foreach (var t in candidate.Tables.Where(t => t.Alias != null && SqlNavigation.Contains(t.Alias, position)))
                { scope = candidate; table = t; }
            if (scope == null)
            {
                var use = aliases.Uses.FirstOrDefault(u => SqlNavigation.Contains(u, position));
                if (use == null) throw new InvalidOperationException("Place the caret on a table alias or an alias qualifier.");
                scope = aliases.Resolve(use.Value, use.StartOffset, out table);
                if (scope == null || table?.Alias == null) throw new InvalidOperationException("The qualifier is not a table alias.");
            }
            var definition = table!.Alias!;
            string oldName = definition.Value, newValue = parsedName.Value;
            var targets = aliases.Uses.Where(u => comparer.Equals(u.Value, oldName) && aliases.Resolve(oldName, u.StartOffset, out var t) == scope && t == table).ToList();
            bool Inner(SqlNavigation.Sources? other) => other != null && other != scope && other.Owner.FragmentLength < scope!.Owner.FragmentLength;
            if (!comparer.Equals(oldName, newValue) && (scope.Tables.Any(t => t != table && comparer.Equals(t.Alias?.Value ?? (t as NamedTableReference)?.SchemaObject.BaseIdentifier.Value, newValue)) ||
                aliases.Uses.Any(u => SqlNavigation.Contains(scope.Owner, u.StartOffset) && comparer.Equals(u.Value, newValue) && !Inner(aliases.Resolve(newValue, u.StartOffset, out _))) ||
                targets.Any(u => Inner(aliases.Resolve(newValue, u.StartOffset, out _)))))
                throw new InvalidOperationException("New alias conflicts with another alias or table in scope.");
            targets.Add(definition);
            var edits = targets.GroupBy(i => i.StartOffset).Select(g => g.First()).OrderByDescending(i => i.StartOffset).ToArray();
            var output = new StringBuilder(sql);
            foreach (var identifier in edits) output.Remove(identifier.StartOffset, identifier.FragmentLength).Insert(identifier.StartOffset, newName);
            string result = output.ToString();
            parser.Parse(new StringReader(result), out var finalErrors);
            if (finalErrors.Count > 0) throw new InvalidOperationException("Rename produced invalid SQL; original text retained.");
            return new RenameResult(result, oldName, edits.Length);
        }

        public static IReadOnlyList<OutlineItem> Summarize(string sql)
        {
            var script = Parse(new TSql170Parser(true), sql, "summarizing");
            return script.Batches.SelectMany(b => b.Statements)
                .Select(s => { var (start, length) = SqlNavigation.Span(s); return new OutlineItem(start, length, s.StartLine, Kind(s), Target(s)); }).ToArray();
        }

        public static IReadOnlyList<string> UnusedDeclarations(string sql) => UnusedDeclarationItems(sql).Select(i => i.Target).ToArray();

        /// <summary>Unused variables, table variables and parameters with their declaration spans (Kind "UNUSED").</summary>
        public static IReadOnlyList<OutlineItem> UnusedDeclarationItems(string sql)
        {
            var script = Parse(new TSql170Parser(true), sql, "finding unused declarations");
            var unused = new List<OutlineItem>();
            foreach (var batch in script.Batches)
            {
                var usage = new Usage();
                batch.Accept(usage);
                unused.AddRange(usage.Declared.Where(d => !usage.Used.Contains(d.Value))
                    .Select(d => new OutlineItem(d.StartOffset, d.FragmentLength, d.StartLine, "UNUSED", d.Value)));
            }
            return unused;
        }

        private static string Kind(TSqlStatement statement)
        {
            switch (statement)
            {
                case ExecuteStatement _: return "EXEC";
                case DeclareVariableStatement _: case DeclareTableVariableStatement _: return "DECLARE";
                case SetVariableStatement _: case PredicateSetStatement _: return "SET";
                case BeginEndBlockStatement _: return "BEGIN...END";
                case TryCatchStatement _: return "TRY...CATCH";
                case AlterTableStatement _: return "ALTER TABLE";
            }
            string name = statement.GetType().Name;
            if (name.EndsWith("Statement", StringComparison.Ordinal)) name = name.Substring(0, name.Length - "Statement".Length);
            return Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ").ToUpperInvariant();
        }

        private static string Target(TSqlStatement statement)
        {
            var name = statement switch
            {
                ProcedureStatementBody p => p.ProcedureReference?.Name,
                FunctionStatementBody f => f.Name,
                ViewStatementBody v => v.SchemaObjectName,
                TriggerStatementBody t => t.Name,
                CreateTableStatement c => c.SchemaObjectName,
                AlterTableStatement a => a.SchemaObjectName,
                CreateIndexStatement i => i.OnName,
                DropObjectsStatement d => d.Objects.FirstOrDefault(),
                TruncateTableStatement t => t.TableName,
                ExecuteStatement e => (e.ExecuteSpecification?.ExecutableEntity as ExecutableProcedureReference)?.ProcedureReference?.ProcedureReference?.Name,
                SelectStatement s => s.Into,
                _ => null
            };
            if (name != null) return string.Join(".", name.Identifiers.Select(i => i.Value));
            var (target, from) = statement switch
            {
                InsertStatement i => (i.InsertSpecification?.Target, (FromClause?)null),
                UpdateStatement u => (u.UpdateSpecification?.Target, u.UpdateSpecification?.FromClause),
                DeleteStatement d => (d.DeleteSpecification?.Target, d.DeleteSpecification?.FromClause),
                MergeStatement m => (m.MergeSpecification?.Target, null),
                SelectStatement s => ((TableReference?)null, (s.QueryExpression as QuerySpecification)?.FromClause),
                _ => ((TableReference?)null, (FromClause?)null)
            };
            var sources = from == null ? null : new SqlNavigation.Sources(statement, from);
            if (target is NamedTableReference named && named.SchemaObject.Identifiers.Count == 1)
                target = sources?.Tables.FirstOrDefault(t => StringComparer.OrdinalIgnoreCase.Equals(t.Alias?.Value, named.SchemaObject.BaseIdentifier.Value)) ?? target;
            target = target ?? sources?.Tables.OfType<NamedTableReference>().FirstOrDefault();
            return target switch
            {
                NamedTableReference n => string.Join(".", n.SchemaObject.Identifiers.Select(i => i.Value)),
                VariableTableReference v => v.Variable.Name,
                _ => ""
            };
        }

        private static TSqlScript Parse(TSql170Parser parser, string sql, string action)
        {
            if (sql == null) throw new ArgumentNullException(nameof(sql));
            if (sql.Length > 1_000_000) throw new ArgumentException("Refactoring input exceeds 1,000,000 characters.");
            var script = (TSqlScript)parser.Parse(new StringReader(sql), out var errors);
            if (errors.Count != 0) throw new FormatException("Fix SQL syntax errors before " + action + ".");
            return script;
        }

        private static readonly Regex Word = new Regex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
        private static readonly Regex Regular = new Regex("^[A-Za-z_#][A-Za-z0-9_@$#]*$", RegexOptions.CultureInvariant);
        // Not reserved to the tokenizer, but unbracketed they change the parse (WINDOW clause) or split batches.
        private static readonly HashSet<string> Contextual = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "WINDOW", "GO" };

        private static bool IsRegularIdentifier(TSql170Parser parser, string name)
        {
            if (name.Length > 128 || !Regular.IsMatch(name) || Contextual.Contains(name)) return false;
            var tokens = parser.GetTokenStream(new StringReader(name), out var errors);
            return errors.Count == 0 && tokens.Count(t => t.TokenType != TSqlTokenType.EndOfFile) == 1 && tokens[0].TokenType == TSqlTokenType.Identifier;
        }

        /// <summary>Applies one-token replacements, then requires identical tokens apart from the edits and an identical tree.</summary>
        private static string Rewrite(TSql170Parser parser, string sql, TSqlScript script, SortedDictionary<int, (TSqlTokenType Type, string Text)> edits,
            StringComparer identifierComparer, string failure)
        {
            var tokens = script.ScriptTokenStream;
            var output = new StringBuilder(sql);
            foreach (var edit in edits.Reverse())
                output.Remove(tokens[edit.Key].Offset, tokens[edit.Key].Text.Length).Insert(tokens[edit.Key].Offset, edit.Value.Text);
            string result = output.ToString();
            var reparsed = parser.Parse(new StringReader(result), out var errors);
            var after = reparsed?.ScriptTokenStream;
            if (errors.Count != 0 || reparsed == null || after == null || after.Count != tokens.Count ||
                Enumerable.Range(0, tokens.Count).Any(i => edits.TryGetValue(i, out var e)
                    ? after[i].TokenType != e.Type || after[i].Text != e.Text
                    : after[i].TokenType != tokens[i].TokenType || after[i].Text != tokens[i].Text) ||
                !Shape(script).SequenceEqual(Shape(reparsed), identifierComparer))
                throw new InvalidOperationException(failure);
            return result;
        }

        private static IEnumerable<string> Shape(TSqlFragment fragment)
        {
            var shape = new ShapeVisitor();
            fragment.Accept(shape);
            return shape.Items;
        }

        private static readonly HashSet<string> BuiltInFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "COUNT", "COUNT_BIG", "SUM", "AVG", "MIN", "MAX", "STDEV", "STDEVP", "VAR", "VARP", "STRING_AGG", "APPROX_COUNT_DISTINCT", "GROUPING", "GROUPING_ID",
            "CHECKSUM_AGG", "ROW_NUMBER", "RANK", "DENSE_RANK", "NTILE", "LAG", "LEAD", "FIRST_VALUE", "LAST_VALUE", "PERCENT_RANK", "CUME_DIST",
            "PERCENTILE_CONT", "PERCENTILE_DISC", "GETDATE", "GETUTCDATE", "SYSDATETIME", "SYSUTCDATETIME", "SYSDATETIMEOFFSET", "DATEADD", "DATEDIFF",
            "DATEDIFF_BIG", "DATEPART", "DATENAME", "DATEFROMPARTS", "DATETIME2FROMPARTS", "DATETIMEFROMPARTS", "TIMEFROMPARTS", "EOMONTH", "DAY", "MONTH",
            "YEAR", "ISDATE", "SWITCHOFFSET", "TODATETIMEOFFSET", "DATE_BUCKET", "DATETRUNC", "ISNULL", "ISNUMERIC", "CHOOSE", "NEWID", "NEWSEQUENTIALID",
            "SCOPE_IDENTITY", "IDENT_CURRENT", "OBJECT_ID", "OBJECT_NAME", "OBJECT_SCHEMA_NAME", "SCHEMA_NAME", "SCHEMA_ID", "DB_NAME", "DB_ID", "SUSER_SNAME",
            "SUSER_NAME", "USER_NAME", "HOST_NAME", "APP_NAME", "ERROR_MESSAGE", "ERROR_NUMBER", "ERROR_LINE", "ERROR_SEVERITY", "ERROR_STATE",
            "ERROR_PROCEDURE", "XACT_STATE", "OBJECTPROPERTY", "COLUMNPROPERTY", "SERVERPROPERTY", "DATABASEPROPERTYEX", "TYPE_NAME", "TYPE_ID", "COL_NAME",
            "LEN", "DATALENGTH", "SUBSTRING", "UPPER", "LOWER", "LTRIM", "RTRIM", "TRIM", "REPLACE", "REPLICATE", "REVERSE", "STUFF", "CHARINDEX",
            "PATINDEX", "CONCAT", "CONCAT_WS", "FORMAT", "STR", "SPACE", "QUOTENAME", "CHAR", "NCHAR", "ASCII", "UNICODE", "SOUNDEX", "DIFFERENCE",
            "STRING_ESCAPE", "TRANSLATE", "ABS", "CEILING", "FLOOR", "ROUND", "POWER", "SQRT", "SQUARE", "EXP", "LOG", "LOG10", "SIGN", "RAND", "PI", "SIN",
            "COS", "TAN", "ASIN", "ACOS", "ATAN", "ATN2", "COT", "DEGREES", "RADIANS", "GREATEST", "LEAST", "JSON_VALUE", "JSON_QUERY", "JSON_MODIFY",
            "ISJSON", "CHECKSUM", "BINARY_CHECKSUM", "HASHBYTES", "COMPRESS", "DECOMPRESS", "CURSOR_STATUS", "FORMATMESSAGE", "SESSION_CONTEXT"
        };

        private sealed class ShapeVisitor : TSqlFragmentVisitor
        {
            internal readonly List<string> Items = new List<string>();
            public override void Visit(TSqlFragment node) { Items.Add(node is Identifier id ? "=" + id.Value : node.GetType().Name); }
        }

        private sealed class CasingNames : TSqlFragmentVisitor
        {
            internal readonly HashSet<int> Names = new HashSet<int>(), BuiltIns = new HashSet<int>();
            private void Mark(HashSet<int> set, TSqlFragment node) { for (int i = node.FirstTokenIndex; i <= node.LastTokenIndex; i++) set.Add(i); }
            public override void Visit(Identifier node) { Mark(Names, node); }
            public override void Visit(IdentifierLiteral node) { Mark(Names, node); }
            public override void Visit(SqlDataTypeReference node) { if (node.Name.Identifiers.Count == 1) Mark(BuiltIns, node.Name); }
            public override void Visit(XmlDataTypeReference node) { if (node.Name.Identifiers.Count == 1) Mark(BuiltIns, node.Name); }
            public override void Visit(GlobalFunctionTableReference node) { Mark(BuiltIns, node.Name); }
            public override void Visit(FunctionCall node)
            {
                if (node.CallTarget != null || !BuiltInFunctions.Contains(node.FunctionName.Value)) return;
                Mark(BuiltIns, node.FunctionName);
                if (node.FunctionName.Value.StartsWith("DATE", StringComparison.OrdinalIgnoreCase) && node.Parameters.FirstOrDefault() is IdentifierLiteral datepart)
                    Mark(BuiltIns, datepart);
            }
        }

        private sealed class ObjectNames : TSqlFragmentVisitor
        {
            internal readonly List<Identifier> Items = new List<Identifier>();
            private void Add(Identifier? identifier) { if (identifier != null) Items.Add(identifier); }
            private void Add(MultiPartIdentifier? name) { if (name != null) Items.AddRange(name.Identifiers); }
            public override void Visit(NamedTableReference node) { Add(node.SchemaObject); }
            public override void Visit(SchemaObjectFunctionTableReference node) { Add(node.SchemaObject); }
            public override void Visit(TableReferenceWithAlias node) { Add(node.Alias); }
            public override void Visit(TableReferenceWithAliasAndColumns node) { foreach (var c in node.Columns) Add(c); }
            public override void Visit(ColumnReferenceExpression node) { if (node.ColumnType == ColumnType.Regular) Add(node.MultiPartIdentifier); }
            public override void Visit(SelectStarExpression node) { Add(node.Qualifier); }
            public override void Visit(SelectScalarExpression node) { Add(node.ColumnName?.Identifier); }
            public override void Visit(CommonTableExpression node) { Add(node.ExpressionName); foreach (var c in node.Columns) Add(c); }
            public override void Visit(ColumnDefinition node) { Add(node.ColumnIdentifier); }
            public override void Visit(ProcedureReference node) { Add(node.Name); }
            public override void Visit(FunctionStatementBody node) { Add(node.Name); }
            public override void Visit(ViewStatementBody node) { Add(node.SchemaObjectName); }
            public override void Visit(CreateTableStatement node) { Add(node.SchemaObjectName); }
        }

        private sealed class AllIdentifiers : TSqlFragmentVisitor
        {
            internal readonly List<Identifier> Items = new List<Identifier>();
            public override void Visit(Identifier node) { Items.Add(node); }
        }

        private sealed class UnqualifiedObjects : TSqlFragmentVisitor
        {
            internal readonly List<SchemaObjectName> Names = new List<SchemaObjectName>();
            internal readonly List<(TSqlFragment Owner, string[] Names)> Ctes = new List<(TSqlFragment, string[])>();
            private readonly HashSet<TableReference> aliasTargets = new HashSet<TableReference>();
            private static readonly HashSet<string> BuiltInTableFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "STRING_SPLIT", "GENERATE_SERIES", "OPENJSON", "OPENROWSET", "OPENQUERY", "OPENXML", "OPENDATASOURCE", "CHANGETABLE", "CONTAINSTABLE", "FREETEXTTABLE" };
            public override void Visit(StatementWithCtesAndXmlNamespaces node)
            {
                if (node.WithCtesAndXmlNamespaces != null)
                    Ctes.Add((node, node.WithCtesAndXmlNamespaces.CommonTableExpressions.Select(c => c.ExpressionName.Value).ToArray()));
            }
            // UPDATE p ... FROM dbo.People p: the target names an alias, not an object.
            public override void Visit(UpdateSpecification node) { AliasTarget(node.Target, node.FromClause); }
            public override void Visit(DeleteSpecification node) { AliasTarget(node.Target, node.FromClause); }
            private void AliasTarget(TableReference target, FromClause? from)
            {
                if (from != null && target is NamedTableReference named && named.SchemaObject.Identifiers.Count == 1 &&
                    new SqlNavigation.Sources(from, from).Tables.Any(t => StringComparer.OrdinalIgnoreCase.Equals(t.Alias?.Value, named.SchemaObject.BaseIdentifier.Value)))
                    aliasTargets.Add(target);
            }
            public override void Visit(NamedTableReference node) { if (!aliasTargets.Contains(node)) Names.Add(node.SchemaObject); }
            public override void Visit(SchemaObjectFunctionTableReference node)
            {
                if (!BuiltInTableFunctions.Contains(node.SchemaObject.BaseIdentifier.Value)) Names.Add(node.SchemaObject);
            }
            public override void Visit(ExecutableProcedureReference node)
            {
                var name = node.ProcedureReference?.ProcedureReference?.Name;
                if (name != null && !name.BaseIdentifier.Value.StartsWith("sp_", StringComparison.OrdinalIgnoreCase) &&
                    !name.BaseIdentifier.Value.StartsWith("xp_", StringComparison.OrdinalIgnoreCase)) Names.Add(name);
            }
        }

        private sealed class Aliases : TSqlFragmentVisitor
        {
            internal readonly List<SqlNavigation.Sources> Scopes = new List<SqlNavigation.Sources>();
            internal readonly List<Identifier> Uses = new List<Identifier>();
            internal SqlNavigation.Sources? Resolve(string name, int position, out TableReferenceWithAlias? table)
            {
                var names = StringComparer.OrdinalIgnoreCase;
                foreach (var scope in Scopes.Where(s => SqlNavigation.Contains(s.Owner, position)).OrderBy(s => s.Owner.FragmentLength))
                    foreach (var t in scope.Tables)
                        if (t.Alias != null ? names.Equals(t.Alias.Value, name) : t is NamedTableReference n && names.Equals(n.SchemaObject.BaseIdentifier.Value, name))
                        { table = t; return scope; }
                table = null;
                return null;
            }
            public override void Visit(QuerySpecification node) { if (node.FromClause != null) Scopes.Add(new SqlNavigation.Sources(node, node.FromClause)); }
            public override void Visit(UpdateSpecification node) { Modification(node, node.Target, node.FromClause); }
            public override void Visit(DeleteSpecification node) { Modification(node, node.Target, node.FromClause); }
            private void Modification(TSqlFragment node, TableReference target, FromClause? from)
            {
                if (from == null) return;
                Scopes.Add(new SqlNavigation.Sources(node, from));
                if (target is NamedTableReference named && named.Alias == null && named.SchemaObject.Identifiers.Count == 1) Uses.Add(named.SchemaObject.BaseIdentifier);
            }
            public override void Visit(ColumnReferenceExpression node)
            {
                var ids = node.MultiPartIdentifier?.Identifiers;
                if (ids != null && ids.Count == 2) Uses.Add(ids[0]);
            }
            public override void Visit(SelectStarExpression node) { if (node.Qualifier?.Identifiers.Count == 1) Uses.Add(node.Qualifier.Identifiers[0]); }
        }

        private sealed class Usage : TSqlFragmentVisitor
        {
            internal readonly List<Identifier> Declared = new List<Identifier>();
            internal readonly HashSet<string> Used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public override void Visit(DeclareVariableElement node) { Declared.Add(node.VariableName); }
            public override void Visit(DeclareTableVariableBody node) { Declared.Add(node.VariableName); }
            public override void Visit(VariableReference node) { Used.Add(node.Name); }
            public override void Visit(VariableTableReference node) { Used.Add(node.Variable.Name); }
            // RETURNS @t TABLE is the function's result, used by returning.
            public override void ExplicitVisit(TableValuedFunctionReturnType node) { }
            public override void ExplicitVisit(ExecuteParameter node) { node.ParameterValue?.Accept(this); }
        }

        private static IEnumerable<string> Significant(IList<TSqlParserToken> tokens) =>
            tokens.Where(t => t.TokenType != TSqlTokenType.Semicolon && t.TokenType != TSqlTokenType.WhiteSpace).Select(t => t.Text);

        private sealed class Statements : TSqlFragmentVisitor
        {
            internal readonly List<TSqlStatement> Items = new List<TSqlStatement>();
            public override void Visit(TSqlStatement node)
            {
                // ponytail: labels end with ':'; WITH CTE prefixes and GO are not statements.
                if (!(node is LabelStatement)) Items.Add(node);
            }
        }

        private sealed class Variables : TSqlFragmentVisitor
        {
            internal readonly List<TSqlFragment> References = new List<TSqlFragment>();
            internal readonly List<Identifier> Declarations = new List<Identifier>();
            internal readonly List<string> Parameters = new List<string>();
            public override void Visit(VariableReference node) { References.Add(node); }
            public override void Visit(DeclareVariableElement node)
            {
                if (node is ProcedureParameter) return;
                Declarations.Add(node.VariableName); References.Add(node.VariableName);
            }
            public override void Visit(DeclareTableVariableBody node)
            { Declarations.Add(node.VariableName); References.Add(node.VariableName); }
            public override void ExplicitVisit(ProcedureParameter node)
            { Parameters.Add(node.VariableName.Value); }
            public override void ExplicitVisit(ExecuteParameter node)
            {
                // EXEC's left side names the called procedure's parameter, not the caller's variable.
                node.ParameterValue?.Accept(this);
            }
        }
    }
}

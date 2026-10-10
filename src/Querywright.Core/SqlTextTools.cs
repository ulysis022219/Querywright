using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Querywright.Core
{
    /// <summary>Selection-level text commands: list building, commenting, unformatting and EXEC/INSERT rewrites.</summary>
    public static class SqlTextTools
    {
        private const int MaxLength = 1_000_000;

        /// <summary>Turns one value per line into a comma-separated list, optionally quoting each value.</summary>
        public static string AddCommas(string text, bool quote = false)
        {
            Guard(text);
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            int last = Array.FindLastIndex(lines, l => l.Trim().Length > 0);
            if (last < 0) throw new InvalidOperationException("Select the lines to turn into a list.");
            for (int i = 0; i <= last; i++)
            {
                string value = lines[i].Trim().TrimEnd(',').TrimEnd();
                if (value.Length == 0) continue;
                string indent = lines[i].Substring(0, lines[i].Length - lines[i].TrimStart().Length);
                bool quoted = value.Length >= 2 && value[value.Length - 1] == '\'' && (value[0] == '\'' || value.StartsWith("N'", StringComparison.OrdinalIgnoreCase));
                if (quote && !quoted) value = "'" + value.Replace("'", "''") + "'";
                lines[i] = indent + value + (i < last ? "," : "");
            }
            return string.Join(newline, lines);
        }

        /// <summary>Prefixes every non-blank line with -- at the shallowest indentation in the selection.</summary>
        public static string Comment(string text)
        {
            Guard(text);
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var filled = lines.Where(l => l.Trim().Length > 0).ToArray();
            if (filled.Length == 0) throw new InvalidOperationException("Select the lines to comment out.");
            int indent = filled.Min(l => l.Length - l.TrimStart(' ', '\t').Length);
            return string.Join(newline, lines.Select(l => l.Trim().Length == 0 ? l : l.Substring(0, indent) + "--" + l.Substring(indent)));
        }

        /// <summary>Removes one leading -- from every line, or the delimiters of a selection that is one block comment.</summary>
        public static string Uncomment(string text)
        {
            Guard(text);
            string trimmed = text.Trim();
            if (trimmed.StartsWith("/*", StringComparison.Ordinal) && trimmed.EndsWith("*/", StringComparison.Ordinal) && trimmed.Length >= 4 &&
                trimmed.IndexOf("*/", StringComparison.Ordinal) == trimmed.Length - 2)
            {
                int open = text.IndexOf("/*", StringComparison.Ordinal), close = text.LastIndexOf("*/", StringComparison.Ordinal);
                return text.Substring(0, open) + text.Substring(open + 2, close - open - 2) + text.Substring(close + 2);
            }
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            if (!lines.Any(l => l.TrimStart().StartsWith("--", StringComparison.Ordinal))) throw new InvalidOperationException("The selection has no commented lines.");
            return string.Join(newline, lines.Select(l =>
            {
                int indent = l.Length - l.TrimStart(' ', '\t').Length;
                return string.CompareOrdinal(l, indent, "--", 0, 2) == 0 ? l.Substring(0, indent) + l.Substring(indent + 2) : l;
            }));
        }

        /// <summary>Deletes every comment. Lines left holding only a comment go with it; code tokens are untouched.</summary>
        public static string RemoveComments(string sql)
        {
            Guard(sql);
            var parser = new TSql170Parser(true);
            var tokens = parser.GetTokenStream(new StringReader(sql), out var errors);
            if (errors.Count > 0) throw new FormatException("Cannot tokenize SQL to remove comments.");
            var output = new StringBuilder(sql);
            int removed = 0;
            for (int i = tokens.Count - 1; i >= 0; i--)
            {
                var token = tokens[i];
                if (token.TokenType != TSqlTokenType.SingleLineComment && token.TokenType != TSqlTokenType.MultilineComment) continue;
                removed++;
                int start = token.Offset, end = token.Offset + token.Text.Length;
                int lineStart = start;
                while (lineStart > 0 && (sql[lineStart - 1] == ' ' || sql[lineStart - 1] == '\t')) lineStart--;
                bool opensLine = lineStart == 0 || sql[lineStart - 1] == '\n';
                int lineEnd = end;
                while (lineEnd < sql.Length && (sql[lineEnd] == ' ' || sql[lineEnd] == '\t')) lineEnd++;
                bool closesLine = lineEnd == sql.Length || sql[lineEnd] == '\r' || sql[lineEnd] == '\n';
                if (opensLine && closesLine)
                {
                    // The comment owns its line: take the line break too.
                    if (lineEnd < sql.Length && sql[lineEnd] == '\r') lineEnd++;
                    if (lineEnd < sql.Length && sql[lineEnd] == '\n') lineEnd++;
                    output.Remove(lineStart, lineEnd - lineStart);
                }
                else if (closesLine) output.Remove(lineStart, end - lineStart);
                else if (lineStart < start && lineEnd > end) output.Remove(lineStart, end - lineStart);
                else output.Remove(start, end - start).Insert(start, opensLine || lineStart < start || lineEnd > end ? "" : " ");
            }
            if (removed == 0) throw new InvalidOperationException("The SQL has no comments.");
            string result = output.ToString();
            if (!Code(tokens).SequenceEqual(Code(parser.GetTokenStream(new StringReader(result), out var after))) || after.Count > 0)
                throw new InvalidOperationException("Removing comments would change the SQL; original text retained.");
            return result;
        }

        /// <summary>Collapses layout to single spaces. Line comments keep their line break and GO keeps its own line.</summary>
        public static string Unformat(string sql)
        {
            Guard(sql);
            var parser = new TSql170Parser(true);
            var tokens = parser.GetTokenStream(new StringReader(sql), out var errors);
            if (errors.Count > 0) throw new FormatException("Cannot tokenize SQL to unformat.");
            var output = new StringBuilder(sql.Length);
            bool breakNext = false, inGo = false;
            foreach (var token in tokens)
            {
                if (token.TokenType == TSqlTokenType.EndOfFile) break;
                if (token.TokenType == TSqlTokenType.WhiteSpace)
                {
                    if (inGo && token.Text.IndexOf('\n') >= 0) { inGo = false; breakNext = true; }
                    else if (!breakNext && output.Length > 0 && output[output.Length - 1] != ' ' && output[output.Length - 1] != '\n') output.Append(' ');
                    continue;
                }
                if (token.TokenType == TSqlTokenType.Go) { breakNext = output.Length > 0; inGo = true; }
                if (breakNext)
                {
                    while (output.Length > 0 && output[output.Length - 1] == ' ') output.Length--;
                    output.Append("\r\n");
                    breakNext = false;
                }
                output.Append(token.Text);
                if (token.TokenType == TSqlTokenType.SingleLineComment) breakNext = true;
            }
            string result = output.ToString().TrimEnd(' ');
            if (!Code(tokens).SequenceEqual(Code(parser.GetTokenStream(new StringReader(result), out var after))) || after.Count > 0)
                throw new InvalidOperationException("Unformatting would change the SQL; original text retained.");
            return result;
        }

        /// <summary>Replaces the sp_executesql call at the caret with DECLAREs for its parameters followed by the statement text.</summary>
        public static string ExecuteSqlToPlain(string sql, int position, string newline = "\r\n")
        {
            var parser = new TSql170Parser(true);
            var (statement, call) = ExecAt(parser, sql, position, "converting sp_executesql");
            if (!string.Equals(call.ProcedureReference.ProcedureReference?.Name.BaseIdentifier.Value, "sp_executesql", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Place the caret on an EXEC sp_executesql statement.");
            var arguments = call.Parameters.ToList();
            StringLiteral? Take(string name)
            {
                var argument = arguments.FirstOrDefault(a => string.Equals(a.Variable?.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? arguments.FirstOrDefault(a => a.Variable == null);
                if (argument == null) return null;
                // A positional argument may not follow a named one, so the first unnamed one is always the next in order.
                arguments.Remove(argument);
                return argument.ParameterValue as StringLiteral ?? throw new InvalidOperationException("sp_executesql must be called with string literals to convert it.");
            }
            var body = Take("@stmt") ?? Take("@statement") ?? throw new InvalidOperationException("sp_executesql has no statement argument.");
            var definition = arguments.Count == 0 ? null : Take("@params");
            var parameters = new List<Parameter>();
            if (definition != null && definition.Value.Trim().Length > 0)
            {
                string header = "CREATE PROCEDURE p " + definition.Value + " AS RETURN;";
                var script = (TSqlScript)parser.ParseSafe(header, out var errors);
                if (errors.Count != 0 || !(script.Batches.SelectMany(b => b.Statements).FirstOrDefault() is CreateProcedureStatement declared))
                    throw new InvalidOperationException("The sp_executesql parameter definition could not be read.");
                parameters.AddRange(declared.Parameters.Select(p => new Parameter(p, header)));
            }
            string text = Declarations(sql, parameters, arguments, newline) + body.Value.Trim();
            if (!text.EndsWith(";", StringComparison.Ordinal)) text += ";";
            return Replace(parser, sql, statement, text);
        }

        /// <summary>Replaces the EXEC at the caret with the body of the procedure, given its CREATE/ALTER script.</summary>
        public static string InlineExec(string sql, int position, string procedureDefinition, string newline = "\r\n")
        {
            if (procedureDefinition == null) throw new ArgumentNullException(nameof(procedureDefinition));
            var parser = new TSql170Parser(true);
            var (statement, call) = ExecAt(parser, sql, position, "inlining EXEC");
            var script = (TSqlScript)parser.ParseSafe(procedureDefinition, out var errors);
            var procedure = errors.Count != 0 ? null : script.Batches.SelectMany(b => b.Statements).OfType<ProcedureStatementBody>().FirstOrDefault();
            if (procedure?.StatementList == null || procedure.StatementList.Statements.Count == 0)
                throw new InvalidOperationException("The procedure definition could not be read.");
            IList<TSqlStatement> statements = procedure.StatementList.Statements;
            if (statements.Count == 1 && statements[0] is BeginEndBlockStatement block && block.StatementList.Statements.Count > 0) statements = block.StatementList.Statements;
            var returns = new Returns();
            foreach (var s in statements) s.Accept(returns);
            // ponytail: RETURN would end the caller's batch, not just the inlined body; rewriting control flow is out of scope.
            if (returns.Found) throw new InvalidOperationException("The procedure uses RETURN, so its body cannot be inlined as-is.");
            var first = statements[0];
            var (lastStart, lastLength) = SqlNavigation.Span(statements[statements.Count - 1]);
            string body = procedureDefinition.Substring(first.StartOffset, lastStart + lastLength - first.StartOffset);
            var parameters = procedure.Parameters.Select(p => new Parameter(p, procedureDefinition)).ToList();
            string text = Declarations(sql, parameters, call.Parameters.ToList(), newline) + body;
            if (!text.EndsWith(";", StringComparison.Ordinal) && !text.EndsWith("END", StringComparison.OrdinalIgnoreCase)) text += ";";
            return Replace(parser, sql, statement, text);
        }

        /// <summary>Name of the procedure called by the EXEC at the caret, as written (for looking up its definition).</summary>
        public static string ExecTarget(string sql, int position)
        {
            var (_, call) = ExecAt(new TSql170Parser(true), sql, position, "inlining EXEC");
            var name = call.ProcedureReference.ProcedureReference?.Name ?? throw new InvalidOperationException("EXEC of a procedure variable cannot be inlined.");
            return string.Join(".", name.Identifiers.Select(i => i.Value));
        }

        /// <summary>Rewrites the single-row INSERT ... VALUES at the caret as an UPDATE with a placeholder predicate that matches no rows.</summary>
        public static string InsertToUpdate(string sql, int position, string newline = "\r\n")
        {
            Guard(sql);
            if (position < 0 || position > sql.Length) throw new ArgumentOutOfRangeException(nameof(position));
            var parser = new TSql170Parser(true);
            var script = (TSqlScript)parser.ParseSafe(sql, out var errors);
            if (errors.Count != 0) throw new FormatException("Fix SQL syntax errors before converting INSERT.");
            var statements = script.Batches.SelectMany(b => b.Statements).OfType<InsertStatement>().ToArray();
            var insert = statements.LastOrDefault(s => s.StartOffset <= position && position <= s.StartOffset + s.FragmentLength + 1) ?? (statements.Length == 1 ? statements[0] : null);
            if (insert == null) throw new InvalidOperationException("Place the caret on an INSERT statement.");
            var spec = insert.InsertSpecification;
            if (spec.Columns.Count == 0) throw new InvalidOperationException("The INSERT needs a column list to become an UPDATE.");
            if (!(spec.InsertSource is ValuesInsertSource values) || values.IsDefaultValues || values.RowValues.Count != 1)
                throw new InvalidOperationException("Only an INSERT with one VALUES row can become an UPDATE.");
            var row = values.RowValues[0].ColumnValues;
            if (row.Count != spec.Columns.Count) throw new InvalidOperationException("The INSERT column list and VALUES row differ in length.");
            string Text(TSqlFragment node) => sql.Substring(node.StartOffset, node.FragmentLength);
            var builder = new StringBuilder("UPDATE ").Append(Text(spec.Target)).Append(newline).Append("SET ");
            for (int i = 0; i < row.Count; i++)
                builder.Append(i == 0 ? "" : "," + newline + "    ").Append(Text(spec.Columns[i])).Append(" = ").Append(Text(row[i]));
            builder.Append(newline).Append("WHERE 1 = 0 /* replace with the key predicate */;");
            return Replace(parser, sql, insert, builder.ToString());
        }

        private static void Guard(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            if (text.Length > MaxLength) throw new ArgumentException("Input exceeds 1,000,000 characters.");
        }

        private static IEnumerable<string> Code(IList<TSqlParserToken> tokens) =>
            tokens.Where(t => t.TokenType != TSqlTokenType.WhiteSpace && t.TokenType != TSqlTokenType.SingleLineComment &&
                t.TokenType != TSqlTokenType.MultilineComment && t.TokenType != TSqlTokenType.EndOfFile).Select(t => t.Text);

        private static (ExecuteStatement Statement, ExecutableProcedureReference Call) ExecAt(TSql170Parser parser, string sql, int position, string action)
        {
            Guard(sql);
            if (position < 0 || position > sql.Length) throw new ArgumentOutOfRangeException(nameof(position));
            var script = (TSqlScript)parser.ParseSafe(sql, out var errors);
            if (errors.Count != 0) throw new FormatException("Fix SQL syntax errors before " + action + ".");
            var finder = new Executes();
            script.Accept(finder);
            var statement = finder.Found.LastOrDefault(s => s.StartOffset <= position && position <= s.StartOffset + s.FragmentLength + 1)
                ?? (finder.Found.Count == 1 ? finder.Found[0] : null);
            if (statement == null || !(statement.ExecuteSpecification.ExecutableEntity is ExecutableProcedureReference call))
                throw new InvalidOperationException("Place the caret on an EXEC of a stored procedure.");
            if (statement.ExecuteSpecification.Variable != null) throw new InvalidOperationException("An EXEC that captures a return value cannot be replaced by its body.");
            return (statement, call);
        }

        private static string Declarations(string sql, IList<Parameter> parameters, IList<ExecuteParameter> arguments, string newline)
        {
            var comparer = StringComparer.OrdinalIgnoreCase;
            if (arguments.Count > parameters.Count) throw new InvalidOperationException("The EXEC passes more arguments than the definition declares.");
            var lines = new List<string>();
            for (int i = 0; i < parameters.Count; i++)
            {
                var parameter = parameters[i];
                var argument = arguments.FirstOrDefault(a => comparer.Equals(a.Variable?.Name, parameter.Name))
                    ?? (i < arguments.Count && arguments[i].Variable == null ? arguments[i] : null);
                string? value = argument?.ParameterValue == null ? parameter.Default : sql.Substring(argument.ParameterValue.StartOffset, argument.ParameterValue.FragmentLength);
                // The caller's variable already has this name: the inlined text reads and writes it directly.
                if (value != null && comparer.Equals(value, parameter.Name)) continue;
                if (argument != null && argument.IsOutput)
                    throw new InvalidOperationException("OUTPUT argument " + value + " has a different name from parameter " + parameter.Name + "; rename one first.");
                lines.Add("DECLARE " + parameter.Name + " " + parameter.Type + (value == null ? "" : " = " + value) + ";");
            }
            if (arguments.Any(a => a.Variable != null && !parameters.Any(p => comparer.Equals(p.Name, a.Variable.Name))))
                throw new InvalidOperationException("The EXEC names a parameter the definition does not declare.");
            return lines.Count == 0 ? "" : string.Join(newline, lines) + newline;
        }

        private static string Replace(TSql170Parser parser, string sql, TSqlStatement statement, string text)
        {
            var (start, length) = SqlNavigation.Span(statement);
            string result = sql.Substring(0, start) + text + sql.Substring(start + length);
            parser.ParseSafe(result, out var errors);
            if (errors.Count > 0) throw new InvalidOperationException("The rewrite produced invalid SQL; original text retained.");
            return result;
        }

        private sealed class Parameter
        {
            internal readonly string Name, Type;
            internal readonly string? Default;
            internal Parameter(ProcedureParameter parameter, string source)
            {
                Name = parameter.VariableName.Value;
                Type = source.Substring(parameter.DataType.StartOffset, parameter.DataType.FragmentLength);
                Default = parameter.Value == null ? null : source.Substring(parameter.Value.StartOffset, parameter.Value.FragmentLength);
            }
        }

        private sealed class Executes : TSqlFragmentVisitor
        {
            internal readonly List<ExecuteStatement> Found = new List<ExecuteStatement>();
            public override void Visit(ExecuteStatement node) => Found.Add(node);
        }

        private sealed class Returns : TSqlFragmentVisitor
        {
            internal bool Found;
            public override void Visit(ReturnStatement node) => Found = true;
        }
    }
}

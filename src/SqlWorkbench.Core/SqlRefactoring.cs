using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlWorkbench.Core
{
    public sealed class RenameResult
    {
        public string Text { get; }
        public string OldName { get; }
        public int Changes { get; }
        internal RenameResult(string text, string oldName, int changes)
        { Text = text; OldName = oldName; Changes = changes; }
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

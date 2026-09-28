using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlWorkbench.Core
{
    public sealed class SqlDiagnostic
    {
        public string Rule { get; }
        public string Message { get; }
        public int Offset { get; }
        public int Length { get; }
        public int Line { get; }
        public int Column { get; }

        internal SqlDiagnostic(string rule, string message, int offset, int length, int line, int column)
        {
            Rule = rule; Message = message; Offset = offset; Length = length; Line = line; Column = column;
        }
    }

    public sealed class AnalysisResult
    {
        public bool Parsed { get; }
        public IReadOnlyList<SqlDiagnostic> Diagnostics { get; }
        internal AnalysisResult(bool parsed, IEnumerable<SqlDiagnostic> diagnostics)
        {
            Parsed = parsed;
            Diagnostics = diagnostics.OrderBy(d => d.Offset).ThenBy(d => d.Rule).ToArray();
        }
    }

    public static class SqlAnalysis
    {
        public static AnalysisResult Analyze(string sql, CancellationToken cancellationToken = default, WorkbenchSettings? settings = null)
        {
            if (sql == null) throw new ArgumentNullException(nameof(sql));
            if (sql.Length > 1_000_000) throw new ArgumentException("Analysis input exceeds 1,000,000 characters.", nameof(sql));
            cancellationToken.ThrowIfCancellationRequested();
            // ponytail: SQL Server 2025 grammar with QUOTED_IDENTIFIER ON; add per-connection dialect settings before live analysis.
            var parser = new TSql170Parser(true);
            var fragment = parser.Parse(new StringReader(sql), out var errors);
            cancellationToken.ThrowIfCancellationRequested();
            if (errors.Count > 0)
                return new AnalysisResult(false, errors.Select(e => new SqlDiagnostic("PARSE" + e.Number,
                    e.Message, e.Offset, e.Offset < sql.Length ? 1 : 0, e.Line, e.Column)));
            var visitor = new Rules(cancellationToken);
            fragment.Accept(visitor);
            return new AnalysisResult(true, visitor.Diagnostics.Where(d => settings == null || settings.Severity(d.Rule) != RuleSeverity.Disabled));
        }

        private sealed class Rules : TSqlFragmentVisitor
        {
            internal readonly List<SqlDiagnostic> Diagnostics = new List<SqlDiagnostic>();
            private readonly CancellationToken cancellation;
            internal Rules(CancellationToken cancellation) { this.cancellation = cancellation; }
            private void Add(string rule, string message, TSqlFragment node)
            {
                cancellation.ThrowIfCancellationRequested();
                Diagnostics.Add(new SqlDiagnostic(rule, message, node.StartOffset, node.FragmentLength, node.StartLine, node.StartColumn));
            }
            public override void Visit(SelectStarExpression node)
            {
                Add("SW001", "Specify output columns instead of a wildcard.", node);
            }
            public override void Visit(InsertSpecification node)
            {
                if (node.Columns.Count == 0 && !(node.InsertSource is ValuesInsertSource values && values.IsDefaultValues))
                    Add("SW002", "Specify target columns so insertion does not depend on table column order.", node);
            }
            public override void Visit(BooleanComparisonExpression node)
            {
                if (IsNull(node.FirstExpression) || IsNull(node.SecondExpression))
                    Add("SW003", "Use IS NULL or IS NOT NULL instead of comparing with NULL.", node);
            }
            private static bool IsNull(ScalarExpression expression)
            {
                while (expression is ParenthesisExpression parenthesis) expression = parenthesis.Expression;
                return expression is NullLiteral;
            }
            public override void Visit(SearchedCaseExpression node)
            {
                if (node.ElseExpression == null)
                    Add("SW004", "CASE has no ELSE; unmatched rows produce NULL.", node);
            }
            public override void Visit(SimpleCaseExpression node)
            {
                if (node.ElseExpression == null)
                    Add("SW004", "CASE has no ELSE; unmatched values produce NULL.", node);
            }
        }
    }
}

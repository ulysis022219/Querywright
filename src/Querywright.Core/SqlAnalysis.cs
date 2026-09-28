using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Querywright.Core
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
            public override void Visit(DeleteSpecification node)
            {
                if (node.WhereClause == null) Add("SW005", "DELETE has no WHERE clause and removes every row.", node);
            }
            public override void Visit(UpdateSpecification node)
            {
                if (node.WhereClause == null) Add("SW006", "UPDATE has no WHERE clause and changes every row.", node);
            }
            public override void Visit(ExpressionWithSortOrder node)
            {
                if (node.Expression is Literal) Add("SW007", "ORDER BY a constant or column ordinal; name the column instead.", node);
            }
            public override void Visit(SqlDataTypeReference node)
            {
                switch (node.SqlDataTypeOption)
                {
                    case SqlDataTypeOption.Char: case SqlDataTypeOption.VarChar: case SqlDataTypeOption.NChar:
                    case SqlDataTypeOption.NVarChar: case SqlDataTypeOption.Binary: case SqlDataTypeOption.VarBinary:
                        if (node.Parameters.Count == 0) Add("SW008", "Specify a length; the default (1 or 30) silently truncates.", node);
                        break;
                    case SqlDataTypeOption.Text: case SqlDataTypeOption.NText: case SqlDataTypeOption.Image:
                        Add("SW010", "TEXT, NTEXT and IMAGE are deprecated; use VARCHAR(MAX), NVARCHAR(MAX) or VARBINARY(MAX).", node);
                        break;
                }
            }
            public override void Visit(GlobalVariableExpression node)
            {
                if (string.Equals(node.Name, "@@IDENTITY", StringComparison.OrdinalIgnoreCase))
                    Add("SW009", "@@IDENTITY can return a trigger's identity; use SCOPE_IDENTITY() or OUTPUT.", node);
            }
            public override void Visit(FromClause node)
            {
                if (node.TableReferences.Count > 1) Add("SW011", "Comma-separated tables are an old-style join; use explicit JOIN.", node);
            }
            public override void Visit(ProcedureStatementBody node)
            {
                var name = node.ProcedureReference?.Name?.BaseIdentifier?.Value;
                if (name != null && name.StartsWith("sp_", StringComparison.OrdinalIgnoreCase))
                    Add("SW012", "Procedure names starting with sp_ are looked up in master first.", node.ProcedureReference!);
                if (node.StatementList == null) return; // CLR procedure.
                var nocount = new NoCountFinder();
                node.StatementList.Accept(nocount);
                if (!nocount.Found) Add("SW015", "Procedure lacks SET NOCOUNT ON; row-count messages add network chatter.", node.ProcedureReference ?? (TSqlFragment)node);
            }
            public override void Visit(FunctionCall node)
            {
                if (string.Equals(node.FunctionName.Value, "ISNUMERIC", StringComparison.OrdinalIgnoreCase))
                    Add("SW013", "ISNUMERIC accepts values like '$' and '1e5'; use TRY_CONVERT.", node);
            }
            public override void Visit(InPredicate node)
            {
                if (node.NotDefined && node.Subquery != null)
                    Add("SW014", "NOT IN with a subquery returns no rows if the subquery yields NULL; use NOT EXISTS.", node);
            }
            public override void Visit(ExecutableProcedureReference node)
            {
                var name = node.ProcedureReference?.ProcedureReference?.Name;
                if (name != null && name.SchemaIdentifier == null && !name.BaseIdentifier.Value.StartsWith("sp_", StringComparison.OrdinalIgnoreCase) &&
                    !name.BaseIdentifier.Value.StartsWith("#", StringComparison.Ordinal))
                    Add("SW017", "Schema-qualify the procedure name to avoid extra name resolution.", name);
            }
            public override void Visit(TSqlBatch node)
            {
                var usage = new VariableUsage();
                node.Accept(usage);
                foreach (var declared in usage.Declared.Where(d => !usage.Used.Contains(d.Value)))
                    Add("SW016", "Variable " + declared.Value + " is declared but never used.", declared);
            }

            private sealed class NoCountFinder : TSqlFragmentVisitor
            {
                internal bool Found;
                public override void Visit(PredicateSetStatement node)
                {
                    if (node.IsOn && node.Options.HasFlag(SetOptions.NoCount)) Found = true;
                }
            }

            private sealed class VariableUsage : TSqlFragmentVisitor
            {
                internal readonly List<Identifier> Declared = new List<Identifier>();
                internal readonly HashSet<string> Used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                public override void Visit(DeclareVariableElement node) { if (!(node is ProcedureParameter)) Declared.Add(node.VariableName); }
                public override void Visit(DeclareTableVariableBody node) => Declared.Add(node.VariableName);
                public override void Visit(VariableReference node) => Used.Add(node.Name);
                public override void Visit(VariableTableReference node) => Used.Add(node.Variable.Name);
            }
        }
    }
}

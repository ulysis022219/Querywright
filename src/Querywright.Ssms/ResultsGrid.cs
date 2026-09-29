#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Querywright.Ssms
{
    /// <summary>Cells read from an SSMS results grid. Display text; null = SQL NULL. Never logged.</summary>
    internal sealed class GridCells
    {
        internal List<string?> Headers = new List<string?>();
        internal List<string?>? Types;
        internal List<string?[]> Rows = new List<string?[]>();
        internal bool Truncated;
    }

    /// <summary>
    /// Reads the focused SSMS results grid. SSMS has no public grid API, so this reflects over
    /// Microsoft.SqlServer.Management.UI.Grid.GridControl and its IGridStorage (QEResultSet), the same
    /// members the open-source SSMS add-ins SSMSDataAnalyzer and SQLExtended use. Grid column 0 is the
    /// row-number gutter; type lookups use the 0-based data column.
    /// </summary>
    internal static class ResultsGridReader
    {
        private const long MaxCells = 500_000; // ponytail: keeps the UI thread responsive; the result says when it was cut.
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        /// <summary>The results grid that has keyboard focus (the one right-clicked or clicked), else null. UI thread only.</summary>
        internal static object? FocusedGrid()
        {
            Control? control;
            try { control = Control.FromHandle(GetFocus()); }
            catch (Exception error) when (!(error is OutOfMemoryException)) { return null; }
            for (var type = control?.GetType(); type != null; type = type.BaseType)
                if (type.Name == "GridControl") return control;
            return null;
        }

        private static MethodInfo? Method(object target, string name, params Type[] args)
        {
            var type = target.GetType();
            return type.GetMethod(name, Any, null, args, null)
                ?? type.GetInterfaces().Select(i => i.GetMethod(name, args)).FirstOrDefault(m => m != null);
        }

        private static object? Member(object target, string name)
        {
            var type = target.GetType();
            var property = type.GetProperty(name, Any) ?? type.GetInterfaces().Select(i => i.GetProperty(name)).FirstOrDefault(p => p != null);
            if (property != null) return property.GetValue(target, null);
            return type.GetField(name, Any)?.GetValue(target);
        }

        private static InvalidOperationException Unsupported(string what) =>
            new InvalidOperationException("This SSMS version's results grid does not expose " + what + ". Copy the cells with SSMS instead.");

        /// <summary>
        /// For <paramref name="valuesOnly"/> (IN clause): exactly the selected cells, in <see cref="GridCells.Rows"/> as one
        /// value per row. Otherwise the selected rows x columns, where a selection of one cell or less means the whole
        /// grid, since a single click always selects a cell.
        /// </summary>
        internal static GridCells Read(object grid, bool valuesOnly)
        {
            object storage = Member(grid, "GridStorage") ?? throw Unsupported("its data");
            var cell = Method(storage, "GetCellDataAsString", typeof(long), typeof(int)) ?? throw Unsupported("cell text");
            var numRows = Method(storage, "NumRows") ?? throw Unsupported("its row count");
            var isNull = Method(storage, "IsCellDataNull", typeof(long), typeof(int));
            var typeName = Method(storage, "GetFormattedDataTypeName", typeof(int));
            var header = grid.GetType().GetMethods(Any).FirstOrDefault(m => m.Name == "GetHeaderInfo" && m.GetParameters().Length == 3
                && m.GetParameters()[0].ParameterType == typeof(int) && m.GetParameters()[1].ParameterType == typeof(string).MakeByRefType());
            long rowCount = Convert.ToInt64(numRows.Invoke(storage, null));
            int lastColumn = Convert.ToInt32(Member(grid, "ColumnsNumber") ?? throw Unsupported("its column count")) - 1;
            if (lastColumn < 1) throw new InvalidOperationException("The focused grid has no data columns.");

            var blocks = new List<(long Top, long Bottom, int Left, int Right)>();
            if (Member(grid, "SelectedCells") is IEnumerable selected)
                foreach (var block in selected)
                {
                    if (block == null || Member(block, "IsEmpty") as bool? == true) continue;
                    long top = Math.Max(0, Convert.ToInt64(Member(block, "Y"))), bottom = Math.Min(rowCount - 1, Convert.ToInt64(Member(block, "Bottom")));
                    int left = Math.Max(1, Convert.ToInt32(Member(block, "X"))), right = Math.Min(lastColumn, Convert.ToInt32(Member(block, "Right")));
                    if (top <= bottom && left <= right) blocks.Add((top, bottom, left, right));
                }

            string? Text(long row, int column)
            {
                if (isNull != null && (bool)isNull.Invoke(storage, new object[] { row, column })) return null;
                string? value = cell.Invoke(storage, new object[] { row, column }) as string;
                return isNull == null && value == "NULL" ? null : value; // older grids: fall back to the NULL display text
            }

            var result = new GridCells();
            if (valuesOnly)
            {
                foreach (var block in blocks)
                    for (long row = block.Top; row <= block.Bottom; row++)
                        for (int column = block.Left; column <= block.Right; column++)
                        {
                            if (result.Rows.Count >= MaxCells) { result.Truncated = true; return result; }
                            result.Rows.Add(new[] { Text(row, column) });
                        }
                return result;
            }

            bool single = blocks.Count == 0 || blocks.Count == 1 && blocks[0].Top == blocks[0].Bottom && blocks[0].Left == blocks[0].Right;
            if (single) blocks = rowCount == 0 ? new List<(long, long, int, int)>() : new List<(long, long, int, int)> { (0, rowCount - 1, 1, lastColumn) };
            var columns = single ? Enumerable.Range(1, lastColumn).ToList()
                : blocks.SelectMany(b => Enumerable.Range(b.Left, b.Right - b.Left + 1)).Distinct().OrderBy(c => c).ToList();
            var rows = new SortedSet<long>();
            foreach (var block in blocks)
                for (long row = block.Top; row <= block.Bottom; row++)
                {
                    if (!rows.Contains(row) && (long)(rows.Count + 1) * columns.Count > MaxCells) { result.Truncated = true; break; }
                    rows.Add(row);
                }
            foreach (int column in columns)
            {
                string? text = null;
                if (header != null)
                {
                    var args = new object?[] { column, null, null };
                    header.Invoke(grid, args);
                    text = args[1] as string;
                }
                result.Headers.Add(text);
            }
            if (typeName != null)
                try { result.Types = columns.Select(c => typeName.Invoke(storage, new object[] { c - 1 }) as string).ToList(); }
                catch (TargetInvocationException) { result.Types = null; }
            foreach (long row in rows) result.Rows.Add(columns.Select(c => Text(row, c)).ToArray());
            return result;
        }
    }
}

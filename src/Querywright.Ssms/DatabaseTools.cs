using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Querywright.Ssms
{
    /// <summary>Cross-database work: run one script in several databases into one merged table, or search object and column names in all of them.</summary>
    internal static class DatabaseTools
    {
        private const int MaxRows = 100_000;
        private const string DatabaseColumn = "Database";

        private const string SearchSql = @"SELECT DB_NAME() AS [Database], s.name AS [Schema], o.name AS [Object], o.type_desc AS [Type], c.name AS [Column]
FROM sys.objects AS o
JOIN sys.schemas AS s ON s.schema_id = o.schema_id
LEFT JOIN sys.columns AS c ON c.object_id = o.object_id AND c.name LIKE @p ESCAPE '\'
WHERE o.is_ms_shipped = 0 AND o.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF', 'TR') AND (o.name LIKE @p ESCAPE '\' OR c.name LIKE @p ESCAPE '\')
ORDER BY 2, 3, 5;";

        internal static bool HasGo(string sql) => Regex.IsMatch(sql, @"^\s*GO\s*(\d+\s*)?$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

        /// <summary>First result set of <paramref name="sql"/> from each database, with a leading database column. Failed databases are listed in <paramref name="errors"/>.</summary>
        internal static DataTable Run(ActiveConnection connection, IReadOnlyList<string> databases, string sql, Action<SqlCommand> parameters, bool addDatabase, List<string> errors)
        {
            var merged = new DataTable("Results");
            foreach (var database in databases)
            {
                try
                {
                    var table = new DataTable();
                    using (var sqlConnection = connection.WithDatabase(database).Open())
                    using (var command = new SqlCommand(sql, sqlConnection) { CommandTimeout = 120 })
                    {
                        parameters?.Invoke(command);
                        using (var reader = command.ExecuteReader()) table.Load(reader);
                    }
                    if (addDatabase)
                    {
                        var added = table.Columns.Add(table.Columns.Contains(DatabaseColumn) ? "Querywright_" + DatabaseColumn : DatabaseColumn, typeof(string));
                        added.SetOrdinal(0);
                        foreach (DataRow row in table.Rows) row[0] = database;
                    }
                    if (merged.Rows.Count + table.Rows.Count > MaxRows) { errors.Add(database + ": stopped, more than " + MaxRows + " rows."); break; }
                    merged.Merge(table, false, MissingSchemaAction.Add);
                }
                catch (Exception error) when (error is SqlException || error is InvalidOperationException || error is ArgumentException || error is ConstraintException)
                {
                    errors.Add(database + ": " + error.Message);
                }
            }
            return merged;
        }

        internal static DataTable Search(ActiveConnection connection, IReadOnlyList<string> databases, string text, List<string> errors)
        {
            string pattern = "%" + Regex.Replace(text, @"[%_\[\\]", "\\$0") + "%";
            return Run(connection, databases, SearchSql, command => command.Parameters.Add("@p", SqlDbType.NVarChar, 400).Value = pattern, false, errors);
        }

        internal static void Show(string title, DataTable table, List<string> errors)
        {
            string status = table.Rows.Count + " row(s)" + (errors.Count == 0 ? "" : "; " + errors.Count + " database(s) failed: " + string.Join(" | ", errors));
            using (var form = new Form { Text = title, Width = 1000, Height = 600, MinimumSize = new System.Drawing.Size(640, 400), Font = System.Drawing.SystemFonts.MessageBoxFont, AutoScaleMode = AutoScaleMode.Dpi, Padding = new Padding(12), StartPosition = FormStartPosition.CenterScreen, ShowInTaskbar = false })
            {
                var grid = new DataGridView
                {
                    Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, DataSource = table,
                    ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
                    AllowUserToDeleteRows = false, AllowUserToOrderColumns = true, RowHeadersVisible = false, BackgroundColor = System.Drawing.SystemColors.Window,
                    BorderStyle = BorderStyle.FixedSingle, AccessibleName = "Database query results",
                };
                var label = new TextBox { Dock = DockStyle.Bottom, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = errors.Count == 0 ? 48 : 96, Text = status + ". Select cells and press Ctrl+C to copy.", AccessibleName = "Result status and database errors" };
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
                var close = new Button { Text = "&Close", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new System.Drawing.Size(90, 32) };
                buttons.Controls.Add(close); form.CancelButton = close;
                form.Controls.Add(grid);
                form.Controls.Add(label);
                form.Controls.Add(buttons);
                form.ShowDialog();
            }
        }

        /// <summary>Single-line prompt; null when cancelled or empty.</summary>
        internal static string Ask(string title, string prompt)
        {
            using (var form = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterScreen, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Font = System.Drawing.SystemFonts.MessageBoxFont, AutoScaleMode = AutoScaleMode.Dpi, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16) })
            {
                var layout = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
                var box = new TextBox { Width = 420, MaxLength = 128, AccessibleName = prompt, Margin = new Padding(3, 8, 3, 12) };
                var ok = new Button { Text = "&OK", AutoSize = true, MinimumSize = new System.Drawing.Size(90, 32), DialogResult = DialogResult.OK, Enabled = false };
                var cancel = new Button { Text = "&Cancel", AutoSize = true, MinimumSize = new System.Drawing.Size(90, 32), DialogResult = DialogResult.Cancel };
                var buttons = new FlowLayoutPanel { Width = 420, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
                buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
                layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new System.Drawing.Size(420, 0), Text = prompt });
                layout.Controls.Add(box); layout.Controls.Add(buttons); form.Controls.Add(layout);
                box.TextChanged += (sender, args) => ok.Enabled = !string.IsNullOrWhiteSpace(box.Text);
                form.Shown += (sender, args) => box.Focus();
                form.AcceptButton = ok; form.CancelButton = cancel;
                return form.ShowDialog() == DialogResult.OK && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
            }
        }
    }
}

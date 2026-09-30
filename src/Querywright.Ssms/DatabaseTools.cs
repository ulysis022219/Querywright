using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Querywright.Core;

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

        internal const int CodeSearchCap = 500;

        // Built for databases with hundreds of thousands of objects: current database only, TOP (500) without ORDER BY (sorted on the client),
        // dirty reads, a 3 s lock wait and low deadlock priority so it gives way to real work. A table, view or synonym name
        // goes through sys.sql_expression_dependencies instead of a LIKE over every module definition.
        private const string CodeSql = @"SET LOCK_TIMEOUT 3000; SET DEADLOCK_PRIORITY LOW; SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
IF @name IS NOT NULL AND EXISTS (SELECT 1 FROM sys.objects WHERE name = @name AND type IN ('U', 'V', 'SN'))
    SELECT TOP (500) s.name, o.name, o.type_desc, m.definition, CAST(1 AS bit)
    FROM sys.sql_modules AS m JOIN sys.objects AS o ON o.object_id = m.object_id JOIN sys.schemas AS s ON s.schema_id = o.schema_id
    WHERE o.is_ms_shipped = 0 AND m.object_id IN (SELECT d.referencing_id FROM sys.sql_expression_dependencies AS d WHERE d.referenced_entity_name = @name);
ELSE
    SELECT TOP (500) s.name, o.name, o.type_desc, m.definition, CAST(0 AS bit)
    FROM sys.sql_modules AS m JOIN sys.objects AS o ON o.object_id = m.object_id JOIN sys.schemas AS s ON s.schema_id = o.schema_id
    WHERE o.is_ms_shipped = 0 AND m.definition LIKE @p ESCAPE '\';";

        /// <summary>Lines of module code in the connection's database containing <paramref name="text"/>. Never logs the definitions.</summary>
        internal static DataTable SearchCode(ActiveConnection connection, string text, List<string> errors)
        {
            var result = new DataTable("Code");
            result.Columns.Add("Schema", typeof(string)); result.Columns.Add("Object", typeof(string)); result.Columns.Add("Type", typeof(string));
            result.Columns.Add("Line", typeof(int)); result.Columns.Add("Code", typeof(string));
            string word = text.Split('.').Last().Trim('[', ']', '"');
            var rows = new List<object[]>();
            int modules = 0; bool dependencies = false;
            try
            {
                using (var sql = connection.Open())
                using (var command = new SqlCommand(CodeSql, sql) { CommandTimeout = 60 })
                {
                    command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = Regex.IsMatch(word, @"^[\w@#$ ]{1,128}\z") ? word : (object)DBNull.Value;
                    command.Parameters.Add("@p", SqlDbType.NVarChar, 4000).Value = "%" + Regex.Replace(text, @"[%_\[\\]", "\\$0") + "%";
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                        {
                            modules++;
                            dependencies = reader.GetBoolean(4);
                            string schema = reader.GetString(0), name = reader.GetString(1), type = reader.GetString(2);
                            var lines = SqlAssist.MatchingLines(reader.IsDBNull(3) ? "" : reader.GetString(3), dependencies ? word : text);
                            if (lines.Count == 0) rows.Add(new object[] { schema, name, type, DBNull.Value, "(referenced; name not found as written)" });
                            foreach (var (line, code) in lines) rows.Add(new object[] { schema, name, type, line, code });
                        }
                }
            }
            catch (Exception error) when (error is SqlException || error is InvalidOperationException)
            {
                errors.Add(error is SqlException sqlError && (sqlError.Number == 1222 || sqlError.Number == 1205 || sqlError.Number == -2)
                    ? "gave way to other work (lock wait, deadlock or timeout); try again or narrow the text." : error.Message);
            }
            foreach (var row in rows.OrderBy(r => (string)r[0], StringComparer.OrdinalIgnoreCase).ThenBy(r => (string)r[1], StringComparer.OrdinalIgnoreCase).ThenBy(r => r[3] as int? ?? 0))
                result.Rows.Add(row);
            if (modules >= CodeSearchCap) errors.Add("stopped at the first " + CodeSearchCap + " objects; narrow the text to see the rest.");
            if (dependencies) errors.Add("usages from the dependency catalog; dynamic SQL is not included.");
            return result;
        }

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

        /// <summary>Read-only result grid. With <paramref name="open"/>, double-click or Enter on a row closes the dialog and hands it the row.</summary>
        internal static void Show(string title, DataTable table, List<string> errors, Action<DataRow> open = null)
        {
            string status = table.Rows.Count + " row(s)" + (errors.Count == 0 ? "" : open != null ? "; " + string.Join(" ", errors) : "; " + errors.Count + " database(s) failed: " + string.Join(" | ", errors));
            DataRow chosen = null;
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
                if (open != null)
                {
                    void Pick() { if (grid.CurrentRow?.DataBoundItem is DataRowView item) { chosen = item.Row; form.DialogResult = DialogResult.OK; } }
                    grid.CellDoubleClick += (sender, args) => { if (args.RowIndex >= 0) Pick(); };
                    grid.KeyDown += (sender, args) => { if (args.KeyCode == Keys.Enter) { args.Handled = true; Pick(); } };
                    label.Text += " Double-click a row (or Enter) to open the object's script at that line.";
                }
                form.Controls.Add(grid);
                form.Controls.Add(label);
                form.Controls.Add(buttons);
                form.Controls.Add(DialogParts.FormHeader("Database results", "Read-only results. Select cells and press Ctrl+C to copy with headers."));
                DialogParts.Style(form);
                form.ShowDialog();
            }
            if (chosen != null) open(chosen);
        }

        /// <summary>Single-line prompt; null when cancelled or empty.</summary>
        internal static string Ask(string title, string prompt, string initial = null)
        {
            using (var form = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterScreen, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Font = System.Drawing.SystemFonts.MessageBoxFont, AutoScaleMode = AutoScaleMode.Dpi, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16) })
            {
                var layout = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
                var box = new TextBox { Width = 420, MaxLength = 128, AccessibleName = prompt, Margin = new Padding(3, 8, 3, 12) };
                box.Text = initial?.Trim() ?? "";
                var ok = new Button { Text = "&OK", AutoSize = true, MinimumSize = new System.Drawing.Size(90, 32), DialogResult = DialogResult.OK, Enabled = false };
                var cancel = new Button { Text = "&Cancel", AutoSize = true, MinimumSize = new System.Drawing.Size(90, 32), DialogResult = DialogResult.Cancel };
                var buttons = new FlowLayoutPanel { Width = 420, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
                buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
                layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new System.Drawing.Size(420, 0), Text = prompt });
                layout.Controls.Add(box); layout.Controls.Add(buttons); form.Controls.Add(layout);
                box.TextChanged += (sender, args) => ok.Enabled = !string.IsNullOrWhiteSpace(box.Text);
                ok.Enabled = box.Text.Length > 0;
                form.Shown += (sender, args) => { box.Focus(); box.SelectAll(); };
                form.AcceptButton = ok; form.CancelButton = cancel;
                DialogParts.Style(form);
                return form.ShowDialog() == DialogResult.OK && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
            }
        }
    }
}

using System;
using System.ComponentModel;
using System.IO;
using Microsoft.VisualStudio.Shell;

namespace Querywright.Ssms
{
    public sealed class WorkbenchOptions : DialogPage
    {
        [Category("Completion")]
        [DisplayName("Offline schema SQL file")]
        [Description("Optional CREATE TABLE script used for completion when not connected, or for tables missing from the connection.")]
        public string SchemaFile { get; set; } = "";

        [Category("Completion")]
        [DisplayName("Read live metadata")]
        [Description("Read tables, views, columns, keys, procedures, parameters and database names from the query window's connection with fixed read-only catalog queries, over Windows or SQL Server authentication (Microsoft Entra when SSMS shares its sign-in token). Never runs your SQL.")]
        public bool LiveMetadata { get; set; } = true;

        [Category("Completion")]
        [DisplayName("Cache schema on disk")]
        [Description("Keep table, column, procedure and parameter names, types and foreign keys in %LOCALAPPDATA%\\Querywright\\SchemaCache so suggestions work at once after a restart while the live load refreshes them. Never stores credentials, query text or data; file names are hashes. Turning this off deletes the cached files.")]
        public bool CacheSchemaOnDisk { get; set; } = true;

        [Category("Completion")]
        [DisplayName("Live metadata row limit")]
        [Description("Most rows read per catalog query (columns, procedure parameters) on each load. Higher loads more tables on very large databases at the cost of load time and memory: roughly 10 s and 600 MB for 360,000 columns. 0 means no limit. Applies from the next refresh.")]
        public int LiveMetadataRowLimit { get; set; } = 100_000;

        [Category("Completion")]
        [DisplayName("Close quotes")]
        [Description("Typing ' inserts '' with the caret between them; typing ' before the closing quote steps over it, and Backspace in an empty pair removes both.")]
        public bool CloseQuotes { get; set; } = true;

        [Category("Completion")]
        [DisplayName("Qualify columns of a single table")]
        [Description("Insert Table.Column even when the statement reads one table. Off inserts just Column; joins always use the alias.")]
        public bool QualifySingleTable { get; set; }

        [Category("Completion")]
        [DisplayName("Suggest while typing")]
        [Description("Open the suggestion list as you type a name, '.' or space. Off shows it only on Ctrl+Space (Edit > IntelliSense > List Members).")]
        public bool SuggestWhileTyping { get; set; } = true;

        [Category("Completion")]
        [DisplayName("Smart matching and ranking")]
        [Description("Filter the open list as you type by prefix, word starts (oi finds OrderId) and text anywhere in the name, with the best match selected and join suggestions first. Off uses the editor's default prefix list.")]
        public bool SmartMatching { get; set; } = true;

        [Category("Completion")]
        [DisplayName("Match word starts")]
        [Description("Typing oi or OI matches OrderId and order_id. Needs smart matching.")]
        public bool CamelHumps { get; set; } = true;

        [Category("Completion")]
        [DisplayName("Rank recently used first")]
        [Description("Among equally good matches, items you picked earlier in this SSMS session come first. Nothing is saved to disk.")]
        public bool RankByUsage { get; set; } = true;

        [Category("Completion")]
        [DisplayName("Assign aliases")]
        [Description("A table or view chosen after FROM or JOIN is inserted with an alias made from the initials of its name (OrderDetails od), numbered when taken.")]
        public bool AliasTables { get; set; }

        [Category("Completion")]
        [DisplayName("Alias with AS")]
        [Description("Insert dbo.Orders AS o instead of dbo.Orders o.")]
        public bool AliasWithAs { get; set; }

        [Category("Completion")]
        [DisplayName("Uppercase aliases")]
        [Description("Generated aliases are written in capitals.")]
        public bool UppercaseAliases { get; set; }

        [Category("Completion")]
        [DisplayName("Alias ignored prefixes")]
        [Description("Name prefixes left out of generated aliases, separated by commas, e.g. tbl, vw.")]
        public string AliasIgnoredPrefixes { get; set; } = "";

        [Category("Completion")]
        [DisplayName("Custom aliases")]
        [Description("Your own aliases as Table=alias pairs separated by semicolons, e.g. Orders=ord; People=per.")]
        public string CustomAliases { get; set; } = "";

        [Category("Completion")]
        [DisplayName("Qualify object names")]
        [Description("Insert dbo.Orders. Off inserts Orders for the dbo schema; other schemas are always written.")]
        public bool QualifyObjects { get; set; } = true;

        [Category("Completion")]
        [DisplayName("Suggest join tables")]
        [Description("After JOIN, tables linked by a foreign key to a table already in the query come first and insert their ON condition.")]
        public bool JoinTables { get; set; } = true;

        [Category("Completion")]
        [DisplayName("Suggest joins on matching column names")]
        [Description("After ON, also offer conditions on columns that have the same name (and type, where known) in both tables.")]
        public bool JoinNameMatches { get; set; } = true;

        [Category("Completion")]
        [DisplayName("Earlier table first in join conditions")]
        [Description("Write p.Id = o.PersonId (earlier table first) instead of o.PersonId = p.Id.")]
        public bool SwapJoinOrder { get; set; }

        [Category("Completion")]
        [DisplayName("Order columns alphabetically")]
        [Description("Off lists columns in table definition order.")]
        public bool ColumnsAlphabetical { get; set; }

        [Category("Completion")]
        [DisplayName("Keywords after a table")]
        [Description("After a table or alias in FROM, list only what can follow (WHERE, JOIN, ...); list nothing while a new alias is typed after AS.")]
        public bool ContextKeywords { get; set; } = true;

        [Category("Completion")]
        [DisplayName("Commit with Space")]
        [Description("Space inserts the selected suggestion, then the space. Never applies to a soft selection (dotted outline). Tab and Enter always insert.")]
        public bool CommitOnSpace { get; set; }

        [Category("Completion")]
        [DisplayName("Commit with .")]
        [Description(". inserts the selected suggestion, then the dot.")]
        public bool CommitOnDot { get; set; }

        [Category("Completion")]
        [DisplayName("Commit with ,")]
        [Description(", inserts the selected suggestion, then the comma.")]
        public bool CommitOnComma { get; set; }

        [Category("Completion")]
        [DisplayName("Commit with ( and )")]
        [Description("A parenthesis inserts the selected suggestion; a function that already ends in ( gets one, not two.")]
        public bool CommitOnParentheses { get; set; }

        [Category("Completion")]
        [DisplayName("Commit with ;")]
        [Description("; inserts the selected suggestion, then the semicolon.")]
        public bool CommitOnSemicolon { get; set; }

        internal string CommitCharacters() =>
            (CommitOnSpace ? " " : "") + (CommitOnDot ? "." : "") + (CommitOnComma ? "," : "") + (CommitOnParentheses ? "()" : "") + (CommitOnSemicolon ? ";" : "");

        /// <summary>Core completion settings. <paramref name="filter"/> false returns every candidate for the item manager to filter as the user types.</summary>
        internal Querywright.Core.CompletionOptions Completion(bool filter)
        {
            var aliases = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string pair in (CustomAliases ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = pair.IndexOf('=');
                if (equals > 0 && pair.Substring(0, equals).Trim().Length > 0 && pair.Substring(equals + 1).Trim().Length > 0)
                    aliases[pair.Substring(0, equals).Trim()] = pair.Substring(equals + 1).Trim();
            }
            return new Querywright.Core.CompletionOptions
            {
                QualifySingleTable = QualifySingleTable, Filter = filter, CamelHumps = CamelHumps, MaxItems = filter ? 200 : 5000,
                AliasTables = AliasTables, AliasWithAs = AliasWithAs, UppercaseAliases = UppercaseAliases, Aliases = aliases,
                IgnoredPrefixes = (AliasIgnoredPrefixes ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries),
                QualifyObjects = QualifyObjects, JoinTables = JoinTables, JoinNameMatches = JoinNameMatches, SwapJoinOrder = SwapJoinOrder,
                ColumnsAlphabetical = ColumnsAlphabetical, ContextKeywords = ContextKeywords,
            };
        }

        [Category("Analysis")]
        [DisplayName("Show analysis squiggles")]
        [Description("Underline code-analysis findings (SW rules) as you type. Rule severities come from the shared settings file. Off hides them after the next edit; Analyze document still works.")]
        public bool LiveAnalysis { get; set; } = true;

        [Category("Analysis")]
        [DisplayName("Flag unmatched BEGIN/END and parentheses")]
        [Description("Underline a BEGIN, CASE, END, ( or ) that has no partner as you type. Strings and comments are ignored; each GO batch is checked on its own.")]
        public bool FlagUnmatched { get; set; } = true;

        [Category("Editor")]
        [DisplayName("F12 goes to definition")]
        [Description("F12 on a table, view, procedure or function scripts its definition into a new window (never executed). Off leaves F12 to SSMS.")]
        public bool GoToDefinition { get; set; } = true;

        [Category("Editor")]
        [DisplayName("Ctrl+] jumps between BEGIN/END")]
        [Description("Ctrl+] moves the caret between BEGIN and END, CASE and END, TRY and CATCH. Off leaves Ctrl+] to SSMS (brackets only).")]
        public bool JumpToBlockPartner { get; set; } = true;

        [Category("Editor")]
        [DisplayName("Color BEGIN/END pairs")]
        [Description("Give each BEGIN/END, CASE/END and TRY/CATCH pair a color by nesting level. Colors are under Fonts and Colors, \"Querywright BEGIN/END level\". The pair at the caret is always highlighted; Ctrl+] jumps to its partner.")]
        public bool ColorBlocks { get; set; } = true;

        [Category("Editor")]
        [DisplayName("Parameter hints")]
        [Description("After EXEC procedure, or ( after a user function, show its parameters with the current one in bold. Uses the cached metadata; never queries while typing.")]
        public bool ParameterHints { get; set; } = true;

        [Category("Editor")]
        [DisplayName("Hover info")]
        [Description("Hovering a column, table, procedure or variable shows its type and details from the cached metadata; hovering an END shows the line that opened it.")]
        public bool HoverInfo { get; set; } = true;

        [Category("Editor")]
        [DisplayName("Format on save")]
        [Description("File > Save (Ctrl+S) formats the SQL document with your formatting style first, and File > Save All formats every unsaved SQL document. One Undo reverts each. Scripts that do not parse are saved unchanged. Off by default.")]
        public bool FormatOnSave { get; set; }

        [Category("Editor")]
        [DisplayName("Fold regions and BEGIN/END blocks")]
        [Description("Collapse --region / --endregion sections and multi-line BEGIN/END blocks from the margin. Reopen the query window after changing this.")]
        public bool FoldBlocks { get; set; } = true;

        [Category("Tab history")]
        [DisplayName("Keep tab history")]
        [Description("Save query text of SQL windows to %LOCALAPPDATA%\\Querywright\\TabHistory (local only): a timestamped version after each edit and on execute, newest 100 per tab, 200 tabs and 256 MB in all kept (favorites aside), so any version can be reopened. PASSWORD and SECRET literals, password variables and the arguments of password procedures (sp_addlogin, sp_password, sp_addlinkedsrvlogin and similar) are saved as ***. Turn off to save nothing.")]
        public bool TabHistory { get; set; } = true;

        [Category("Shared settings")]
        [DisplayName("Settings file")]
        [Description("Optional team XML file containing formatting style and rule severities. Empty uses defaults.")]
        public string SettingsFile { get; set; } = "";

        [Category("Environment")]
        [DisplayName("Tab color rules")]
        [Description("Colors the strip above a query editor by connection: \"pattern=color\" pairs separated by ';' matched against server/database, e.g. \"prod=Red;test=Orange\".")]
        public string TabColorRules { get; set; } = "";

        [Category("Environment")]
        [DisplayName("Show connection in editor")]
        [Description("Shows \"server \u00B7 database\" at the bottom right of each connected query editor, colored by the tab color rules.")]
        public bool ShowConnection { get; set; } = true;

        [Category("Environment")]
        [DisplayName("Warn before DELETE/UPDATE without WHERE")]
        [Description("When you execute a DELETE or UPDATE that has no WHERE clause, ask before SSMS runs it.")]
        public bool WarnUnfilteredChanges { get; set; } = true;

        [Category("Environment")]
        [DisplayName("Warn before DROP TABLE/TRUNCATE TABLE")]
        [Description("When you execute DROP TABLE or TRUNCATE TABLE, ask before SSMS runs it.")]
        public bool WarnDropTruncate { get; set; }

        [Category("Environment")]
        [DisplayName("Production servers")]
        [Description("Patterns separated by ';' matched against server/database, e.g. \"prod;live\". On a matching connection, executing DROP, ALTER, TRUNCATE or a DELETE/UPDATE without WHERE always asks first; Run in multiple databases names matching databases in its prompt.")]
        public string ProductionServers { get; set; } = "";

        [Category("Environment")]
        [DisplayName("Warn before USE then change data")]
        [Description("When a script switches database with USE and then inserts, updates, deletes, merges, truncates or drops, ask before SSMS runs it.")]
        public bool WarnUseSwitch { get; set; } = true;

        [Category("Results grid")]
        [DisplayName("CSV delimiter")]
        [Description("Separator used by Save as CSV. Semicolon suits Excel in locales that use a decimal comma.")]
        public CsvDelimiter CsvDelimiter { get; set; } = CsvDelimiter.Comma;

        [Category("Results grid")]
        [DisplayName("CSV column headers")]
        [Description("Write the column names as the first line of Save as CSV.")]
        public bool CsvHeaders { get; set; } = true;

        [Category("Results grid")]
        [DisplayName("Selection totals in status bar")]
        [Description("When more than one cell is selected, show count, sum, average, min and max of the numbers in the status bar. Values are never logged.")]
        public bool GridTotals { get; set; } = true;

        [Category("Results grid menu")]
        [DisplayName("Show copy as IN clause")]
        [Description("Show or hide this item in the results grid right-click menu.")]
        public bool ShowCopyAsIn { get; set; } = true;

        [Category("Results grid menu")]
        [DisplayName("Show script as INSERT")]
        [Description("Show or hide this item in the results grid right-click menu.")]
        public bool ShowScriptAsInsert { get; set; } = true;

        [Category("Results grid menu")]
        [DisplayName("Show open in Excel")]
        [Description("Show or hide this item in the results grid right-click menu.")]
        public bool ShowOpenInExcel { get; set; } = true;

        [Category("Results grid menu")]
        [DisplayName("Show save as CSV")]
        [Description("Show or hide this item in the results grid right-click menu.")]
        public bool ShowSaveAsCsv { get; set; } = true;

        [Category("Results grid menu")]
        [DisplayName("Show copy as Markdown table")]
        [Description("Show or hide this item in the results grid right-click menu.")]
        public bool ShowCopyAsMarkdown { get; set; } = true;

        [Category("Results grid menu")]
        [DisplayName("Show copy as JSON")]
        [Description("Show or hide this item in the results grid right-click menu.")]
        public bool ShowCopyAsJson { get; set; } = true;

        [Category("Results grid menu")]
        [DisplayName("Show script as UPDATE")]
        [Description("Show or hide this item in the results grid right-click menu.")]
        public bool ShowScriptAsUpdate { get; set; } = true;

        [Category("Results grid menu")]
        [DisplayName("Show script as MERGE")]
        [Description("Show or hide this item in the results grid right-click menu.")]
        public bool ShowScriptAsMerge { get; set; } = true;

        [Category("Results grid menu")]
        [DisplayName("Show script as CREATE TABLE")]
        [Description("Show or hide this item in the results grid right-click menu.")]
        public bool ShowScriptAsCreateTable { get; set; } = true;

        [Category("Updates")]
        [DisplayName("Check for updates")]
        [Description("Once a day, ask github.com for the latest Querywright release and show a notice when it is newer. Sends no query text, connection or user data.")]
        public bool CheckForUpdates { get; set; } = true;

        [Category("Diagnostics")]
        [DisplayName("Verbose diagnostics")]
        [Description("Keep bounded Querywright process events and AI request summaries in memory for export, including query, prompt, answer and result text. Known API keys and credential patterns are redacted, but exports can contain sensitive database data: review before sharing. Off by default; turning off clears the session log.")]
        public bool VerboseDiagnostics { get; set; }

        [Category("Snippets")]
        [DisplayName("Snippet folder")]
        [Description("Folder of editable .sql templates; ssf.sql expands when you type ssf and press Tab. Use a shared folder for team snippets. New files are picked up within 30 seconds.")]
        public string SnippetFolder { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Querywright", "Snippets");

        /// <summary>Databases ticked last time in Script for multiple databases, one per line.</summary>
        [Browsable(false)]
        public string MultiDatabaseSelection { get; set; } = "";

        protected override void OnApply(PageApplyEventArgs e)
        {
            base.OnApply(e);
            if (e.ApplyBehavior == ApplyKind.Apply)
            {
                Diagnostics.Enabled = VerboseDiagnostics;
                if (!CacheSchemaOnDisk) Querywright.Ssms.LiveMetadata.ClearDiskCache();
            }
        }

        [Category("Snippets")]
        [DisplayName("Tab expands snippets")]
        [Description("Tab after a snippet shortcut (e.g. ssf) or after * expands it. Off leaves Tab to insert a tab.")]
        public bool TabExpandSnippets { get; set; } = true;
    }

    public enum CsvDelimiter
    {
        Comma,
        Semicolon,
        Tab,
        Pipe,
    }
}

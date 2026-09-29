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
        [Description("Read table and column names from the query window's connection with one fixed catalog query (sys.objects, sys.columns). Never runs your SQL.")]
        public bool LiveMetadata { get; set; } = true;

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

        [Category("Analysis")]
        [DisplayName("Show analysis squiggles")]
        [Description("Underline code-analysis findings (SW rules) as you type. Rule severities come from the shared settings file. Off hides them after the next edit; Analyze document still works.")]
        public bool LiveAnalysis { get; set; } = true;

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

        [Category("Tab history")]
        [DisplayName("Keep tab history")]
        [Description("Save query text of SQL windows to %LOCALAPPDATA%\\Querywright\\TabHistory (local only): a timestamped version after each edit and on execute, newest 100 per tab and 200 tabs kept, so any version can be reopened. Turn off to save nothing.")]
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

        [Category("Results grid")]
        [DisplayName("CSV delimiter")]
        [Description("Separator used by Save as CSV. Semicolon suits Excel in locales that use a decimal comma.")]
        public CsvDelimiter CsvDelimiter { get; set; } = CsvDelimiter.Comma;

        [Category("Results grid")]
        [DisplayName("CSV column headers")]
        [Description("Write the column names as the first line of Save as CSV.")]
        public bool CsvHeaders { get; set; } = true;

        [Category("Updates")]
        [DisplayName("Check for updates")]
        [Description("Once a day, ask github.com for the latest Querywright release and show a notice when it is newer. Sends no query text, connection or user data.")]
        public bool CheckForUpdates { get; set; } = true;

        [Category("Snippets")]
        [DisplayName("Snippet folder")]
        [Description("Folder of editable .sql templates. Use a shared folder for team snippets.")]
        public string SnippetFolder { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Querywright", "Snippets");

        /// <summary>Databases ticked last time in Script for multiple databases, one per line.</summary>
        [Browsable(false)]
        public string MultiDatabaseSelection { get; set; } = "";

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

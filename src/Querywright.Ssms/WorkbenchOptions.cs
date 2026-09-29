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

        [Category("Updates")]
        [DisplayName("Check for updates")]
        [Description("Once a day, ask github.com for the latest Querywright release and show a notice when it is newer. Sends no query text, connection or user data.")]
        public bool CheckForUpdates { get; set; } = true;

        [Category("Snippets")]
        [DisplayName("Snippet folder")]
        [Description("Folder of editable .sql templates. Use a shared folder for team snippets.")]
        public string SnippetFolder { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Querywright", "Snippets");
    }
}

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

        [Category("Shared settings")]
        [DisplayName("Settings file")]
        [Description("Optional team XML file containing formatting style and rule severities. Empty uses defaults.")]
        public string SettingsFile { get; set; } = "";

        [Category("Snippets")]
        [DisplayName("Snippet folder")]
        [Description("Folder of editable .sql templates. Use a shared folder for team snippets.")]
        public string SnippetFolder { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Querywright", "Snippets");
    }
}

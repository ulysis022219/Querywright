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
        [Description("Optional CREATE TABLE script used for offline completion. Live connection metadata is not yet implemented.")]
        public string SchemaFile { get; set; } = "";

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

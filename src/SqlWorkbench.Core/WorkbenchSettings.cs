using System;
using System.IO;
using System.Xml;
using System.Xml.Serialization;

namespace SqlWorkbench.Core
{
    public enum RuleSeverity { Disabled, Info, Warning, Error }

    public sealed class WorkbenchSettings
    {
        public FormattingStyle Formatting { get; set; } = new FormattingStyle();
        public RuleSeverity SW001 { get; set; } = RuleSeverity.Warning;
        public RuleSeverity SW002 { get; set; } = RuleSeverity.Warning;
        public RuleSeverity SW003 { get; set; } = RuleSeverity.Warning;
        public RuleSeverity SW004 { get; set; } = RuleSeverity.Warning;

        public RuleSeverity Severity(string rule)
        {
            switch (rule)
            {
                case "SW001": return SW001;
                case "SW002": return SW002;
                case "SW003": return SW003;
                case "SW004": return SW004;
                default: return RuleSeverity.Error; // Parser errors cannot be disabled.
            }
        }

        public static WorkbenchSettings Load(string path)
        {
            using (var reader = XmlReader.Create(path, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_000_000
            }))
            {
                var settings = (WorkbenchSettings)new XmlSerializer(typeof(WorkbenchSettings)).Deserialize(reader);
                if (settings.Formatting == null || settings.Formatting.IndentSize < 1 || settings.Formatting.IndentSize > 16)
                    throw new FormatException("Settings require a formatting indent size between 1 and 16.");
                return settings;
            }
        }
    }
}

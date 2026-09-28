using System;
using System.IO;
using System.Xml;
using System.Xml.Serialization;

namespace Querywright.Core
{
    public enum RuleSeverity { Disabled, Info, Warning, Error }

    public sealed class WorkbenchSettings
    {
        public FormattingStyle Formatting { get; set; } = new FormattingStyle();
        public RuleSeverity SW001 { get; set; } = RuleSeverity.Warning;
        public RuleSeverity SW002 { get; set; } = RuleSeverity.Warning;
        public RuleSeverity SW003 { get; set; } = RuleSeverity.Warning;
        public RuleSeverity SW004 { get; set; } = RuleSeverity.Warning;
        public RuleSeverity SW005 { get; set; } = RuleSeverity.Warning;
        public RuleSeverity SW006 { get; set; } = RuleSeverity.Warning;
        public RuleSeverity SW007 { get; set; } = RuleSeverity.Warning;
        public RuleSeverity SW008 { get; set; } = RuleSeverity.Warning;
        public RuleSeverity SW009 { get; set; } = RuleSeverity.Warning;
        public RuleSeverity SW010 { get; set; } = RuleSeverity.Warning;
        public RuleSeverity SW011 { get; set; } = RuleSeverity.Warning;
        public RuleSeverity SW012 { get; set; } = RuleSeverity.Warning;
        public RuleSeverity SW013 { get; set; } = RuleSeverity.Warning;
        public RuleSeverity SW014 { get; set; } = RuleSeverity.Warning;
        public RuleSeverity SW015 { get; set; } = RuleSeverity.Warning;
        public RuleSeverity SW016 { get; set; } = RuleSeverity.Warning;
        public RuleSeverity SW017 { get; set; } = RuleSeverity.Warning;
        public RuleSeverity SW018 { get; set; } = RuleSeverity.Warning;
        public RuleSeverity SW019 { get; set; } = RuleSeverity.Warning;
        public RuleSeverity SW020 { get; set; } = RuleSeverity.Warning;
        public RuleSeverity SW021 { get; set; } = RuleSeverity.Warning;
        public RuleSeverity SW022 { get; set; } = RuleSeverity.Warning;
        public RuleSeverity SW023 { get; set; } = RuleSeverity.Warning;
        public RuleSeverity SW024 { get; set; } = RuleSeverity.Warning;
        public RuleSeverity SW025 { get; set; } = RuleSeverity.Warning;
        public RuleSeverity SW026 { get; set; } = RuleSeverity.Warning;
        public RuleSeverity SW027 { get; set; } = RuleSeverity.Warning;

        public RuleSeverity Severity(string rule)
        {
            // Rule IDs are property names; anything else (parser errors) cannot be disabled.
            var property = rule != null && rule.StartsWith("SW", StringComparison.Ordinal) ? typeof(WorkbenchSettings).GetProperty(rule) : null;
            return property?.PropertyType == typeof(RuleSeverity) ? (RuleSeverity)property.GetValue(this)! : RuleSeverity.Error;
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

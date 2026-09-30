using System;
using System.ComponentModel;
using System.IO;
using System.Xml;
using System.Xml.Serialization;

namespace Querywright.Core
{
    public enum RuleSeverity { Disabled, Info, Warning, Error }

    public sealed class WorkbenchSettings
    {
        [Browsable(false)]
        public FormattingStyle Formatting { get; set; } = new FormattingStyle();
        [DisplayName("SW001 Wildcard in output list")]
        public RuleSeverity SW001 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW002 INSERT lacks explicit target columns")]
        public RuleSeverity SW002 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW003 Direct or parenthesized NULL comparison")]
        public RuleSeverity SW003 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW004 CASE without fallback expression")]
        public RuleSeverity SW004 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW005 DELETE without WHERE")]
        public RuleSeverity SW005 { get; set; } = RuleSeverity.Disabled; // prompted on execute instead
        [DisplayName("SW006 UPDATE without WHERE")]
        public RuleSeverity SW006 { get; set; } = RuleSeverity.Disabled; // prompted on execute instead
        [DisplayName("SW007 ORDER BY constant or ordinal")]
        public RuleSeverity SW007 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW008 char/varchar/nchar/nvarchar/binary/varbinary without length")]
        public RuleSeverity SW008 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW009 @@IDENTITY")]
        public RuleSeverity SW009 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW010 TEXT, NTEXT, IMAGE")]
        public RuleSeverity SW010 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW011 Comma-separated FROM (old-style join)")]
        public RuleSeverity SW011 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW012 Procedure named sp_")]
        public RuleSeverity SW012 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW013 ISNUMERIC")]
        public RuleSeverity SW013 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW014 NOT IN with subquery")]
        public RuleSeverity SW014 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW015 Procedure without SET NOCOUNT ON")]
        public RuleSeverity SW015 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW016 Variable declared but never used (per batch)")]
        public RuleSeverity SW016 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW017 EXEC without schema")]
        public RuleSeverity SW017 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW018 Cursor without LOCAL/GLOBAL scope")]
        public RuleSeverity SW018 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW019 RETURN without a value in a stored procedure")]
        public RuleSeverity SW019 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW020 Column without explicit NULL/NOT NULL")]
        public RuleSeverity SW020 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW021 READTEXT, WRITETEXT, UPDATETEXT")]
        public RuleSeverity SW021 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW022 ALTER TABLE ADD NOT NULL column without DEFAULT")]
        public RuleSeverity SW022 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW023 SET ROWCOUNT")]
        public RuleSeverity SW023 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW024 NOLOCK / READUNCOMMITTED table hint")]
        public RuleSeverity SW024 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW025 WAITFOR DELAY inside a stored procedure")]
        public RuleSeverity SW025 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW026 SELECT TOP without ORDER BY")]
        public RuleSeverity SW026 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW027 EXECUTE(string) dynamic SQL")]
        public RuleSeverity SW027 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW028 COUNT subquery compared with 0")]
        public RuleSeverity SW028 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW029 GOTO")]
        public RuleSeverity SW029 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW030 SET ANSI_NULLS / ANSI_PADDING / CONCAT_NULL_YIELDS_NULL OFF")]
        public RuleSeverity SW030 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW031 @@ERROR")]
        public RuleSeverity SW031 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW032 Function around a column in a WHERE/JOIN comparison")]
        public RuleSeverity SW032 { get; set; } = RuleSeverity.Info;
        [DisplayName("SW033 LIKE pattern starting with % on a column")]
        public RuleSeverity SW033 { get; set; } = RuleSeverity.Info;
        [DisplayName("SW034 Index table hint")]
        public RuleSeverity SW034 { get; set; } = RuleSeverity.Info;
        [DisplayName("SW035 SET FMTONLY ON")]
        public RuleSeverity SW035 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW036 Named constraint on a #temp table")]
        public RuleSeverity SW036 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW037 FLOAT / REAL")]
        public RuleSeverity SW037 { get; set; } = RuleSeverity.Info;
        [DisplayName("SW038 MONEY / SMALLMONEY")]
        public RuleSeverity SW038 { get; set; } = RuleSeverity.Info;
        [DisplayName("SW039 TIMESTAMP type synonym")]
        public RuleSeverity SW039 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW040 String literal as column alias ('x' = 1, AS 'x')")]
        public RuleSeverity SW040 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW041 Numbered procedure (name;n)")]
        public RuleSeverity SW041 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW042 !< and !> operators")]
        public RuleSeverity SW042 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW043 TOP 100 PERCENT")]
        public RuleSeverity SW043 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW044 EXISTS over an aggregate with no GROUP BY/HAVING")]
        public RuleSeverity SW044 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW045 DECIMAL / NUMERIC without precision")]
        public RuleSeverity SW045 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW046 xp_cmdshell")]
        public RuleSeverity SW046 { get; set; } = RuleSeverity.Warning;
        [DisplayName("SW047 EXEC of a concatenated string (EXEC('...' + @x))")]
        public RuleSeverity SW047 { get; set; } = RuleSeverity.Warning;

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

        public void Save(string path)
        {
            string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (folder != null) Directory.CreateDirectory(folder);
            string temp = path + ".qwtmp";
            try
            {
                using (var writer = XmlWriter.Create(temp, new XmlWriterSettings { Indent = true }))
                    new XmlSerializer(typeof(WorkbenchSettings)).Serialize(writer, this);
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}

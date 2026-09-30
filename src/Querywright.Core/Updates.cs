using System;

namespace Querywright.Core
{
    public static class Updates
    {
        public const string Repository = "https://github.com/ulysis022219/Querywright/";

        /// <summary>Line a user can paste into a bug report: version, command, error type. Never the message, query text or connection.</summary>
        public static string ErrorLine(string? version, string command, Type error)
        {
            if (command.EndsWith("Async", StringComparison.Ordinal)) command = command.Substring(0, command.Length - 5);
            return "Querywright " + (string.IsNullOrEmpty(version) ? "unknown" : version) + " \u00B7 " + command + " \u00B7 " + error.Name;
        }

        /// <summary>New-issue link with the bug-report form's error field pre-filled.</summary>
        public static string IssueUrl(string errorLine) =>
            Repository + "issues/new?template=bug_report.yml&error=" + Uri.EscapeDataString(errorLine);

        /// <summary>True when release tag "v1.2.3" is newer than the installed "1.2.3.456" (build number ignored).</summary>
        public static bool IsNewer(string? tag, string? installed)
        {
            var latest = Parse(tag?.TrimStart('v', 'V'));
            var current = Parse(installed);
            return latest != null && current != null && latest > current;
        }

        private static Version? Parse(string? text)
        {
            if (!Version.TryParse(text, out var version)) return null;
            return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
        }
    }
}

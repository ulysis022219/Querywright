using System;

namespace Querywright.Core
{
    public static class Updates
    {
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

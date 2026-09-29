using System;
using System.Linq;

namespace Querywright.Core
{
    /// <summary>Edits the "pattern=color;..." tab color rules; patterns match "server/database" as substrings, first match wins.</summary>
    public static class ColorRules
    {
        /// <summary>Pattern for one server: "server/" matches every database on it.</summary>
        public static string ServerPattern(string server) => server.Trim() + "/";

        public static string? Get(string? rules, string pattern) =>
            Split(rules).Where(r => string.Equals(r[0].Trim(), pattern, StringComparison.OrdinalIgnoreCase)).Select(r => r[1].Trim()).FirstOrDefault();

        /// <summary>Puts pattern=color first so it wins over broader rules; a null color removes the pattern.</summary>
        public static string Set(string? rules, string pattern, string? color)
        {
            if (pattern.IndexOfAny(new[] { '=', ';' }) >= 0 || (color != null && color.IndexOfAny(new[] { '=', ';' }) >= 0))
                throw new ArgumentException("Server names and colors cannot contain '=' or ';'.");
            var kept = Split(rules).Where(r => !string.Equals(r[0].Trim(), pattern, StringComparison.OrdinalIgnoreCase)).Select(r => r[0].Trim() + "=" + r[1].Trim());
            return string.Join(";", (color == null ? kept : new[] { pattern + "=" + color }.Concat(kept)));
        }

        private static string[][] Split(string? rules) =>
            (rules ?? "").Split(';').Select(r => r.Split('=')).Where(r => r.Length == 2 && r[0].Trim().Length > 0).ToArray();
    }
}

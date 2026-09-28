using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Querywright.Core
{
    public sealed class SnippetExpansion
    {
        public string Text { get; }
        public int Caret { get; }
        public int SelectionStart { get; }
        public int SelectionLength { get; }

        internal SnippetExpansion(string text, int caret, int start, int length)
        {
            Text = text;
            Caret = caret;
            SelectionStart = start;
            SelectionLength = length;
        }
    }

    public static class Snippets
    {
        private static readonly Regex Tokens = new Regex(
            @"\$(?<name>[A-Z]+)(?:\((?<format>[^\r\n$]*)\))?\$",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        // Context values are inserted literally, never interpreted as more placeholders.
        public static SnippetExpansion Expand(string template,
            IReadOnlyDictionary<string, string> context, DateTimeOffset now)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (template.Length > 1_000_000)
                throw new ArgumentException("Snippet exceeds 1,000,000 characters.", nameof(template));

            var output = new StringBuilder();
            int offset = 0, caret = -1, start = -1, end = -1;
            foreach (Match token in Tokens.Matches(template))
            {
                output.Append(template, offset, token.Index - offset);
                string name = token.Groups["name"].Value;
                bool formatted = token.Groups["format"].Success;
                if (formatted && name != "DATE" && name != "TIME")
                    throw new FormatException("Only DATE and TIME accept formats.");
                switch (name)
                {
                    case "CURSOR":
                        if (caret >= 0) throw new FormatException("Duplicate CURSOR marker.");
                        caret = output.Length;
                        break;
                    case "SELECTIONSTART":
                        if (start >= 0 || end >= 0) throw new FormatException("Invalid selection markers.");
                        start = output.Length;
                        break;
                    case "SELECTIONEND":
                        if (start < 0 || end >= 0) throw new FormatException("Invalid selection markers.");
                        end = output.Length;
                        break;
                    case "DATE":
                    case "TIME":
                        string format = formatted ? token.Groups["format"].Value
                            : name == "DATE" ? "yyyy-MM-dd" : "HH:mm:ss";
                        if (format.Length == 0) throw new FormatException("Empty date/time format.");
                        output.Append(now.ToString(format, CultureInfo.InvariantCulture));
                        break;
                    case "USER":
                    case "PASTE":
                    case "MACHINE":
                    case "SERVER":
                    case "DBNAME":
                        if (!context.TryGetValue(name, out var value) || value == null)
                            throw new FormatException("Missing snippet context: " + name);
                        output.Append(value);
                        break;
                    default:
                        // Preserve unknown placeholders; SQL may contain literal dollar-delimited text.
                        output.Append(token.Value);
                        break;
                }
                offset = token.Index + token.Length;
            }
            output.Append(template, offset, template.Length - offset);
            if (start >= 0 && end < 0) throw new FormatException("Missing SELECTIONEND marker.");
            return new SnippetExpansion(output.ToString(), caret < 0 ? output.Length : caret,
                start < 0 ? 0 : start, start < 0 ? 0 : end - start);
        }
    }
}

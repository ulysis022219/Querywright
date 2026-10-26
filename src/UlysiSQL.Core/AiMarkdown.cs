using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace UlysiSQL.Core
{
    public enum AiBlockKind { Paragraph, Heading, ListItem, Code, Table }

    public sealed class AiInline
    {
        public string Text { get; }
        public bool Bold { get; }
        public bool Italic { get; }
        public bool Code { get; }
        internal AiInline(string text, bool bold = false, bool italic = false, bool code = false)
        { Text = text; Bold = bold; Italic = italic; Code = code; }
    }

    public sealed class AiBlock
    {
        public AiBlockKind Kind { get; }
        public string Text { get; }
        public string Language { get; }
        public int Level { get; }
        public string Prefix { get; }
        public bool Closed { get; }
        public IReadOnlyList<AiInline> Inlines { get; }
        public bool IsSql => Kind == AiBlockKind.Code && (Language.Length == 0 ||
            new[] { "sql", "tsql", "t-sql", "mssql" }.Contains(Language, StringComparer.OrdinalIgnoreCase));
        internal AiBlock(AiBlockKind kind, string text, string language = "", int level = 0, string prefix = "", bool closed = true)
        { Kind = kind; Text = text; Language = language; Level = level; Prefix = prefix; Closed = closed; Inlines = kind == AiBlockKind.Code || kind == AiBlockKind.Table ? Array.Empty<AiInline>() : AiMarkdown.Inlines(text); }
    }

    /// <summary>Text-only Markdown subset; never interprets URLs, HTML or images.</summary>
    public static class AiMarkdown
    {
        private static readonly Regex Structure = new Regex(@"^(#{1,6})\s+(.+)$|^([-+*]|\d+[.)])\s+(.+)$", RegexOptions.Compiled);
        private static readonly Regex Inline = new Regex(@"\*\*([^*\r\n]+)\*\*|\*([^*\r\n]+)\*|`([^`\r\n]+)`", RegexOptions.Compiled);

        // ponytail: flat Markdown emphasis and tables, use a full parser only if nested CommonMark is needed.
        public static IReadOnlyList<AiBlock> Parse(string text)
        {
            var blocks = new List<AiBlock>();
            var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length;)
            {
                string line = lines[i].TrimStart();
                if (line.Length == 0) { i++; continue; }
                if (Fence(line, out char marker, out int width))
                {
                    string language = line.Substring(width).Trim();
                    int start = ++i;
                    while (i < lines.Length && !CloseFence(lines[i].Trim(), marker, width)) i++;
                    blocks.Add(new AiBlock(AiBlockKind.Code, string.Join("\n", lines.Skip(start).Take(i - start)), language, closed: i < lines.Length));
                    if (i < lines.Length) i++;
                    continue;
                }
                var match = Structure.Match(line);
                if (match.Success)
                {
                    bool heading = match.Groups[1].Success;
                    blocks.Add(new AiBlock(heading ? AiBlockKind.Heading : AiBlockKind.ListItem,
                        match.Groups[heading ? 2 : 4].Value, level: heading ? match.Groups[1].Length : 0,
                        prefix: heading ? "" : (char.IsDigit(match.Groups[3].Value[0]) ? match.Groups[3].Value : "•")));
                    i++;
                    continue;
                }
                bool table = i + 1 < lines.Length && line.Contains("|") && TableRule(lines[i + 1]);
                int from = i++;
                if (table)
                {
                    while (i < lines.Length && lines[i].Contains("|") && lines[i].Trim().Length > 0) i++;
                }
                else
                {
                    while (i < lines.Length && lines[i].Trim().Length > 0 && !Structure.IsMatch(lines[i].TrimStart()) &&
                        !Fence(lines[i].TrimStart(), out _, out _) && !(i + 1 < lines.Length && lines[i].Contains("|") && TableRule(lines[i + 1]))) i++;
                }
                blocks.Add(new AiBlock(table ? AiBlockKind.Table : AiBlockKind.Paragraph, string.Join("\n", lines.Skip(from).Take(i - from))));
            }
            return blocks;
        }

        private static bool Fence(string line, out char marker, out int width)
        {
            marker = line.Length > 0 ? line[0] : '\0'; width = 0;
            if (marker != '`' && marker != '~') return false;
            while (width < line.Length && line[width] == marker) width++;
            return width >= 3;
        }

        private static bool CloseFence(string line, char marker, int width) =>
            line.Length >= width && line.All(c => c == marker);

        private static bool TableRule(string line) => line.Contains("|") && line.Contains("-") && line.Trim().All(c => c == '|' || c == '-' || c == ':' || char.IsWhiteSpace(c));

        /// <summary>Cells of a Markdown table block, header row first, the alignment rule dropped; short rows padded to the widest.</summary>
        public static IReadOnlyList<IReadOnlyList<string>> TableRows(string table)
        {
            var rows = new List<List<string>>();
            foreach (string raw in (table ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || TableRule(line)) continue;
                if (line.StartsWith("|")) line = line.Substring(1);
                if (line.EndsWith("|") && !line.EndsWith("\\|")) line = line.Substring(0, line.Length - 1);
                rows.Add(Regex.Split(line, @"(?<!\\)\|").Select(c => c.Trim().Replace("\\|", "|")).ToList());
            }
            int width = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
            foreach (var row in rows) while (row.Count < width) row.Add("");
            return rows;
        }

        public static IReadOnlyList<AiInline> Inlines(string text)
        {
            var runs = new List<AiInline>();
            int at = 0;
            foreach (Match match in Inline.Matches(text))
            {
                if (match.Index > at) runs.Add(new AiInline(text.Substring(at, match.Index - at)));
                runs.Add(new AiInline(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value,
                    match.Groups[1].Success, match.Groups[2].Success, match.Groups[3].Success));
                at = match.Index + match.Length;
            }
            if (at < text.Length) runs.Add(new AiInline(text.Substring(at)));
            return runs;
        }

        /// <summary>ScriptDom token colors, preserving even unfinished or invalid SQL verbatim.</summary>
        public static IReadOnlyList<(string Text, string Kind)> SqlTokens(string sql)
        {
            var runs = new List<(string, string)>();
            int at = 0;
            foreach (var token in new TSql170Parser(true).GetTokenStream(new StringReader(sql), out _))
            {
                if (token.TokenType == TSqlTokenType.EndOfFile) continue;
                if (token.Offset > at) runs.Add((sql.Substring(at, token.Offset - at), "text"));
                string kind = token.TokenType == TSqlTokenType.SingleLineComment || token.TokenType == TSqlTokenType.MultilineComment ? "comment" :
                    token.TokenType == TSqlTokenType.AsciiStringLiteral || token.TokenType == TSqlTokenType.UnicodeStringLiteral ? "string" :
                    token.TokenType != TSqlTokenType.Identifier && token.TokenType != TSqlTokenType.QuotedIdentifier &&
                    token.Text.Length > 0 && char.IsLetter(token.Text[0]) ? "keyword" : "text";
                runs.Add((token.Text, kind));
                at = token.Offset + token.Text.Length;
            }
            if (at < sql.Length) runs.Add((sql.Substring(at), "text"));
            return runs;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Querywright.Core
{
    public enum AiTask { Generate, Edit, Explain, Fix, Optimize, Complete, Chat }

    public sealed class AiMessage
    {
        /// <summary>"system", "user" or "assistant".</summary>
        public string Role { get; }
        public string Content { get; }
        public AiMessage(string role, string content) { Role = role; Content = content; }
    }

    /// <summary>Builds provider requests and reads replies for any OpenAI-compatible chat endpoint (DeepSeek, OpenAI, OpenRouter,
    /// Groq, Ollama, LM Studio) or the Anthropic messages endpoint. No network here; the caller posts the body.</summary>
    public static class AiAssist
    {
        private const int MaxSql = 60_000, MaxExtra = 60_000;

        private const string Rules =
            "You are a Microsoft SQL Server (T-SQL) assistant working inside SQL Server Management Studio. " +
            "Write T-SQL only, never another SQL dialect. Use only tables and columns from the schema given; if something needed is missing, say so instead of inventing names. " +
            "Schema-qualify object names and end statements with semicolons.";

        private const string SqlOnly = " Reply with one ```sql code block holding the complete result, then at most two short sentences about what changed or what to check.";

        /// <summary>System and user messages for one task. <paramref name="extra"/> is the server's error list (Fix) or the execution plan (Optimize).</summary>
        public static IReadOnlyList<AiMessage> Messages(AiTask task, string? instruction, string? sql, string? schema, string? extra = null)
        {
            instruction = (instruction ?? "").Trim();
            sql = Clip(sql ?? "", MaxSql);
            extra = Clip(extra ?? "", MaxExtra);
            string system = Rules, ask;
            switch (task)
            {
                case AiTask.Generate:
                    if (instruction.Length == 0) throw new InvalidOperationException("Describe the query to write.");
                    system += SqlOnly;
                    ask = "Write T-SQL for this request:\n" + instruction + Block("Script already in the editor, for context", sql);
                    break;
                case AiTask.Edit:
                    if (instruction.Length == 0) throw new InvalidOperationException("Describe the change to make.");
                    RequireSql(sql);
                    system += SqlOnly + " Keep everything the request does not ask to change, including comments and formatting.";
                    ask = "Change this T-SQL as requested.\nRequest: " + instruction + Block("T-SQL", sql);
                    break;
                case AiTask.Explain:
                    RequireSql(sql);
                    system += " Explain in plain language: what the script does step by step, which tables it reads and writes, and anything risky or surprising. No code block unless a short excerpt helps.";
                    ask = (instruction.Length > 0 ? instruction + "\n" : "Explain this T-SQL.") + Block("T-SQL", sql);
                    break;
                case AiTask.Fix:
                    RequireSql(sql);
                    system += SqlOnly + " Fix only what causes the errors; do not restyle or rewrite the rest.";
                    ask = "This T-SQL fails. Correct it." + (instruction.Length > 0 ? "\nNote: " + instruction : "")
                        + Block("Errors reported by SQL Server", extra.Length > 0 ? extra : "(none captured; look for syntax and name errors)") + Block("T-SQL", sql);
                    break;
                case AiTask.Optimize:
                    RequireSql(sql);
                    system += SqlOnly + " The rewrite must return the same rows. List index suggestions separately as CREATE INDEX statements inside a comment, never run them.";
                    ask = "Make this T-SQL faster." + (instruction.Length > 0 ? "\nNote: " + instruction : "")
                        + Block("Estimated execution plan (XML, may be cut short)", extra) + Block("T-SQL", sql);
                    break;
                case AiTask.Complete:
                    system += " Continue the script at the marker <CURSOR>. Reply with only the text to insert there: no code fence, no explanation, no repetition of text that is already present.";
                    ask = "Complete at <CURSOR>:\n" + sql;
                    break;
                default:
                    if (instruction.Length == 0) throw new InvalidOperationException("Type a question.");
                    system += " When you give SQL, put it in a ```sql code block.";
                    ask = instruction + Block("Script in the editor", sql);
                    break;
            }
            if (!string.IsNullOrWhiteSpace(schema)) ask += Block("Database schema (table(column type, ...); -> marks a foreign key)", schema!);
            return new[] { new AiMessage("system", system), new AiMessage("user", ask) };
        }

        private static void RequireSql(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) throw new InvalidOperationException("Select the SQL to work on, or open a query window that has some.");
        }

        private static string Block(string title, string body) => string.IsNullOrWhiteSpace(body) ? "" : "\n\n" + title + ":\n" + body.Trim('\r', '\n');

        private static string Clip(string text, int max) => text.Length <= max ? text : text.Substring(0, max) + "\n-- (cut: too long to send)";

        /// <summary>Text before and after the caret with a marker between, windowed so a long script stays cheap to send.</summary>
        public static string CompletionWindow(string sql, int caret, int before = 6000, int after = 1500)
        {
            caret = Math.Max(0, Math.Min(caret, sql.Length));
            int from = Math.Max(0, caret - before), to = Math.Min(sql.Length, caret + after);
            return sql.Substring(from, caret - from) + "<CURSOR>" + sql.Substring(caret, to - caret);
        }

        /// <summary>URL to post to: the base URL as typed in options, with the chat path added when it is not already there.</summary>
        public static string Endpoint(string baseUrl, bool anthropic)
        {
            string url = (baseUrl ?? "").Trim().TrimEnd('/');
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
                throw new InvalidOperationException("Set the AI provider base URL under Tools > Options > UlysiSQL > AI (for example https://api.deepseek.com).");
            if (anthropic) return url.EndsWith("/messages", StringComparison.OrdinalIgnoreCase) ? url : url + (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? "/messages" : "/v1/messages");
            return url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? url : url + "/chat/completions";
        }

        public static string RequestBody(string model, IReadOnlyList<AiMessage> messages, bool anthropic, int? maxTokens = null, double? temperature = 0.1, bool stream = false,
            AiReasoningLevel reasoning = AiReasoningLevel.Off, string? extraRequestJson = null, string? baseUrl = null)
        {
            if (string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException("Set the AI model name under Tools > Options > UlysiSQL > AI.");
            int tokenLimit = maxTokens ?? 4096;
            if (tokenLimit <= 0) throw new ArgumentOutOfRangeException(nameof(maxTokens));
            if (temperature.HasValue && (double.IsNaN(temperature.Value) || double.IsInfinity(temperature.Value) || temperature.Value < 0 || temperature.Value > 2))
                throw new ArgumentOutOfRangeException(nameof(temperature));
            var body = new Dictionary<string, object?> { ["model"] = model.Trim() };
            if (anthropic)
            {
                // The messages endpoint takes the system text as its own field and requires max_tokens.
                string system = string.Join("\n\n", messages.Where(m => m.Role == "system").Select(m => m.Content));
                if (system.Length > 0) body["system"] = system;
                body["max_tokens"] = tokenLimit;
                body["messages"] = messages.Where(m => m.Role != "system").Select(Pair).ToList();
            }
            else
            {
                body["messages"] = messages.Select(Pair).ToList();
                body["max_tokens"] = tokenLimit;
            }
            body["stream"] = stream;
            if (temperature.HasValue) body["temperature"] = temperature.Value;
            foreach (var option in AiProvider.RequestOptions(anthropic, model, reasoning, maxTokens, baseUrl)) body[option.Key] = option.Value;
            if (body.ContainsKey("max_completion_tokens")) body.Remove("max_tokens");
            if (stream && !anthropic && (AiProvider.Host(baseUrl, "api.openai.com") || AiProvider.Host(baseUrl, "api.deepseek.com")))
                body["stream_options"] = new Dictionary<string, object?> { ["include_usage"] = true };
            AiProvider.MergeExtra(body, extraRequestJson, reasoning);
            if (reasoning != AiReasoningLevel.Off || AiProvider.Host(baseUrl, anthropic ? "api.anthropic.com" : "api.openai.com")) body.Remove("temperature");
            if (body.TryGetValue("temperature", out var sentTemperature) && sentTemperature == null) body.Remove("temperature");
            if (!stream) body.Remove("stream_options");
            if (anthropic && MiniJson.Text(MiniJson.Get(body, "thinking"), "type") == "enabled")
            {
                int? budget = AiProvider.TokenCount(MiniJson.Get(body, "thinking"), "budget_tokens");
                if (!budget.HasValue || budget.Value < 1024) throw new InvalidOperationException("Anthropic thinking budget_tokens must be an integer of at least 1024.");
                int cap = AiProvider.TokenCount(body, "max_tokens") ?? tokenLimit;
                if (cap <= budget.Value) body["max_tokens"] = checked(budget.Value + tokenLimit);
            }
            return MiniJson.Write(body);
        }

        private static object Pair(AiMessage message) => new Dictionary<string, object?> { ["role"] = message.Role, ["content"] = message.Content };

        /// <summary>The assistant's text from a provider response; throws with the provider's own message when it reports an error.</summary>
        public static string ReplyText(string json, bool anthropic) => ReplyText(json, anthropic, false);

        internal static string ReplyText(string json, bool anthropic, bool allowEmpty)
        {
            object? root;
            try { root = MiniJson.Parse(json); }
            catch (FormatException) { throw new InvalidOperationException("The AI provider did not answer with JSON. Check the base URL. First characters: " + Clip(json.Trim(), 120)); }
            var error = MiniJson.Get(root, "error");
            if (error != null) throw new InvalidOperationException("AI provider error: " + (error as string ?? MiniJson.Text(error, "message") ?? "unknown"));
            string? text;
            if (anthropic)
                text = MiniJson.Get(root, "content") is List<object?> parts
                    ? string.Concat(parts.Where(p => MiniJson.Text(p, "type") == "text").Select(p => MiniJson.Text(p, "text"))) : null;
            else
            {
                var first = (MiniJson.Get(root, "choices") as List<object?>)?.FirstOrDefault();
                var content = MiniJson.Get(MiniJson.Get(first, "message"), "content");
                // Some gateways return content as a list of parts.
                text = content as string ?? (content is List<object?> list ? string.Concat(list.Where(p => MiniJson.Text(p, "type") == null || MiniJson.Text(p, "type") == "text").Select(p => MiniJson.Text(p, "text"))) : null);
            }
            if (!allowEmpty && string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("The AI provider returned an empty answer.");
            return (text ?? "").Trim();
        }

        private static readonly Regex Fence = new Regex(@"```[ \t]*([A-Za-z0-9_+-]*)[ \t]*\r?\n(.*?)\r?\n?[ \t]*```", RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>The SQL in a reply: its code blocks joined (sql-tagged ones when any are tagged), or null when it has none.</summary>
        public static string? ExtractSql(string reply)
        {
            var blocks = Fence.Matches(reply ?? "").Cast<Match>().ToList();
            if (blocks.Count == 0) return null;
            var tagged = blocks.Where(b => b.Groups[1].Value.Equals("sql", StringComparison.OrdinalIgnoreCase) || b.Groups[1].Value.Equals("tsql", StringComparison.OrdinalIgnoreCase)).ToList();
            return string.Join("\r\n\r\n", (tagged.Count > 0 ? tagged : blocks).Select(b => b.Groups[2].Value.Trim('\r', '\n')));
        }

        /// <summary>For Complete: the reply with any code fence and marker stripped.</summary>
        public static string CompletionText(string reply)
        {
            string text = ExtractSql(reply) ?? reply ?? "";
            return text.Replace("<CURSOR>", "");
        }

        private static readonly Regex Go = new Regex(@"^[ \t]*GO[ \t]*(\d+[ \t]*)?(--[^\r\n]*)?\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        /// <summary>Batches between GO lines, with the 1-based line each starts on (the GO line itself for a batch after one, as its text begins at that line's end).</summary>
        // ponytail: a line holding only GO inside a string or block comment splits too, as it does in sqlcmd.
        public static IReadOnlyList<(string Text, int Line)> Batches(string sql)
        {
            var batches = new List<(string, int)>();
            if (sql == null) return batches;
            int start = 0, line = 1;
            void Add(int end)
            {
                string text = sql.Substring(start, end - start);
                if (!string.IsNullOrWhiteSpace(text)) batches.Add((text, line));
                for (int i = start; i < end; i++) if (sql[i] == '\n') line++;
            }
            foreach (Match go in Go.Matches(sql))
            {
                Add(go.Index);
                start = go.Index + go.Length;
            }
            Add(sql.Length);
            return batches;
        }

        private static readonly Regex Word = new Regex(@"[A-Za-z_][A-Za-z0-9_]{2,}", RegexOptions.Compiled);

        /// <summary>Schema lines for the tables a request or script most likely concerns: tables named in <paramref name="text"/>
        /// (whole or as words of the name), then their foreign-key neighbours. A small catalog is sent whole.</summary>
        // ponytail: name matching, not embeddings; "customers who bought" finds Customer/Customers but not a table named Buyer.
        public static string SchemaContext(IReadOnlyList<SchemaTable>? tables, string? text, int maxTables = 30, int maxChars = 24_000)
            => SchemaContext(tables, text, out _, maxTables, maxChars);

        public static string SchemaContext(IReadOnlyList<SchemaTable>? tables, string? text, out int tableCount, int maxTables = 30, int maxChars = 24_000)
        {
            tableCount = 0;
            if (tables == null || tables.Count == 0 || maxTables <= 0) return "";
            var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match word in Word.Matches(text ?? ""))
            {
                words.Add(word.Value);
                if (word.Value.EndsWith("ies", StringComparison.OrdinalIgnoreCase)) words.Add(word.Value.Substring(0, word.Value.Length - 3) + "y");
                else if (word.Value.EndsWith("s", StringComparison.OrdinalIgnoreCase)) words.Add(word.Value.Substring(0, word.Value.Length - 1));
                else words.Add(word.Value + "s");
            }
            int Score(SchemaTable table)
            {
                if (words.Contains(table.Name)) return 100;
                int score = NameParts(table.Name).Count(words.Contains) * 10;
                return score + Math.Min(5, table.Columns.Count(words.Contains));
            }
            var chosen = new List<SchemaTable>();
            if (tables.Count <= maxTables) chosen.AddRange(tables);
            else
            {
                var ranked = tables.Select(t => (Table: t, Score: Score(t))).Where(p => p.Score > 0).OrderByDescending(p => p.Score).Select(p => p.Table).Take(maxTables).ToList();
                var seen = new HashSet<string>(ranked.Select(Key), StringComparer.OrdinalIgnoreCase);
                var byKey = new Dictionary<string, SchemaTable>(StringComparer.OrdinalIgnoreCase);
                foreach (var table in tables) byKey[Key(table)] = table;
                chosen.AddRange(ranked);
                // Neighbours of the direct matches only, so one hub table cannot pull in the whole catalog.
                foreach (var table in ranked)
                    foreach (var key in table.ForeignKeys)
                        if (chosen.Count < maxTables && byKey.TryGetValue(key.ReferencedSchema + "." + key.ReferencedTable, out var parent) && seen.Add(Key(parent))) chosen.Add(parent);
                foreach (var table in tables)
                    if (chosen.Count < maxTables && !seen.Contains(Key(table)) && table.ForeignKeys.Any(k => ranked.Any(r => SameTable(r, k))) && seen.Add(Key(table))) chosen.Add(table);
            }
            var lines = new StringBuilder();
            foreach (var table in chosen)
            {
                string line = Line(table);
                if (lines.Length + line.Length > maxChars) break;
                lines.Append(line).Append('\n');
                tableCount++;
            }
            chosen = chosen.Take(tableCount).ToList();
            if (chosen.Count < tables.Count)
            {
                // Names only for the rest, so the model can say which table it would need rather than invent one.
                var rest = tables.Where(t => !chosen.Contains(t)).Select(Key).Take(300).ToList();
                string names = "Other objects (columns not shown): " + string.Join(", ", rest) + (tables.Count - chosen.Count > rest.Count ? ", ..." : "");
                if (lines.Length + names.Length <= maxChars + 6000) lines.Append(names).Append('\n');
            }
            return lines.ToString().TrimEnd('\n');
        }

        /// <summary>Conservative run warning from ScriptDom tokens; quoted names, strings and comments cannot trigger it.</summary>
        public static IReadOnlyList<string> SqlChanges(string sql)
        {
            var parser = new TSql170Parser(true);
            var script = parser.ParseSafe(sql, out var errors);
            var changes = new List<string>();
            var words = new HashSet<string>(new[] { "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "DROP", "ALTER", "CREATE", "EXEC", "EXECUTE", "GRANT", "REVOKE", "DENY",
                "BACKUP", "RESTORE", "DBCC", "KILL", "SHUTDOWN", "RECONFIGURE", "WRITETEXT", "UPDATETEXT" }, StringComparer.OrdinalIgnoreCase);
            // SQL too deep to parse still gets the keyword scan: the lexer is iterative.
            foreach (var token in script.ScriptTokenStream ?? parser.GetTokenStream(new StringReader(sql), out _))
            {
                if (token.TokenType == TSqlTokenType.EndOfFile || token.TokenType == TSqlTokenType.Identifier || token.TokenType == TSqlTokenType.QuotedIdentifier) continue;
                string word = token.Text.ToUpperInvariant();
                if (words.Contains(word) && !changes.Contains(word)) changes.Add(word);
                if (token.TokenType == TSqlTokenType.Into && !changes.Contains("INTO")) changes.Add("INTO");
            }
            var execution = new ExecutionWarning(); script.Accept(execution);
            if (execution.Executes && !changes.Contains("EXEC") && !changes.Contains("EXECUTE")) changes.Add("EXEC");
            // ENABLE and DISABLE are not reserved words, so the token scan cannot see them.
            if (execution.Triggers != null && !changes.Contains(execution.Triggers)) changes.Add(execution.Triggers);
            if (errors.Count > 0) changes.Add("Unrecognized SQL (parse errors; effects unknown)");
            return changes;
        }

        private sealed class ExecutionWarning : TSqlFragmentVisitor
        {
            internal bool Executes;
            internal string? Triggers;
            public override void ExplicitVisit(ExecuteStatement node) { Executes = true; }
            public override void ExplicitVisit(EnableDisableTriggerStatement node) { Triggers = node.TriggerEnforcement == TriggerEnforcement.Enable ? "ENABLE TRIGGER" : "DISABLE TRIGGER"; }
        }

        private static string Key(SchemaTable table) => table.Schema + "." + table.Name;

        private static bool SameTable(SchemaTable table, SchemaForeignKey key) =>
            string.Equals(table.Schema, key.ReferencedSchema, StringComparison.OrdinalIgnoreCase) && string.Equals(table.Name, key.ReferencedTable, StringComparison.OrdinalIgnoreCase);

        // OrderDetail, order_detail and tblOrder all yield "Order".
        private static IEnumerable<string> NameParts(string name) =>
            Regex.Matches(name, @"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|\d+").Cast<Match>().Select(m => m.Value).Where(p => p.Length > 2);

        private static string Line(SchemaTable table)
        {
            var line = new StringBuilder(Key(table)).Append(table.IsView ? " [view](" : "(");
            for (int i = 0; i < table.Columns.Count; i++)
            {
                if (i > 0) line.Append(", ");
                line.Append(table.Columns[i]);
                string? type = table.ColumnTypes != null && i < table.ColumnTypes.Count ? table.ColumnTypes[i] : null;
                if (!string.IsNullOrEmpty(type)) line.Append(' ').Append(type);
                var key = table.ForeignKeys.FirstOrDefault(k => k.Columns.Count == 1 && string.Equals(k.Columns[0], table.Columns[i], StringComparison.OrdinalIgnoreCase));
                if (key != null) line.Append(" -> ").Append(key.ReferencedSchema).Append('.').Append(key.ReferencedTable).Append('.').Append(key.ReferencedColumns[0]);
            }
            line.Append(')');
            foreach (var key in table.ForeignKeys.Where(k => k.Columns.Count > 1))
                line.Append(" FK(").Append(string.Join(", ", key.Columns)).Append(") -> ").Append(key.ReferencedSchema).Append('.').Append(key.ReferencedTable)
                    .Append('(').Append(string.Join(", ", key.ReferencedColumns)).Append(')');
            return line.ToString();
        }
    }
}

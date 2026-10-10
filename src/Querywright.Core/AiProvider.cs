using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Querywright.Core
{
    public enum AiReasoningLevel { Off, Low, Medium, High }

    public sealed class AiReply
    {
        public string Text { get; }
        public string Reasoning { get; }
        public int? InputTokens { get; }
        public int? OutputTokens { get; }
        public int? ReasoningTokens { get; }
        public bool ReasoningUnsupported { get; set; }
        public AiReply(string text, string reasoning = "", int? inputTokens = null, int? outputTokens = null, int? reasoningTokens = null)
        { Text = text; Reasoning = reasoning; InputTokens = inputTokens; OutputTokens = outputTokens; ReasoningTokens = reasoningTokens; }
    }

    /// <summary>Provider metadata, reasoning options and response parsing. No network or model-name catalog.</summary>
    public static class AiProvider
    {
        public static IReadOnlyList<string> Models(string json)
        {
            var root = MiniJson.Parse(json);
            if (MiniJson.Get(root, "error") != null || !(MiniJson.Get(root, "data") is List<object?> data))
                throw new InvalidOperationException("The AI provider did not return a model list. Type a model name instead.");
            return data.Select(item => MiniJson.Text(item, "id")).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).Distinct(StringComparer.Ordinal).ToArray();
        }

        public static string? ModelsNext(string json)
        {
            var root = MiniJson.Parse(json);
            if (!(MiniJson.Get(root, "has_more") is true)) return null;
            string? cursor = MiniJson.Text(root, "last_id");
            if (string.IsNullOrWhiteSpace(cursor)) throw new InvalidOperationException("The AI provider omitted the next model-list cursor.");
            return cursor;
        }

        public static AiReply Reply(string json, bool anthropic)
        {
            string text = AiAssist.ReplyText(json, anthropic);
            var root = MiniJson.Parse(json);
            string reasoning = anthropic
                ? string.Concat((MiniJson.Get(root, "content") as List<object?> ?? new List<object?>()).Where(part => MiniJson.Text(part, "type") == "thinking").Select(part => MiniJson.Text(part, "thinking")))
                : MiniJson.Text(MiniJson.Get((MiniJson.Get(root, "choices") as List<object?>)?.FirstOrDefault(), "message"), "reasoning_content") ?? "";
            var usage = MiniJson.Get(root, "usage");
            return new AiReply(text, reasoning, TokenCount(usage, anthropic ? "input_tokens" : "prompt_tokens"), TokenCount(usage, anthropic ? "output_tokens" : "completion_tokens"),
                TokenCount(MiniJson.Get(usage, anthropic ? "output_tokens_details" : "completion_tokens_details"), anthropic ? "thinking_tokens" : "reasoning_tokens"));
        }

        internal static int? TokenCount(object? usage, string field)
        {
            object? value = MiniJson.Get(usage, field);
            if (value is int count) return count >= 0 ? count : (int?)null;
            return value is double number && number >= 0 && number <= int.MaxValue && number == Math.Truncate(number) ? (int?)number : null;
        }

        private static bool ReasoningField(string name) => name.Equals("think", StringComparison.OrdinalIgnoreCase)
            || name.Equals("effort", StringComparison.OrdinalIgnoreCase) || name.Equals("budget_tokens", StringComparison.OrdinalIgnoreCase)
            || name.IndexOf("reasoning", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("thinking", StringComparison.OrdinalIgnoreCase) >= 0;

        internal static bool Host(string? baseUrl, string host) => Uri.TryCreate(baseUrl, UriKind.Absolute, out var url)
            && url.Host.Equals(host, StringComparison.OrdinalIgnoreCase);

        public static Dictionary<string, object?> RequestOptions(bool anthropic, string model, AiReasoningLevel level, int maxTokens = 4096, string? baseUrl = null)
        {
            if (string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException("Choose an AI model name.");
            if (maxTokens <= 0) throw new ArgumentOutOfRangeException(nameof(maxTokens));
            if ((uint)level > (uint)AiReasoningLevel.High) throw new ArgumentOutOfRangeException(nameof(level));
            var fields = new Dictionary<string, object?>();
            if (!anthropic && Host(baseUrl, "api.openai.com")) fields["max_completion_tokens"] = maxTokens;
            if (level == AiReasoningLevel.Off) return fields;
            fields["temperature"] = null;
            if (anthropic)
            {
                // ponytail: manual budgets; use Extra request JSON for models requiring adaptive thinking.
                int budget = level == AiReasoningLevel.Low ? 1024 : level == AiReasoningLevel.Medium ? 4096 : 8192;
                fields["thinking"] = new Dictionary<string, object?> { ["type"] = "enabled", ["budget_tokens"] = budget };
                fields["max_tokens"] = checked(maxTokens + budget);
            }
            else
            {
                fields["reasoning_effort"] = level.ToString().ToLowerInvariant();
                if (Host(baseUrl, "api.deepseek.com"))
                {
                    fields["thinking"] = new Dictionary<string, object?> { ["type"] = "enabled" };
                    if (level == AiReasoningLevel.Medium) fields["reasoning_effort"] = "high";
                }
            }
            return fields;
        }

        /// <summary>Off omits reasoning request fields, including custom nested provider options.</summary>
        public static string? WithoutReasoningJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            var extra = Extra(json!);
            RemoveReasoning(extra);
            return MiniJson.Write(extra);
        }

        private static void RemoveReasoning(object? node)
        {
            if (node is Dictionary<string, object?> map)
                foreach (var key in map.Keys.ToArray())
                    if (ReasoningField(key)) map.Remove(key); else RemoveReasoning(map[key]);
            else if (node is List<object?> list) foreach (var item in list) RemoveReasoning(item);
        }

        private static readonly string[] Credentials = { "key", "api_key", "api-key", "apikey", "x-api-key", "x_api_key", "token", "api_token", "auth", "authentication", "authorization", "access_token", "bearer", "bearer_token", "password", "secret", "client_secret", "credentials", "headers" };

        private static Dictionary<string, object?> Extra(string json)
        {
            if (json.Length > 16_384) throw new InvalidOperationException("Extra request JSON is too large (maximum 16384 characters).");
            Dictionary<string, object?>? extra;
            try { extra = MiniJson.Parse(json) as Dictionary<string, object?>; }
            catch (FormatException) { throw new InvalidOperationException("Extra request JSON must be a JSON object."); }
            if (extra == null) throw new InvalidOperationException("Extra request JSON must be a JSON object.");
            if (extra.Keys.Any(key => new[] { "messages", "system", "model", "stream" }.Contains(key, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Extra request JSON cannot replace messages, system, model or stream.");
            ValidateExtra(extra);
            foreach (string field in new[] { "max_tokens", "max_completion_tokens" })
                if (extra.ContainsKey(field) && (!TokenCount(extra, field).HasValue || TokenCount(extra, field) == 0))
                    throw new InvalidOperationException("Extra request JSON token limits must be positive integers.");
            if (extra.TryGetValue("temperature", out var temperature) && temperature != null && (!(temperature is double number) || number < 0 || number > 2))
                throw new InvalidOperationException("Extra request JSON temperature must be a number from 0 to 2, or null to omit it.");
            if (extra.TryGetValue("reasoning_effort", out var effort) && effort != null && !(effort is string))
                throw new InvalidOperationException("Extra request JSON reasoning_effort must be a string or null to omit it.");
            if (extra.TryGetValue("thinking", out var thinking) && thinking != null && !(thinking is Dictionary<string, object?>) && !(thinking is bool))
                throw new InvalidOperationException("Extra request JSON thinking must be an object, boolean or null to omit it.");
            return extra;
        }

        private static void ValidateExtra(object? value)
        {
            if (value is Dictionary<string, object?> map)
                foreach (var item in map)
                {
                    if (Credentials.Contains(item.Key, StringComparer.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Extra request JSON cannot contain credentials or request headers. Use the encrypted API key setting.");
                    ValidateExtra(item.Value);
                }
            else if (value is List<object?> list) foreach (var item in list) ValidateExtra(item);
            else if (value is double number && (double.IsNaN(number) || double.IsInfinity(number)))
                throw new InvalidOperationException("Extra request JSON numbers must be finite.");
        }

        internal static void MergeExtra(Dictionary<string, object?> body, string? json, AiReasoningLevel level)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            var extra = Extra(json!);
            if (level == AiReasoningLevel.Off) RemoveReasoning(extra);
            foreach (var item in extra)
                if (item.Value == null && ReasoningField(item.Key)) body.Remove(item.Key); else body[item.Key] = item.Value;
            if (extra.ContainsKey("max_tokens")) body.Remove("max_completion_tokens");
            else if (extra.ContainsKey("max_completion_tokens")) body.Remove("max_tokens");
            if (body.TryGetValue("temperature", out var temperature) && temperature == null) body.Remove("temperature");
        }

        /// <summary>Retry a rejected reasoning field once, never authentication, billing or unrelated errors.</summary>
        public static bool ReasoningRejected(int statusCode, string body, bool anthropic, string? requestBody = null)
        {
            if (statusCode != 400) return false;
            object? root;
            try { root = MiniJson.Parse(body); } catch (FormatException) { return false; }
            var error = MiniJson.Get(root, "error");
            string message = ((error as string ?? MiniJson.Text(error, "message") ?? "") + " " + MiniJson.Text(error, "param")).ToLowerInvariant();
            if (new[] { "api key", "api_key", "unauthor", "authenticat", "credit", "quota", "billing", "balance", "payment", "rate limit" }.Any(message.Contains)
                || !new[] { "unsupported", "not supported", "not allowed", "unknown", "unrecognized", "invalid", "not permitted", "unexpected", "does not support" }.Any(message.Contains)) return false;
            var fields = new List<string>();
            if (requestBody == null) fields.AddRange(anthropic ? new[] { "thinking", "budget_tokens" } : new[] { "reasoning_effort", "thinking", "reasoning" });
            else
            {
                try { FindFields(MiniJson.Parse(requestBody)); } catch (FormatException) { return false; }
            }
            return fields.Any(field => Regex.IsMatch(message, @"(?<![a-z0-9_])" + Regex.Escape(field.ToLowerInvariant()) + @"(?![a-z0-9_])"));
            void FindFields(object? node)
            {
                if (!(node is Dictionary<string, object?> map)) return;
                foreach (var item in map)
                {
                    if (ReasoningField(item.Key)) fields.Add(item.Key);
                    FindFields(item.Value);
                }
            }
        }
    }
}

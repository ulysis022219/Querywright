using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Querywright.Core
{
    public sealed class AiDiagnosticEntry
    {
        public string AppVersion { get; set; } = "";
        public string RequestId { get; set; } = "";
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
        public string ProviderHost { get; set; } = "";
        public string ApiFormat { get; set; } = "";
        public string Model { get; set; } = "";
        public string Reasoning { get; set; } = "";
        public int MaxOutputTokens { get; set; }
        public int Attempt { get; set; } = 1;
        public bool Streaming { get; set; }
        public string ResponseFormat { get; set; } = "unknown";
        public int? HttpStatus { get; set; }
        public string FinishReason { get; set; } = "";
        public int? InputTokens { get; set; }
        public int? OutputTokens { get; set; }
        public int? ReasoningTokens { get; set; }
        public int AnswerCharacters { get; set; }
        public int ReasoningCharacters { get; set; }
        public long DurationMilliseconds { get; set; }
        public string Outcome { get; set; } = "error";
        public string ErrorCategory { get; set; } = "other";
    }

    /// <summary>Opt-in metadata only: never stores request, response, SQL, credentials or error text.</summary>
    public sealed class AiDiagnostics
    {
        public const int MaxEntries = 100, MaxJsonBytes = 262_144;
        private readonly object gate = new object();
        private readonly List<AiDiagnosticEntry> entries = new List<AiDiagnosticEntry>();
        private bool enabled;
        public bool Enabled { get { lock (gate) return enabled; } set { lock (gate) { enabled = value; if (!value) entries.Clear(); } } }
        public IReadOnlyList<AiDiagnosticEntry> Entries { get { lock (gate) return entries.Select(entry => Read(Map(entry), false)).ToArray(); } }

        public void Add(AiDiagnosticEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            lock (gate)
            {
                if (!enabled) return;
                entries.Add(Read(Map(entry), false));
                if (entries.Count > MaxEntries) entries.RemoveAt(0);
            }
        }

        public string Export()
        {
            lock (gate) return MiniJson.Write(new Dictionary<string, object?> { ["version"] = 1, ["entries"] = entries.Select(Map).ToArray() });
        }

        public void Import(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            if (json.Length > MaxJsonBytes || Encoding.UTF8.GetByteCount(json) > MaxJsonBytes) throw new FormatException("Diagnostic file is too large.");
            object? root = MiniJson.Parse(json);
            if (!(MiniJson.Get(root, "version") is double version) || version != 1 || !(MiniJson.Get(root, "entries") is List<object?> list) || list.Count > MaxEntries)
                throw new FormatException("Diagnostic file must contain version 1 and at most 100 entries.");
            var imported = list.Select(item => Read(item, true)).ToArray();
            lock (gate) { entries.Clear(); entries.AddRange(imported); }
        }

        private static Dictionary<string, object?> Map(AiDiagnosticEntry entry) => new Dictionary<string, object?>
        {
            ["appVersion"] = entry.AppVersion, ["requestId"] = entry.RequestId,
            ["startedUtc"] = entry.StartedUtc.ToString("O", CultureInfo.InvariantCulture),
            ["providerHost"] = entry.ProviderHost, ["apiFormat"] = entry.ApiFormat, ["model"] = entry.Model,
            ["reasoning"] = entry.Reasoning, ["maxOutputTokens"] = entry.MaxOutputTokens, ["attempt"] = entry.Attempt,
            ["streaming"] = entry.Streaming, ["responseFormat"] = entry.ResponseFormat, ["httpStatus"] = entry.HttpStatus,
            ["finishReason"] = entry.FinishReason, ["inputTokens"] = entry.InputTokens, ["outputTokens"] = entry.OutputTokens,
            ["reasoningTokens"] = entry.ReasoningTokens, ["answerCharacters"] = entry.AnswerCharacters,
            ["reasoningCharacters"] = entry.ReasoningCharacters, ["durationMilliseconds"] = entry.DurationMilliseconds,
            ["outcome"] = entry.Outcome, ["errorCategory"] = entry.ErrorCategory
        };

        private static readonly string[] Formats = { "", "OpenAiCompatible", "Anthropic" }, Levels = { "", "off", "low", "medium", "high" }, Responses = { "unknown", "json", "sse" },
            Outcomes = { "success", "empty", "error", "canceled", "retry" },
            Categories = { "none", "http", "network", "timeout", "invalid_json", "empty_answer", "stream", "reasoning_unsupported", "stream_unsupported", "canceled", "configuration", "other" },
            Finishes = { "", "stop", "length", "tool_calls", "content_filter", "function_call", "end_turn", "max_tokens", "stop_sequence", "tool_use", "refusal", "pause_turn", "model_context_window_exceeded", "insufficient_system_resource", "aborted" };

        private static AiDiagnosticEntry Read(object? node, bool strict)
        {
            if (!(node is Dictionary<string, object?>)) throw new FormatException("Diagnostic entry must be an object.");
            string Text(string name, int max, Func<string, bool> valid, string fallback = "")
            {
                if (MiniJson.Get(node, name) is string text && text.Length <= max && valid(text)) return text;
                if (strict) throw new FormatException("Invalid diagnostic field: " + name + ".");
                return fallback;
            }
            long? Number(string name, long min, long max, bool optional = false, long fallback = 0)
            {
                object? value = MiniJson.Get(node, name);
                if (optional && value == null) return null;
                double number = value is int integer ? integer : value is long wide ? wide : value is double floating ? floating : double.NaN;
                if (!double.IsNaN(number) && !double.IsInfinity(number) && number >= min && number <= max && number == Math.Truncate(number)) return (long)number;
                if (strict) throw new FormatException("Invalid diagnostic field: " + name + ".");
                return optional ? null : (long?)fallback;
            }
            string Choice(string name, string[] values, string fallback = "") => Text(name, 64, value => values.Contains(value, StringComparer.Ordinal), fallback);
            string requestId = Text("requestId", 36, value => Guid.TryParse(value, out _), Guid.NewGuid().ToString("N"));
            string started = Text("startedUtc", 40, value => DateTime.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) && date.Kind == DateTimeKind.Utc,
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            object? streaming = MiniJson.Get(node, "streaming");
            if (strict && !(streaming is bool)) throw new FormatException("Invalid diagnostic field: streaming.");
            return new AiDiagnosticEntry
            {
                AppVersion = Text("appVersion", 32, value => value.Length == 0 || Version.TryParse(value, out _)),
                RequestId = Guid.Parse(requestId).ToString("N"),
                StartedUtc = DateTime.ParseExact(started, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                ProviderHost = Text("providerHost", 253, value => value.Length == 0 || Uri.CheckHostName(value) != UriHostNameType.Unknown),
                ApiFormat = Choice("apiFormat", Formats), Model = Text("model", 128, Identifier), Reasoning = Choice("reasoning", Levels),
                MaxOutputTokens = (int)Number("maxOutputTokens", 0, int.MaxValue)!, Attempt = (int)Number("attempt", 1, 16, fallback: 1)!,
                Streaming = streaming is true, ResponseFormat = Choice("responseFormat", Responses, "unknown"),
                HttpStatus = (int?)Number("httpStatus", 100, 599, optional: true), FinishReason = Choice("finishReason", Finishes),
                InputTokens = (int?)Number("inputTokens", 0, int.MaxValue, optional: true), OutputTokens = (int?)Number("outputTokens", 0, int.MaxValue, optional: true),
                ReasoningTokens = (int?)Number("reasoningTokens", 0, int.MaxValue, optional: true),
                AnswerCharacters = (int)Number("answerCharacters", 0, int.MaxValue)!, ReasoningCharacters = (int)Number("reasoningCharacters", 0, int.MaxValue)!,
                DurationMilliseconds = Number("durationMilliseconds", 0, 86_400_000)!.Value,
                Outcome = Choice("outcome", Outcomes, "error"), ErrorCategory = Choice("errorCategory", Categories, "other")
            };
        }

        private static bool Identifier(string value) => !value.StartsWith("sk-", StringComparison.OrdinalIgnoreCase) && !value.Contains("://")
            && value.All(c => c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || "_./:-@".Contains(c));
    }
}

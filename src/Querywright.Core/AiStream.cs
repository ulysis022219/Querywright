using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Querywright.Core
{
    /// <summary>SSE event framing and text deltas for both provider formats. No network or UI.</summary>
    public sealed class AiStream
    {
        private readonly bool anthropic;
        private readonly StringBuilder data = new StringBuilder();
        private string eventName = "";
        public bool Done { get; private set; }
        public string ReasoningDelta { get; private set; } = "";
        public int? InputTokens { get; private set; }
        public int? OutputTokens { get; private set; }
        public int? ReasoningTokens { get; private set; }
        public AiStream(bool anthropic) { this.anthropic = anthropic; }

        public string Line(string line)
        {
            ReasoningDelta = "";
            if (Done) return "";
            line = line.TrimStart('\uFEFF');
            if (line.Length == 0) return Dispatch();
            if (line[0] == ':') return "";
            int colon = line.IndexOf(':');
            string field = colon < 0 ? line : line.Substring(0, colon);
            string value = colon < 0 ? "" : line.Substring(colon + 1);
            if (value.StartsWith(" ", StringComparison.Ordinal)) value = value.Substring(1);
            if (field == "event") eventName = value;
            else if (field == "data")
            {
                if (data.Length + value.Length > 1_000_000) throw new InvalidOperationException("The AI provider sent an oversized stream event.");
                data.Append(value).Append('\n');
            }
            return "";
        }

        public string Finish()
        {
            ReasoningDelta = "";
            string text = Dispatch();
            if (!Done) throw new InvalidOperationException("The AI stream ended before completion. Any partial answer is kept; try again.");
            return text;
        }

        private string Dispatch()
        {
            string payload = data.ToString().TrimEnd('\n'), name = eventName;
            data.Clear(); eventName = "";
            if (payload == "[DONE]") { Done = true; return ""; }
            if (name == "error") throw new InvalidOperationException("The AI provider reported a streaming error. Any partial answer is kept; try again.");
            if (payload.Length == 0) return "";
            object? root;
            try { root = MiniJson.Parse(payload); }
            catch (FormatException) { throw new InvalidOperationException("The AI provider sent an invalid stream event."); }
            if (MiniJson.Get(root, "error") != null || MiniJson.Text(root, "type") == "error")
                throw new InvalidOperationException("The AI provider reported a streaming error. Any partial answer is kept; try again.");
            if (anthropic)
            {
                string? type = MiniJson.Text(root, "type") ?? name;
                ReadUsage(MiniJson.Get(type == "message_start" ? MiniJson.Get(root, "message") : root, "usage"));
                if (type == "message_stop") { Done = true; return ""; }
                var delta = MiniJson.Get(root, "delta");
                if (type == "content_block_delta" && MiniJson.Text(delta, "type") == "text_delta") return MiniJson.Text(delta, "text") ?? "";
                if (type == "content_block_delta" && MiniJson.Text(delta, "type") == "thinking_delta") ReasoningDelta = MiniJson.Text(delta, "thinking") ?? "";
                var block = MiniJson.Get(root, "content_block");
                if (type == "content_block_start" && MiniJson.Text(block, "type") == "thinking") ReasoningDelta = MiniJson.Text(block, "thinking") ?? "";
                return type == "content_block_start" && MiniJson.Text(block, "type") == "text" ? MiniJson.Text(block, "text") ?? "" : "";
            }
            ReadUsage(MiniJson.Get(root, "usage"));
            var first = (MiniJson.Get(root, "choices") as List<object?>)?.FirstOrDefault(c => MiniJson.Get(c, "index") is double index && index == 0);
            ReasoningDelta = MiniJson.Text(MiniJson.Get(first, "delta"), "reasoning_content") ?? "";
            return MiniJson.Text(MiniJson.Get(first, "delta"), "content") ?? "";
        }

        private void ReadUsage(object? usage)
        {
            InputTokens = AiProvider.TokenCount(usage, anthropic ? "input_tokens" : "prompt_tokens") ?? InputTokens;
            OutputTokens = AiProvider.TokenCount(usage, anthropic ? "output_tokens" : "completion_tokens") ?? OutputTokens;
            ReasoningTokens = AiProvider.TokenCount(MiniJson.Get(usage, anthropic ? "output_tokens_details" : "completion_tokens_details"), anthropic ? "thinking_tokens" : "reasoning_tokens") ?? ReasoningTokens;
        }

        /// <summary>Retry only explicit streaming rejection, never auth, credit or model errors.</summary>
        public static bool Rejected(int status, string body)
        {
            if (status != 400 && status != 422 && status != 501) return false;
            object? root;
            try { root = MiniJson.Parse(body); } catch (FormatException) { return false; }
            var error = MiniJson.Get(root, "error");
            string message = ((error as string ?? MiniJson.Text(error, "message") ?? "") + " " + MiniJson.Text(error, "param")).ToLowerInvariant();
            if (new[] { "api key", "api_key", "unauthor", "authenticat", "credit", "quota", "billing", "balance", "payment", "rate limit" }.Any(message.Contains)) return false;
            return message.Contains("stream") && new[] { "unsupported", "not supported", "not allowed", "unknown", "invalid", "not implemented" }.Any(message.Contains);
        }
    }
}

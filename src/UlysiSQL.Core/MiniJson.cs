using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UlysiSQL.Core
{
    /// <summary>Small JSON reader and writer for the AI provider and MCP messages, so the extension ships no JSON library.
    /// Objects are <see cref="Dictionary{TKey,TValue}"/>, arrays <see cref="List{T}"/>, numbers <see cref="double"/>.</summary>
    public static class MiniJson
    {
        private const int MaxDepth = 64;

        public static object? Parse(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            int at = 0;
            var value = Value(json, ref at, 0);
            Skip(json, ref at);
            if (at != json.Length) throw new FormatException("Unexpected text after JSON value at " + at + ".");
            return value;
        }

        /// <summary>Member of an object, or null when <paramref name="node"/> is not an object or has no such member.</summary>
        public static object? Get(object? node, string name) =>
            node is Dictionary<string, object?> map && map.TryGetValue(name, out var value) ? value : null;

        public static string? Text(object? node, string name) => Get(node, name) as string;

        public static string Write(object? value)
        {
            var text = new StringBuilder();
            Write(text, value, 0);
            return text.ToString();
        }

        private static void Write(StringBuilder text, object? value, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("JSON nesting too deep.");
            switch (value)
            {
                case null: text.Append("null"); break;
                case string s: Quote(text, s); break;
                case bool b: text.Append(b ? "true" : "false"); break;
                case RawJson raw: text.Append(raw.Text); break;
                case IDictionary<string, object?> map:
                    text.Append('{');
                    bool first = true;
                    foreach (var pair in map)
                    {
                        if (!first) text.Append(',');
                        first = false;
                        Quote(text, pair.Key); text.Append(':'); Write(text, pair.Value, depth + 1);
                    }
                    text.Append('}');
                    break;
                // Dictionary<string, string> and the like, which would otherwise come out as an array of pairs.
                case System.Collections.IDictionary other:
                    text.Append('{');
                    bool head = true;
                    foreach (System.Collections.DictionaryEntry pair in other)
                    {
                        if (!head) text.Append(',');
                        head = false;
                        Quote(text, Convert.ToString(pair.Key, CultureInfo.InvariantCulture) ?? ""); text.Append(':'); Write(text, pair.Value, depth + 1);
                    }
                    text.Append('}');
                    break;
                case System.Collections.IEnumerable list:
                    text.Append('[');
                    bool start = true;
                    foreach (var item in list)
                    {
                        if (!start) text.Append(',');
                        start = false;
                        Write(text, item, depth + 1);
                    }
                    text.Append(']');
                    break;
                case double d when double.IsNaN(d) || double.IsInfinity(d): text.Append("null"); break;
                case float f when float.IsNaN(f) || float.IsInfinity(f): text.Append("null"); break;
                case double _: case float _: text.Append(((IFormattable)value).ToString("R", CultureInfo.InvariantCulture)); break;
                case int _: case long _: case short _: case byte _: case sbyte _: case uint _: case ulong _: case ushort _: case decimal _:
                    text.Append(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture)); break;
                // Dates, GUIDs and enums are formattable too, but bare they are not JSON.
                case IFormattable other: Quote(text, other.ToString(null, CultureInfo.InvariantCulture)); break;
                default: Quote(text, value.ToString() ?? ""); break;
            }
        }

        /// <summary>Already-serialized JSON written as is (a tool's input schema).</summary>
        public sealed class RawJson
        {
            public string Text { get; }
            public RawJson(string text) { Text = text; }
        }

        private static void Quote(StringBuilder text, string s)
        {
            text.Append('"');
            foreach (char c in s)
                switch (c)
                {
                    case '"': text.Append("\\\""); break;
                    case '\\': text.Append("\\\\"); break;
                    case '\n': text.Append("\\n"); break;
                    case '\r': text.Append("\\r"); break;
                    case '\t': text.Append("\\t"); break;
                    default:
                        // Lone surrogates and controls escaped so the output is always valid UTF-8 JSON.
                        if (c < ' ' || char.IsSurrogate(c) || c == (char)0x2028 || c == (char)0x2029) text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else text.Append(c);
                        break;
                }
            text.Append('"');
        }

        private static void Skip(string json, ref int at)
        {
            while (at < json.Length && (json[at] == ' ' || json[at] == '\t' || json[at] == '\r' || json[at] == '\n')) at++;
        }

        private static object? Value(string json, ref int at, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("JSON nesting too deep.");
            Skip(json, ref at);
            if (at >= json.Length) throw new FormatException("Unexpected end of JSON.");
            char c = json[at];
            if (c == '{')
            {
                at++;
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                Skip(json, ref at);
                if (at < json.Length && json[at] == '}') { at++; return map; }
                while (true)
                {
                    Skip(json, ref at);
                    if (at >= json.Length || json[at] != '"') throw new FormatException("Expected a member name at " + at + ".");
                    string name = String(json, ref at);
                    Skip(json, ref at);
                    if (at >= json.Length || json[at] != ':') throw new FormatException("Expected ':' at " + at + ".");
                    at++;
                    map[name] = Value(json, ref at, depth + 1);
                    Skip(json, ref at);
                    if (at < json.Length && json[at] == ',') { at++; continue; }
                    if (at < json.Length && json[at] == '}') { at++; return map; }
                    throw new FormatException("Expected ',' or '}' at " + at + ".");
                }
            }
            if (c == '[')
            {
                at++;
                var list = new List<object?>();
                Skip(json, ref at);
                if (at < json.Length && json[at] == ']') { at++; return list; }
                while (true)
                {
                    list.Add(Value(json, ref at, depth + 1));
                    Skip(json, ref at);
                    if (at < json.Length && json[at] == ',') { at++; continue; }
                    if (at < json.Length && json[at] == ']') { at++; return list; }
                    throw new FormatException("Expected ',' or ']' at " + at + ".");
                }
            }
            if (c == '"') return String(json, ref at);
            if (Literal(json, ref at, "true")) return true;
            if (Literal(json, ref at, "false")) return false;
            if (Literal(json, ref at, "null")) return null;
            int from = at;
            while (at < json.Length && (char.IsDigit(json[at]) || json[at] == '-' || json[at] == '+' || json[at] == '.' || json[at] == 'e' || json[at] == 'E')) at++;
            if (at == from || !double.TryParse(json.Substring(from, at - from), NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                throw new FormatException("Unexpected character in JSON at " + from + ".");
            return number;
        }

        private static bool Literal(string json, ref int at, string word)
        {
            if (string.CompareOrdinal(json, at, word, 0, word.Length) != 0) return false;
            at += word.Length;
            return true;
        }

        private static string String(string json, ref int at)
        {
            var text = new StringBuilder();
            for (at++; at < json.Length; at++)
            {
                char c = json[at];
                if (c == '"') { at++; return text.ToString(); }
                if (c != '\\') { text.Append(c); continue; }
                if (++at >= json.Length) break;
                switch (json[at])
                {
                    case 'n': text.Append('\n'); break;
                    case 'r': text.Append('\r'); break;
                    case 't': text.Append('\t'); break;
                    case 'b': text.Append('\b'); break;
                    case 'f': text.Append('\f'); break;
                    case 'u':
                        if (at + 4 >= json.Length || !int.TryParse(json.Substring(at + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                            throw new FormatException("Bad \\u escape at " + at + ".");
                        text.Append((char)code);
                        at += 4;
                        break;
                    default: text.Append(json[at]); break;
                }
            }
            throw new FormatException("Unterminated JSON string.");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.Shell;
using Querywright.Core;

namespace Querywright.Ssms
{
    /// <summary>Opt-in session diagnostics with bounded text capture and credential redaction.</summary>
    internal static class Diagnostics
    {
        internal const int MaxEvents = 500, MaxJsonBytes = 8_388_608, MaxTextBytes = 131_072, MaxBufferBytes = 4_194_304;
        private static readonly object gate = new object();
        private static readonly List<Dictionary<string, object>> events = new List<Dictionary<string, object>>();
        private static readonly List<string> secrets = new List<string>();
        private static int bufferBytes;
        private static readonly string[] outcomes = { "info", "start", "success", "error", "warning", "canceled", "retry" };
        private static bool enabled;
        private static DateTime startedUtc = DateTime.UtcNow;

        internal static bool Enabled
        {
            get { lock (gate) return enabled; }
            set
            {
                lock (gate)
                {
                    if (value && !enabled) startedUtc = DateTime.UtcNow;
                    enabled = value;
                    if (!value) { events.Clear(); bufferBytes = 0; }
                    AiClient.DiagnosticLog.Enabled = value;
                }
            }
        }

        internal static void Write(string component, string eventName, string outcome = "info", long? elapsedMs = null, Exception error = null, string text = null)
        {
            lock (gate)
            {
                if (!enabled) return;
                var entry = new Dictionary<string, object>
                {
                    ["utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    ["component"] = SafeName(component), ["event"] = SafeName(eventName),
                    ["outcome"] = outcomes.Contains(outcome, StringComparer.Ordinal) ? outcome : "info",
                    ["durationMilliseconds"] = elapsedMs.HasValue ? (object)Math.Max(0, Math.Min(86_400_000, elapsedMs.Value)) : null,
                    ["errorType"] = error == null ? "" : SafeName(error.GetType().FullName),
                    ["errorText"] = error == null ? "" : Redact(error.Message), ["text"] = Redact(text),
                    ["stack"] = error == null ? Array.Empty<string>() : (new StackTrace(error, false).GetFrames() ?? Array.Empty<StackFrame>())
                        .Take(8).Select(frame => frame.GetMethod()).Where(method => method != null)
                        .Select(method => SafeName(method.DeclaringType?.FullName + "." + method.Name)).ToArray()
                };
                events.Add(entry);
                bufferBytes += JsonBytes(entry);
                while (events.Count > MaxEvents || bufferBytes > MaxBufferBytes)
                {
                    bufferBytes -= JsonBytes(events[0]);
                    events.RemoveAt(0);
                }
            }
        }

        internal static void SetSecret(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (gate)
            {
                if (secrets.Contains(key, StringComparer.Ordinal)) return;
                secrets.Add(key);
                string escaped = MiniJson.Write(key);
                escaped = escaped.Substring(1, escaped.Length - 2);
                if (!secrets.Contains(escaped, StringComparer.Ordinal)) secrets.Add(escaped);
                // Context can be captured before the client reads its existing API key.
                foreach (var entry in events)
                {
                    entry["text"] = Redact((string)entry["text"]);
                    entry["errorText"] = Redact((string)entry["errorText"]);
                }
                bufferBytes = events.Sum(JsonBytes);
                while (bufferBytes > MaxBufferBytes)
                {
                    bufferBytes -= JsonBytes(events[0]);
                    events.RemoveAt(0);
                }
            }
        }

        internal static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            lock (gate)
            {
                foreach (string secret in secrets) text = text.Replace(secret, "[REDACTED]");
                text = SqlRefactoring.RedactSecrets(text);
                if (text == null) return "[redaction timed out; text omitted]";
                try
                {
                    text = Regex.Replace(text, @"(?im)(\bAuthorization\s*[:=]\s*(?:Bearer|Basic)\s+|\bx-api-key\s*[:=]\s*)[^\s,;]+", "$1[REDACTED]", RegexOptions.None, TimeSpan.FromSeconds(1));
                    text = Regex.Replace(text, "(?i)(\"(?:api[_-]?key|access[_-]?token|client[_-]?secret|password|authorization)\"\\s*:\\s*)\"(?:\\\\.|[^\"\\\\])*\"", "$1\"[REDACTED]\"", RegexOptions.None, TimeSpan.FromSeconds(1));
                    text = Regex.Replace(text, "(?i)(\\b(?:password|pwd|access[_ -]?token|client[_ -]?secret)\\s*=\\s*)(?:N?'(?:''|[^'])*'|\"(?:\"\"|[^\"])*\"|[^;\\r\\n]*)", "$1[REDACTED]", RegexOptions.None, TimeSpan.FromSeconds(1));
                }
                catch (RegexMatchTimeoutException) { return "[redaction timed out; text omitted]"; }
                return BoundedText(text);
            }
        }

        internal static bool TryLogInformation(string source, string message, [CallerMemberName] string member = "", [CallerFilePath] string file = "")
        {
            Write(Path.GetFileNameWithoutExtension(file), member, text: message);
            return ActivityLog.TryLogInformation(source, message);
        }

        internal static bool TryLogWarning(string source, string message, [CallerMemberName] string member = "", [CallerFilePath] string file = "")
        {
            Write(Path.GetFileNameWithoutExtension(file), member, "warning", text: message);
            return ActivityLog.TryLogWarning(source, message);
        }

        internal static bool TryLogError(string source, string message, [CallerMemberName] string member = "", [CallerFilePath] string file = "")
        {
            Write(Path.GetFileNameWithoutExtension(file), member, "error", text: message);
            return ActivityLog.TryLogError(source, message);
        }

        internal static string Export()
        {
            lock (gate)
            {
                string hostVersion = "";
                try { using (var process = Process.GetCurrentProcess()) hostVersion = SafeVersion(process.MainModule?.FileVersionInfo.FileVersion); }
                catch (Exception error) when (!(error is OutOfMemoryException)) { }
                return MiniJson.Write(new Dictionary<string, object>
                {
                    ["schema"] = "querywright-diagnostics", ["version"] = 1,
                    ["session"] = new Dictionary<string, object>
                    {
                        ["startedUtc"] = startedUtc.ToString("O", CultureInfo.InvariantCulture), ["enabled"] = enabled,
                        ["appVersion"] = SafeVersion(UpdateCheck.InstalledVersion() ?? typeof(WorkbenchPackage).Assembly.GetName().Version?.ToString()),
                        ["hostVersion"] = hostVersion, ["runtimeVersion"] = Environment.Version.ToString(),
                        ["osVersion"] = Environment.OSVersion.Version.ToString(), ["processArchitecture"] = IntPtr.Size == 8 ? "x64" : "x86"
                    },
                    ["events"] = events.ToArray(), ["ai"] = MiniJson.Parse(AiClient.DiagnosticLog.Export())
                });
            }
        }

        internal static string ValidateImport(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            if (json.Length > MaxJsonBytes || Encoding.UTF8.GetByteCount(json) > MaxJsonBytes) throw new FormatException("Diagnostic file exceeds 8 MB.");
            object root = MiniJson.Parse(json);
            if (MiniJson.Get(root, "schema") as string != "querywright-diagnostics" || !(MiniJson.Get(root, "version") is double version) || version != 1
                || !(MiniJson.Get(root, "session") is Dictionary<string, object> session) || !(MiniJson.Get(root, "events") is List<object> list) || list.Count > MaxEvents)
                throw new FormatException("Diagnostic file must contain querywright-diagnostics version 1 and at most 500 events.");
            var safeSession = new Dictionary<string, object>
            {
                ["startedUtc"] = Utc(session, "startedUtc"), ["enabled"] = MiniJson.Get(session, "enabled") is bool flag ? flag : throw Invalid("enabled"),
                ["appVersion"] = VersionField(session, "appVersion"), ["hostVersion"] = VersionField(session, "hostVersion"),
                ["runtimeVersion"] = VersionField(session, "runtimeVersion"), ["osVersion"] = VersionField(session, "osVersion"),
                ["processArchitecture"] = Choice(session, "processArchitecture", new[] { "x64", "x86" })
            };
            var safeEvents = list.Select(item =>
            {
                if (!(item is Dictionary<string, object>) || !(MiniJson.Get(item, "stack") is List<object> stack) || stack.Count > 8) throw Invalid("event");
                return new Dictionary<string, object>
                {
                    ["utc"] = Utc(item, "utc"), ["component"] = Name(item, "component"), ["event"] = Name(item, "event"),
                    ["outcome"] = Choice(item, "outcome", outcomes), ["durationMilliseconds"] = Duration(item),
                    ["text"] = Text(item, "text"), ["errorText"] = Text(item, "errorText"),
                    ["errorType"] = Name(item, "errorType"), ["stack"] = stack.Select(value => NameValue(value, "stack")).ToArray()
                };
            }).ToArray();
            var ai = new AiDiagnostics();
            ai.Import(MiniJson.Write(MiniJson.Get(root, "ai")));
            return MiniJson.Write(new Dictionary<string, object> { ["schema"] = "querywright-diagnostics", ["version"] = 1,
                ["session"] = safeSession, ["events"] = safeEvents, ["ai"] = MiniJson.Parse(ai.Export()) });
        }

        internal static string FormatDisplay(string json)
        {
            object root = MiniJson.Parse(ValidateImport(json));
            var text = new StringBuilder("Querywright diagnostics\r\n");
            foreach (var field in (Dictionary<string, object>)MiniJson.Get(root, "session")) text.Append(field.Key).Append(": ").Append(field.Value).AppendLine();
            text.AppendLine().AppendLine("Process events");
            foreach (var item in (List<object>)MiniJson.Get(root, "events"))
            {
                text.Append(MiniJson.Get(item, "utc")).Append("  ").Append(MiniJson.Get(item, "component")).Append('.').Append(MiniJson.Get(item, "event"))
                    .Append("  ").Append(MiniJson.Get(item, "outcome"));
                if (MiniJson.Get(item, "durationMilliseconds") != null) text.Append("  ").Append(MiniJson.Get(item, "durationMilliseconds")).Append(" ms");
                text.AppendLine();
                if ((string)MiniJson.Get(item, "errorType") != "") text.Append("  ").Append(MiniJson.Get(item, "errorType")).AppendLine();
                if ((string)MiniJson.Get(item, "errorText") != "") text.Append("  Error: ").Append(MiniJson.Get(item, "errorText")).AppendLine();
                if ((string)MiniJson.Get(item, "text") != "") text.AppendLine((string)MiniJson.Get(item, "text"));
                foreach (var frame in (List<object>)MiniJson.Get(item, "stack")) text.Append("    ").Append(frame).AppendLine();
            }
            text.AppendLine().AppendLine("AI requests");
            foreach (var item in (List<object>)MiniJson.Get(MiniJson.Get(root, "ai"), "entries"))
            {
                foreach (var field in (Dictionary<string, object>)item) text.Append("  ").Append(field.Key).Append(": ").Append(field.Value ?? "-").AppendLine();
                text.AppendLine();
            }
            return text.ToString();
        }

        private static string SafeName(string value) => new string((value ?? "").Where(c => char.IsLetterOrDigit(c) || c == '.' || c == '_').Take(128).ToArray());
        private static int JsonBytes(object value) => Encoding.UTF8.GetByteCount(MiniJson.Write(value));
        private static string BoundedText(string text)
        {
            if (text.Length <= MaxTextBytes && Encoding.UTF8.GetByteCount(text) <= MaxTextBytes) return text;
            const string marker = "\r\n[truncated]";
            int limit = MaxTextBytes - Encoding.UTF8.GetByteCount(marker);
            if (text.Length > limit) text = text.Substring(0, limit);
            int count = Encoding.UTF8.GetByteCount(text), length = count <= limit ? text.Length : (int)((long)limit * text.Length / count);
            while (Encoding.UTF8.GetByteCount(text.Substring(0, length)) > limit) length--;
            if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
            return text.Substring(0, length) + marker;
        }
        private static string SafeVersion(string value) => Version.TryParse(value, out var version) ? version.ToString() : "";
        private static FormatException Invalid(string field) => new FormatException("Invalid diagnostic field: " + field + ".");
        private static string Name(object item, string field) => NameValue(MiniJson.Get(item, field), field);
        private static string NameValue(object value, string field)
        {
            if (!(value is string text) || text.Length > 128 || SafeName(text) != text) throw Invalid(field);
            return text;
        }
        private static string VersionField(object item, string field)
        {
            if (!(MiniJson.Get(item, field) is string text) || text.Length > 32 || text != "" && SafeVersion(text) != text) throw Invalid(field);
            return text;
        }
        private static string Choice(object item, string field, string[] values)
        {
            if (!(MiniJson.Get(item, field) is string text) || !values.Contains(text, StringComparer.Ordinal)) throw Invalid(field);
            return text;
        }
        private static string Text(object item, string field)
        {
            if (!(MiniJson.Get(item, field) is string text) || text.Length > MaxTextBytes || Encoding.UTF8.GetByteCount(text) > MaxTextBytes) throw Invalid(field);
            return Redact(text);
        }
        private static string Utc(object item, string field)
        {
            if (!(MiniJson.Get(item, field) is string text) || text.Length > 40 || !DateTime.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) || date.Kind != DateTimeKind.Utc)
                throw Invalid(field);
            return date.ToString("O", CultureInfo.InvariantCulture);
        }
        private static object Duration(object item)
        {
            object value = MiniJson.Get(item, "durationMilliseconds");
            if (value == null) return null;
            if (!(value is double number) || double.IsNaN(number) || double.IsInfinity(number) || number < 0 || number > 86_400_000 || number != Math.Truncate(number)) throw Invalid("durationMilliseconds");
            return (long)number;
        }
    }
}

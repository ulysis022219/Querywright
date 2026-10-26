using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using UlysiSQL.Core;

namespace UlysiSQL.Ssms
{
    public enum AiApiFormat { OpenAiCompatible, Anthropic }

    /// <summary>Tools > Options > UlysiSQL > AI. The provider is whatever the base URL points at.</summary>
    public sealed class AiOptions : DialogPage
    {
        private const string Saved = "(saved)";

        [Category("Provider")]
        [DisplayName("API format")]
        [Description("OpenAiCompatible covers DeepSeek, OpenAI, OpenRouter, Groq, Mistral, Ollama and LM Studio. Anthropic uses the Claude messages endpoint.")]
        public AiApiFormat ApiFormat { get; set; } = AiApiFormat.OpenAiCompatible;

        [Category("Provider")]
        [DisplayName("Base URL")]
        [Description("DeepSeek: https://api.deepseek.com   OpenAI: https://api.openai.com/v1   OpenRouter: https://openrouter.ai/api/v1   Ollama: http://localhost:11434/v1   Anthropic: https://api.anthropic.com")]
        public string BaseUrl { get; set; } = "https://api.deepseek.com";

        [Category("Provider")]
        [DisplayName("Model")]
        [Description("Choose a model returned by your provider, or type its identifier in the AI panel.")]
        public string Model { get; set; } = "deepseek-chat";

        [Category("Provider")]
        [DisplayName("Reasoning")]
        [Description("Requested reasoning effort. Off omits reasoning fields; a provider may still reason by default. Inline completion always omits them.")]
        public AiReasoningLevel Reasoning { get; set; }

        [Category("Provider")]
        [DisplayName("Extra request JSON")]
        [Description("Additional provider-specific JSON object. Cannot override messages, model, stream or credentials. Use null temperature to omit it. Reasoning/thinking fields apply only when reasoning is selected.")]
        public string ExtraRequestJson { get; set; } = "";

        /// <summary>Never holds the key: reads back a placeholder, so the options store only ever sees "(saved)". The key itself
        /// is encrypted for the Windows account (DPAPI) in %LocalAppData%\UlysiSQL\ai-key.bin.</summary>
        [Category("Provider")]
        [DisplayName("API key")]
        [Description("Paste the provider's API key and press OK. Stored encrypted for your Windows account, outside the SSMS settings. Clear the box to remove it. Leave empty for a local server that needs none.")]
        [PasswordPropertyText(true)]
        public string ApiKey
        {
            get => AiKey.Exists ? Saved : "";
            set { if (value != Saved) AiKey.Save(value); }
        }

        [Category("Context")]
        [DisplayName("Schema tables to send")]
        [Description("How many table definitions (names, columns, types, foreign keys) of the connected database go with a request, picked by matching names in your prompt and script. 0 sends no schema.")]
        public int MaxSchemaTables { get; set; } = 30;

        [Category("Execution")]
        [DisplayName("Confirm before running AI SQL")]
        [Description("Ask before SQL is executed by the AI window's Run button or by an MCP client's execute_sql tool. Off runs it straight away with your query window's login.")]
        public bool ConfirmExecution { get; set; } = true;

        [Category("MCP server")]
        [DisplayName("Run MCP server")]
        [Description("Lets MCP clients (Claude Code, Claude Desktop, Cursor, VS Code) read the active query window, browse the connected database, format, analyze and execute SQL. Listens on this computer only and requires the token from UlysiSQL > AI > Copy MCP client configuration.")]
        public bool McpServer { get; set; }

        [Category("MCP server")]
        [DisplayName("Port")]
        [Description("Local port of the MCP server (http://127.0.0.1:port/mcp).")]
        public int McpPort { get; set; } = 8765;

        public override void LoadSettingsFromStorage()
        {
            string current = SettingsRegistryPath;
            SettingsRegistryPath = Product.SettingsPath(current, Product.UserSettingsExist);
            try { base.LoadSettingsFromStorage(); }
            finally { SettingsRegistryPath = current; }
        }

        protected override void OnApply(PageApplyEventArgs e)
        {
            try { AiModels.Remember(BaseUrl, Model); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException)
            {
                e.ApplyBehavior = ApplyKind.CancelNoNavigate;
                VsShellUtilities.ShowMessageBox(WorkbenchPackage.Instance, "Could not save model choice: " + error.Message, "UlysiSQL AI", Microsoft.VisualStudio.Shell.Interop.OLEMSGICON.OLEMSGICON_WARNING,
                    Microsoft.VisualStudio.Shell.Interop.OLEMSGBUTTON.OLEMSGBUTTON_OK, Microsoft.VisualStudio.Shell.Interop.OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }
            base.OnApply(e);
            WorkbenchPackage.Instance?.RestartMcp();
        }
    }

    internal static class AiKey
    {
        private static readonly string Folder = Product.DataFolder;
        private static readonly string File = Path.Combine(Folder, "ai-key.bin");
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("UlysiSQL AI provider key");
        // Keys saved before the rename; read only, the next save uses the new entropy.
        private static readonly byte[] LegacyEntropy = Encoding.UTF8.GetBytes("Querywright AI provider key");

        internal static bool Exists => System.IO.File.Exists(File);

        internal static void Save(string key)
        {
            key = (key ?? "").Trim();
            if (key.Length == 0) { if (Exists) System.IO.File.Delete(File); return; }
            Directory.CreateDirectory(Folder);
            System.IO.File.WriteAllBytes(File, ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser));
        }

        /// <summary>Empty when none is saved or it was saved by another Windows account.</summary>
        internal static string Read()
        {
            try
            {
                if (!Exists) return "";
                byte[] data = System.IO.File.ReadAllBytes(File);
                try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser)); }
                catch (CryptographicException) { return Encoding.UTF8.GetString(ProtectedData.Unprotect(data, LegacyEntropy, DataProtectionScope.CurrentUser)); }
            }
            catch (Exception error) when (error is CryptographicException || error is IOException || error is UnauthorizedAccessException) { return ""; }
        }
    }

    internal static class AiClient
    {
        // Custom API-key headers survive automatic redirects on .NET Framework.
        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5), MaxResponseContentBufferSize = 2_000_000 };
        private static readonly Dictionary<string, IReadOnlyList<string>> ModelLists = new Dictionary<string, IReadOnlyList<string>>();
        private static readonly HashSet<string> UnsupportedReasoning = new HashSet<string>();
        internal static readonly AiDiagnostics DiagnosticLog = new AiDiagnostics();

        private static string Identity(AiApiFormat format, string baseUrl) => format + ":" + AiModels.Key(baseUrl);
        private static HttpRequestMessage Request(HttpMethod method, string url, bool anthropic, string key)
        {
            var request = new HttpRequestMessage(method, url);
            if (anthropic)
            {
                request.Headers.TryAddWithoutValidation("x-api-key", key);
                request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            }
            else if (key.Length > 0) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            return request;
        }
        private static string Key(string url)
        {
            var uri = new Uri(url);
            if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                throw new InvalidOperationException("Use a provider base URL without credentials, query parameters or fragments.");
            string key = AiKey.Read();
            if (key.Length == 0 && !uri.IsLoopback)
                throw new InvalidOperationException("Enter your AI provider API key under Tools > Options > UlysiSQL > AI.");
            return key;
        }

        internal static async Task<IReadOnlyList<string>> ModelsAsync(AiApiFormat format, string baseUrl, CancellationToken cancellation, bool refresh = false)
        {
            string identity = Identity(format, baseUrl);
            lock (ModelLists) if (!refresh && ModelLists.TryGetValue(identity, out var cached)) return cached;
            bool anthropic = format == AiApiFormat.Anthropic;
            string endpoint = AiAssist.Endpoint(baseUrl, anthropic);
            string url = endpoint.Substring(0, endpoint.Length - (anthropic ? "/messages" : "/chat/completions").Length) + "/models";
            string key = Key(url);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    var models = new List<string>(); var cursors = new HashSet<string>(); string cursor = null;
                    for (int page = 0; page < 20; page++)
                    {
                        using (var request = Request(HttpMethod.Get, url + (cursor == null ? "" : "?after_id=" + Uri.EscapeDataString(cursor)), anthropic, key))
                        using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                        using (timeout.Token.Register(() => response.Dispose()))
                        {
                            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Model discovery returned HTTP " + (int)response.StatusCode + ". Type a model name instead.");
                            await response.Content.LoadIntoBufferAsync(2_000_000).ConfigureAwait(false);
                            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (body.Length > 2_000_000) throw new InvalidOperationException("The provider model list is too large. Type a model name instead.");
                            models.AddRange(AiProvider.Models(body));
                            cursor = anthropic ? AiProvider.ModelsNext(body) : null;
                            if (cursor == null)
                            {
                                var result = models.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
                                lock (ModelLists) ModelLists[identity] = result;
                                return result;
                            }
                            if (!cursors.Add(cursor)) break;
                        }
                    }
                    throw new InvalidOperationException("The provider model list is incomplete. Type a model name instead.");
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new InvalidOperationException("Model discovery timed out. Type a model name instead."); }
                catch (HttpRequestException) { throw new InvalidOperationException("Could not reach the model list. Type a model name instead."); }
                catch (FormatException) { throw new InvalidOperationException("The provider returned an invalid model list. Type a model name instead."); }
            }
        }

        internal static async Task<string> AskAsync(AiApiFormat format, string baseUrl, string model, IReadOnlyList<AiMessage> messages, CancellationToken cancellation, int maxTokens = 4096, IProgress<string> progress = null)
            => (await ReplyAsync(format, baseUrl, model, messages, cancellation, maxTokens, progress).ConfigureAwait(false)).Text;

        internal static async Task<AiReply> ReplyAsync(AiApiFormat format, string baseUrl, string model, IReadOnlyList<AiMessage> messages, CancellationToken cancellation,
            int? maxTokens = null, IProgress<string> progress = null, AiReasoningLevel reasoning = AiReasoningLevel.Off, string extraJson = null, IProgress<string> thoughts = null)
        {
            bool anthropic = format == AiApiFormat.Anthropic;
            string url = AiAssist.Endpoint(baseUrl, anthropic), key = Key(url);
            Diagnostics.SetSecret(key);
            string requestId = Guid.NewGuid().ToString("N"); int attempt = 0;
            string identity = Identity(format, baseUrl) + ":" + model;
            bool unsupported;
            lock (UnsupportedReasoning) unsupported = reasoning != AiReasoningLevel.Off && UnsupportedReasoning.Contains(identity);
            if (unsupported) reasoning = AiReasoningLevel.Off;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(5));
                try
                {
                    var reply = await PostAsync(progress != null, reasoning, extraJson).ConfigureAwait(false);
                    reply.ReasoningUnsupported = unsupported;
                    return reply;
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new InvalidOperationException("The AI provider did not answer in time."); }
                catch (Exception error) when (timeout.IsCancellationRequested && !(error is OutOfMemoryException))
                {
                    if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
                    throw new InvalidOperationException("The AI provider did not answer in time.");
                }
                catch (HttpRequestException) { throw new InvalidOperationException("Could not reach the AI provider. Check the base URL and connection."); }

                async Task<AiReply> PostAsync(bool streaming, AiReasoningLevel effort, string extra)
                {
                    var clock = Stopwatch.StartNew();
                    var trace = new AiDiagnosticEntry
                    {
                        AppVersion = Diagnostics.InstalledVersion() ?? typeof(AiClient).Assembly.GetName().Version.ToString(), RequestId = requestId,
                        ProviderHost = Diagnostics.Redact(new Uri(url).Host), ApiFormat = format.ToString(), Model = Diagnostics.Redact(model),
                        Reasoning = effort.ToString().ToLowerInvariant(), Attempt = ++attempt, Streaming = streaming, ErrorCategory = "configuration"
                    };
                    var answer = new StringBuilder(); var reasoningText = new StringBuilder(); var streamTrace = new StringBuilder();
                    try
                    {
                        string requestBody = AiAssist.RequestBody(model, messages, anthropic, maxTokens, stream: streaming, reasoning: effort, extraRequestJson: extra, baseUrl: baseUrl);
                        var requestJson = MiniJson.Parse(requestBody);
                        trace.MaxOutputTokens = AiProvider.TokenCount(requestJson, "max_completion_tokens") ?? AiProvider.TokenCount(requestJson, "max_tokens") ?? 0;
                        if (Diagnostics.Enabled) Diagnostics.Write("ai", "request", text: "Request " + requestId + " attempt " + trace.Attempt + "\n" + requestBody);
                        trace.ErrorCategory = "network";
                        using (var request = Request(HttpMethod.Post, url, anthropic, key))
                        {
                            request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                            using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                            using (timeout.Token.Register(() => response.Dispose()))
                            {
                                trace.HttpStatus = (int)response.StatusCode;
                                if (response.IsSuccessStatusCode && streaming && response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                                {
                                    trace.ResponseFormat = "sse"; trace.ErrorCategory = "stream";
                                    var parser = new AiStream(anthropic);
                                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                                    using (timeout.Token.Register(() => stream.Dispose()))
                                    {
                                        var chunk = new char[4096]; var line = new StringBuilder(); bool afterCr = false;
                                        while (!parser.Done)
                                        {
                                            timeout.Token.ThrowIfCancellationRequested();
                                            int read = await reader.ReadAsync(chunk, 0, chunk.Length).ConfigureAwait(false);
                                            if (read == 0)
                                            {
                                                if (line.Length > 0) Emit(line.ToString());
                                                Emit(""); parser.Finish(); break;
                                            }
                                            for (int i = 0; i < read && !parser.Done; i++)
                                            {
                                                if (afterCr && chunk[i] == '\n') { afterCr = false; continue; }
                                                afterCr = chunk[i] == '\r';
                                                if (chunk[i] == '\n' || chunk[i] == '\r') { Emit(line.ToString()); line.Clear(); }
                                                else
                                                {
                                                    if (line.Length >= 1_000_000) throw new InvalidOperationException("The AI provider sent an oversized stream line.");
                                                    line.Append(chunk[i]);
                                                }
                                            }
                                        }
                                        void Emit(string value)
                                        {
                                            if (Diagnostics.Enabled && streamTrace.Length < Diagnostics.MaxTextBytes)
                                                streamTrace.AppendLine(value.Length + streamTrace.Length <= Diagnostics.MaxTextBytes ? value : value.Substring(0, Diagnostics.MaxTextBytes - streamTrace.Length));
                                            string delta = parser.Line(value);
                                            if (answer.Length + delta.Length > 2_000_000) throw new InvalidOperationException("The AI answer is too large to display.");
                                            answer.Append(delta); if (delta.Length > 0) progress.Report(delta);
                                            if (reasoningText.Length + parser.ReasoningDelta.Length > 2_000_000) throw new InvalidOperationException("The AI reasoning is too large to display.");
                                            reasoningText.Append(parser.ReasoningDelta); if (parser.ReasoningDelta.Length > 0) thoughts?.Report(parser.ReasoningDelta);
                                            trace.AnswerCharacters = answer.Length; trace.ReasoningCharacters = reasoningText.Length;
                                            trace.FinishReason = parser.FinishReason ?? "";
                                            trace.InputTokens = parser.InputTokens; trace.OutputTokens = parser.OutputTokens; trace.ReasoningTokens = parser.ReasoningTokens;
                                        }
                                    }
                                    timeout.Token.ThrowIfCancellationRequested();
                                    return Complete(new AiReply(answer.ToString(), reasoningText.ToString(), parser.InputTokens, parser.OutputTokens, parser.ReasoningTokens, parser.FinishReason));
                                }
                                trace.ResponseFormat = "json"; trace.ErrorCategory = response.IsSuccessStatusCode ? "invalid_json" : "http";
                                await response.Content.LoadIntoBufferAsync(2_000_000).ConfigureAwait(false);
                                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                                if (Diagnostics.Enabled) Diagnostics.Write("ai", "response_json", response.IsSuccessStatusCode ? "info" : "error", text: "Request " + requestId + " attempt " + trace.Attempt + "\n" + body);
                                timeout.Token.ThrowIfCancellationRequested();
                                if (effort != AiReasoningLevel.Off && AiProvider.ReasoningRejected((int)response.StatusCode, body, anthropic, requestBody))
                                {
                                    lock (UnsupportedReasoning) UnsupportedReasoning.Add(identity);
                                    unsupported = true;
                                    trace.Outcome = "retry"; trace.ErrorCategory = "reasoning_unsupported";
                                    return await PostAsync(streaming, AiReasoningLevel.Off, AiProvider.WithoutReasoningJson(extra)).ConfigureAwait(false);
                                }
                                if (streaming && AiStream.Rejected((int)response.StatusCode, body))
                                {
                                    trace.Outcome = "retry"; trace.ErrorCategory = "stream_unsupported";
                                    return await PostAsync(false, effort, extra).ConfigureAwait(false);
                                }
                                if (!response.IsSuccessStatusCode)
                                    throw new InvalidOperationException("The AI provider answered HTTP " + (int)response.StatusCode + ". Check the base URL, model, API key and account credit.");
                                // Provider error bodies can echo credentials; report only local diagnostic text.
                                AiReply reply;
                                try { reply = AiProvider.Reply(body, anthropic); }
                                catch (Exception error) when (error is InvalidOperationException || error is FormatException) { throw new InvalidOperationException("The AI provider returned an invalid or empty answer. Check the base URL and model."); }
                                return Complete(reply);
                            }
                        }
                    }
                    catch (Exception error) when (!(error is OutOfMemoryException))
                    {
                        if (timeout.IsCancellationRequested) { trace.Outcome = cancellation.IsCancellationRequested ? "canceled" : "error"; trace.ErrorCategory = cancellation.IsCancellationRequested ? "canceled" : "timeout"; }
                        else if (error is HttpRequestException) trace.ErrorCategory = "network";
                        Diagnostics.Write("ai", "failed", "error", error: error);
                        throw;
                    }
                    finally
                    {
                        trace.DurationMilliseconds = clock.ElapsedMilliseconds;
                        DiagnosticLog.Add(trace);
                        if (Diagnostics.Enabled && streamTrace.Length > 0)
                            Diagnostics.Write("ai", "response_stream", trace.Outcome, trace.DurationMilliseconds, text: "Request " + requestId + " attempt " + trace.Attempt + "\n" + streamTrace + (streamTrace.Length >= Diagnostics.MaxTextBytes ? "\n[truncated]" : ""));
                        if (Diagnostics.Enabled && (answer.Length > 0 || reasoningText.Length > 0))
                            Diagnostics.Write("ai", "response_text", trace.Outcome, trace.DurationMilliseconds, text: "Request " + requestId + " attempt " + trace.Attempt + "\nReasoning:\n" + reasoningText + "\nAnswer:\n" + answer);
                    }

                    AiReply Complete(AiReply reply)
                    {
                        trace.InputTokens = reply.InputTokens; trace.OutputTokens = reply.OutputTokens; trace.ReasoningTokens = reply.ReasoningTokens;
                        trace.FinishReason = reply.FinishReason ?? ""; trace.AnswerCharacters = reply.Text.Length; trace.ReasoningCharacters = reply.Reasoning.Length;
                        if (string.IsNullOrWhiteSpace(reply.Text))
                        {
                            trace.Outcome = "empty"; trace.ErrorCategory = "empty_answer";
                            throw new InvalidOperationException(AiProvider.CompletionProblem(reply) ?? "The AI provider returned an empty answer. Export diagnostics to inspect the provider response.");
                        }
                        trace.Outcome = "success"; trace.ErrorCategory = "none";
                        return reply;
                    }
                }
            }
        }
    }

    /// <summary>SQL the AI features send to the server on the query window's own login: compile checks, estimated plans and, on request, real execution.</summary>
    internal static class AiSql
    {
        private const int Timeout = 120;

        /// <summary>Compile errors per GO batch, found with SET NOEXEC ON so nothing runs. Empty when the script compiles.</summary>
        // ponytail: compile-time errors only; a missing table (deferred name resolution) or a runtime error such as a conversion failure is not seen.
        internal static List<string> Errors(ActiveConnection connection, string sql)
        {
            Diagnostics.Write("sql", "compile", text: sql);
            var errors = new List<string>();
            using (var server = connection.Open())
            {
                Run(server, "SET NOEXEC ON;");
                try
                {
                    foreach (var (text, line) in AiAssist.Batches(sql))
                        try { Run(server, text); }
                        catch (SqlException error)
                        {
                            foreach (SqlError item in error.Errors)
                                errors.Add("Line " + (line + Math.Max(1, item.LineNumber) - 1) + ", Msg " + item.Number + ": " + item.Message);
                        }
                }
                finally { try { Run(server, "SET NOEXEC OFF;"); } catch (Exception error) when (error is SqlException || error is InvalidOperationException) { } }
            }
            if (Diagnostics.Enabled) Diagnostics.Write("sql", "compile_result", errors.Count == 0 ? "success" : "error", text: string.Join("\n", errors));
            return errors;
        }

        /// <summary>Estimated plan XML of each statement (SET SHOWPLAN_XML ON); the script is compiled, not run.</summary>
        internal static string Plan(ActiveConnection connection, string sql)
        {
            Diagnostics.Write("sql", "estimated_plan", text: sql);
            var plans = new StringBuilder();
            using (var server = connection.Open())
            {
                Run(server, "SET SHOWPLAN_XML ON;");
                try
                {
                    foreach (var (text, _) in AiAssist.Batches(sql))
                        using (var command = new SqlCommand(text, server) { CommandTimeout = Timeout })
                        using (var reader = command.ExecuteReader())
                            do { while (reader.Read()) plans.AppendLine(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)); } while (reader.NextResult());
                }
                finally { try { Run(server, "SET SHOWPLAN_XML OFF;"); } catch (Exception error) when (error is SqlException || error is InvalidOperationException) { } }
            }
            string plan = plans.ToString(); Diagnostics.Write("sql", "estimated_plan_result", "success", text: plan); return plan;
        }

        /// <summary>Runs the script for real, batch by batch, and returns result sets, messages and row counts as JSON. Stops at the first failing batch.</summary>
        internal static string Execute(ActiveConnection connection, string sql, int maxRows)
        {
            Diagnostics.Write("sql", "execute", text: sql);
            maxRows = Math.Max(1, Math.Min(maxRows, 5000));
            var sets = new List<object>();
            var messages = new List<object>();
            string failure = null;
            int affected = 0;
            using (var server = connection.Open())
            {
                server.InfoMessage += (sender, info) => { if (messages.Count < 200) messages.Add(info.Message); };
                foreach (var (text, line) in AiAssist.Batches(sql))
                {
                    try
                    {
                        using (var command = new SqlCommand(text, server) { CommandTimeout = Timeout })
                        using (var reader = command.ExecuteReader())
                        {
                            do
                            {
                                if (reader.FieldCount == 0) continue;
                                var columns = Enumerable.Range(0, reader.FieldCount).Select(i => (object)reader.GetName(i)).ToList();
                                var rows = new List<object>();
                                bool more = false;
                                while (reader.Read())
                                {
                                    if (rows.Count >= maxRows) { more = true; break; }
                                    rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => Cell(reader.GetValue(i))).ToList());
                                }
                                if (more) command.Cancel(); // do not drain a huge result just to drop it
                                sets.Add(new Dictionary<string, object> { ["columns"] = columns, ["rows"] = rows, ["truncated"] = more });
                                if (more) break;
                            } while (reader.NextResult());
                            if (reader.RecordsAffected > 0) affected += reader.RecordsAffected;
                        }
                    }
                    catch (SqlException error)
                    {
                        failure = string.Join(" ", error.Errors.Cast<SqlError>().Where(e => e.Class > 10).Select(e => "Line " + (line + Math.Max(1, e.LineNumber) - 1) + ", Msg " + e.Number + ": " + e.Message));
                        if (failure.Length == 0) failure = error.Message;
                        break;
                    }
                }
            }
            var result = new Dictionary<string, object> { ["resultSets"] = sets, ["rowsAffected"] = affected, ["messages"] = messages };
            if (failure != null) result["error"] = failure;
            string json = MiniJson.Write(result);
            Diagnostics.Write("sql", "result", failure == null ? "success" : "error", text: json);
            return json;
        }

        private static object Cell(object value)
        {
            switch (value)
            {
                case null: case DBNull _: return null;
                case bool _: case int _: case long _: case short _: case byte _: case double _: case float _: case decimal _: return value;
                case DateTime date: return date.ToString(date.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-ddTHH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
                case byte[] bytes: return "0x" + BitConverter.ToString(bytes, 0, Math.Min(bytes.Length, 64)).Replace("-", "") + (bytes.Length > 64 ? "..." : "");
                default:
                    string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                    return text.Length > 4000 ? text.Substring(0, 4000) + "..." : text;
            }
        }

        private static void Run(SqlConnection server, string sql)
        {
            using (var command = new SqlCommand(sql, server) { CommandTimeout = Timeout }) command.ExecuteNonQuery();
        }
    }
}

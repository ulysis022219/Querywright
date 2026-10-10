using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Querywright.Core;

namespace Querywright.Ssms
{
    public enum AiApiFormat { OpenAiCompatible, Anthropic }

    /// <summary>Tools > Options > Querywright > AI. The provider is whatever the base URL points at.</summary>
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
        [Description("Model name as the provider spells it, for example deepseek-chat, deepseek-reasoner, gpt-4.1, claude-sonnet-4-5.")]
        public string Model { get; set; } = "deepseek-chat";

        /// <summary>Never holds the key: reads back a placeholder, so the options store only ever sees "(saved)". The key itself
        /// is encrypted for the Windows account (DPAPI) in %LocalAppData%\Querywright\ai-key.bin.</summary>
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
        [Description("Lets MCP clients (Claude Code, Claude Desktop, Cursor, VS Code) read the active query window, browse the connected database, format, analyze and execute SQL. Listens on this computer only and requires the token from Querywright > AI > Copy MCP client configuration.")]
        public bool McpServer { get; set; }

        [Category("MCP server")]
        [DisplayName("Port")]
        [Description("Local port of the MCP server (http://127.0.0.1:port/mcp).")]
        public int McpPort { get; set; } = 8765;

        protected override void OnApply(PageApplyEventArgs e)
        {
            base.OnApply(e);
            WorkbenchPackage.Instance?.RestartMcp();
        }
    }

    internal static class AiKey
    {
        private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Querywright");
        private static readonly string File = Path.Combine(Folder, "ai-key.bin");
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Querywright AI provider key");

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
            try { return Exists ? Encoding.UTF8.GetString(ProtectedData.Unprotect(System.IO.File.ReadAllBytes(File), Entropy, DataProtectionScope.CurrentUser)) : ""; }
            catch (Exception error) when (error is CryptographicException || error is IOException || error is UnauthorizedAccessException) { return ""; }
        }
    }

    internal static class AiClient
    {
        // ponytail: one shared client, no streaming; the answer appears when the provider has finished it.
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        internal static async Task<string> AskAsync(AiApiFormat format, string baseUrl, string model, IReadOnlyList<AiMessage> messages, CancellationToken cancellation, int maxTokens = 4096)
        {
            bool anthropic = format == AiApiFormat.Anthropic;
            string url = AiAssist.Endpoint(baseUrl, anthropic), key = AiKey.Read();
            if (key.Length == 0 && !new Uri(url).IsLoopback)
                throw new InvalidOperationException("Enter your AI provider API key under Tools > Options > Querywright > AI.");
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(AiAssist.RequestBody(model, messages, anthropic, maxTokens), Encoding.UTF8, "application/json");
                if (anthropic)
                {
                    request.Headers.TryAddWithoutValidation("x-api-key", key);
                    request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                }
                else if (key.Length > 0) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                HttpResponseMessage response;
                try { response = await Http.SendAsync(request, cancellation).ConfigureAwait(false); }
                catch (HttpRequestException error) { throw new InvalidOperationException("Could not reach the AI provider at " + new Uri(url).Host + ": " + (error.InnerException?.Message ?? error.Message)); }
                catch (TaskCanceledException) when (!cancellation.IsCancellationRequested) { throw new InvalidOperationException("The AI provider did not answer in time."); }
                using (response)
                {
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (response.IsSuccessStatusCode) return AiAssist.ReplyText(body, anthropic);
                    // The provider's own error text when it sent one (bad key, unknown model, no credit); never the key.
                    try { AiAssist.ReplyText(body, anthropic); }
                    catch (InvalidOperationException error) when (error.Message.StartsWith("AI provider error", StringComparison.Ordinal))
                    { throw new InvalidOperationException(error.Message + " (HTTP " + (int)response.StatusCode + ")"); }
                    catch (InvalidOperationException) { }
                    throw new InvalidOperationException("The AI provider answered HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + ". Check the base URL, model and API key.");
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
            return errors;
        }

        /// <summary>Estimated plan XML of each statement (SET SHOWPLAN_XML ON); the script is compiled, not run.</summary>
        internal static string Plan(ActiveConnection connection, string sql)
        {
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
            return plans.ToString();
        }

        /// <summary>Runs the script for real, batch by batch, and returns result sets, messages and row counts as JSON. Stops at the first failing batch.</summary>
        internal static string Execute(ActiveConnection connection, string sql, int maxRows)
        {
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
            return MiniJson.Write(result);
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

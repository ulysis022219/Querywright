using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Querywright.Core;

namespace Querywright.Ssms
{
    /// <summary>Model Context Protocol server (streamable HTTP, JSON replies) on the loopback interface, so an external AI
    /// agent can read the active query window, inspect the connected database and run SQL on the window's own login.
    /// Every request needs the bearer token from Querywright > Copy MCP configuration.</summary>
    internal sealed class McpHost : IDisposable
    {
        private const int MaxBody = 4 * 1024 * 1024, MaxText = 60_000;
        private static readonly string TokenFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Querywright", "mcp-token.txt");
        private static string token;

        private readonly WorkbenchPackage package;
        private readonly HttpListener listener;
        private readonly McpProtocol protocol;

        internal McpHost(WorkbenchPackage package, int port)
        {
            if (port < 1024 || port > 65535) throw new ArgumentException("the port must be between 1024 and 65535");
            this.package = package;
            Token();
            protocol = new McpProtocol("querywright", typeof(McpHost).Assembly.GetName().Version.ToString(), Tools());
            listener = Listen(port);
            Diagnostics.Write("mcp", "started");
            _ = Task.Run(AcceptAsync);
        }

        public void Dispose()
        {
            Diagnostics.Write("mcp", "stopped");
            try { listener.Close(); }
            catch (Exception error) when (error is HttpListenerException || error is ObjectDisposedException) { }
        }

        /// <summary>The access token, created on first use and kept for this Windows account. Delete the file to revoke it.</summary>
        internal static string Token()
        {
            if (token != null) return token;
            try
            {
                if (File.Exists(TokenFile))
                {
                    string saved = File.ReadAllText(TokenFile).Trim();
                    if (saved.Length >= 32) { Diagnostics.SetSecret(saved); return token = saved; }
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { }
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            string made = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            try { Directory.CreateDirectory(Path.GetDirectoryName(TokenFile)); File.WriteAllText(TokenFile, made); }
            // Not saved: the token then lasts until SSMS closes.
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { }
            Diagnostics.SetSecret(made); return token = made;
        }

        // Loopback prefixes only: http.sys drops any request whose Host is something else, which also stops DNS rebinding.
        private static HttpListener Listen(int port)
        {
            foreach (bool both in new[] { true, false })
            {
                var made = new HttpListener();
                made.Prefixes.Add("http://localhost:" + port + "/");
                if (both) made.Prefixes.Add("http://127.0.0.1:" + port + "/");
                try { made.Start(); return made; }
                // Some machines only let a non-administrator register the localhost prefix.
                catch (HttpListenerException) when (both) { made.Close(); }
            }
            throw new InvalidOperationException("no listener");
        }

        private async Task AcceptAsync()
        {
            while (true)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync().ConfigureAwait(false); }
                catch (Exception error) when (error is HttpListenerException || error is ObjectDisposedException || error is InvalidOperationException) { return; }
                _ = Task.Run(() => Serve(context));
            }
        }

        private void Serve(HttpListenerContext context)
        {
            var response = context.Response;
            try
            {
                var request = context.Request;
                // A web page in the user's browser must never reach the database: only loopback origins, and never without the token.
                string origin = request.Headers["Origin"];
                if (origin != null && !(Uri.TryCreate(origin, UriKind.Absolute, out var from) && from.IsLoopback)) { Reply(response, 403, null); return; }
                if (!Authorized(request.Headers["Authorization"]))
                {
                    response.AddHeader("WWW-Authenticate", "Bearer");
                    Reply(response, 401, null);
                    return;
                }
                string path = request.Url.AbsolutePath.TrimEnd('/');
                if (path.Length > 0 && !path.Equals("/mcp", StringComparison.OrdinalIgnoreCase)) { Reply(response, 404, null); return; }
                if (request.HttpMethod != "POST")
                {
                    response.AddHeader("Allow", "POST");
                    Reply(response, 405, null);
                    return;
                }
                string body = Body(request);
                if (body == null) { Reply(response, 413, null); return; }
                Diagnostics.Write("mcp", "request", text: body);
                string answer = protocol.Handle(body);
                Diagnostics.Write("mcp", "response", text: answer);
                Reply(response, answer == null ? 202 : 200, answer);
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                Diagnostics.Write("mcp", "failed", "error", error: error);
                try { Reply(response, 500, null); }
                catch (Exception again) when (again is HttpListenerException || again is ObjectDisposedException || again is InvalidOperationException) { }
            }
        }

        private static bool Authorized(string header)
        {
            const string scheme = "Bearer ";
            if (header == null || !header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;
            byte[] given = Encoding.UTF8.GetBytes(header.Substring(scheme.Length).Trim()), wanted = Encoding.UTF8.GetBytes(Token());
            int difference = given.Length ^ wanted.Length;
            for (int i = 0; i < wanted.Length; i++) difference |= wanted[i] ^ (i < given.Length ? given[i] : 0);
            return difference == 0;
        }

        private static string Body(HttpListenerRequest request)
        {
            if (request.ContentLength64 > MaxBody) return null;
            using (var memory = new MemoryStream())
            {
                var buffer = new byte[81920];
                int read;
                while ((read = request.InputStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (memory.Length + read > MaxBody) return null;
                    memory.Write(buffer, 0, read);
                }
                return Encoding.UTF8.GetString(memory.ToArray());
            }
        }

        private static void Reply(HttpListenerResponse response, int status, string json)
        {
            response.StatusCode = status;
            if (json == null) { response.ContentLength64 = 0; response.Close(); return; }
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            response.ContentType = "application/json";
            response.Close(bytes, willBlock: false);
        }

        // Tools run on a pool thread; the editor and the connection are read on the UI thread.
        private static T OnUi<T>(Func<T> read) => ThreadHelper.JoinableTaskFactory.Run(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            return read();
        });

        private ActiveConnection Connection() => OnUi(() => package.AiConnection(required: true));

        private IReadOnlyList<SchemaTable> Tables(bool required = true)
        {
            var connection = OnUi(() => package.AiConnection(required: false));
            var tables = ThreadHelper.JoinableTaskFactory.Run(() => package.AiTablesAsync(connection, 30));
            if (tables == null && required) throw new InvalidOperationException("No schema is available: connect the query window to a database, or set an offline schema file.");
            return tables;
        }

        private string Sql(IReadOnlyDictionary<string, object> arguments)
        {
            string sql = McpProtocol.Argument(arguments, "sql");
            if (string.IsNullOrWhiteSpace(sql)) sql = OnUi(() => package.AiEditorText());
            if (string.IsNullOrWhiteSpace(sql)) throw new InvalidOperationException("No SQL: pass the sql argument, or open a query window with a script in it.");
            return sql;
        }

        // ponytail: "schema.name" splits at the first dot; an object whose own name contains a dot is not reachable.
        private static (string Schema, string Name) Split(IReadOnlyDictionary<string, object> arguments)
        {
            string name = (McpProtocol.Argument(arguments, "name") ?? "").Replace("[", "").Replace("]", "").Replace("\"", "").Trim();
            if (name.Length == 0) throw new InvalidOperationException("Give the object name as schema.name.");
            int dot = name.IndexOf('.');
            return dot < 0 ? ("dbo", name) : (name.Substring(0, dot), name.Substring(dot + 1));
        }

        private static string Clip(string text) => text.Length <= MaxText ? text : text.Substring(0, MaxText) + "\n... (" + (text.Length - MaxText) + " more characters cut)";

        private static string Lines(IEnumerable<string> lines, string none)
        {
            string text = string.Join("\n", lines);
            return text.Length == 0 ? none : Clip(text);
        }

        // Server and driver failures become tool errors the model can read; anything else stays a tool error too, without internals.
        private static Func<IReadOnlyDictionary<string, object>, string> Safe(Func<IReadOnlyDictionary<string, object>, string> handler) => arguments =>
        {
            try { return handler(arguments); }
            catch (SqlException error) { throw new InvalidOperationException(WorkbenchPackage.Reason(error)); }
            catch (Exception error) when (!(error is InvalidOperationException || error is FormatException || error is ArgumentException || error is OutOfMemoryException))
            { throw new InvalidOperationException("The tool failed: " + error.GetType().Name + "."); }
        };

        private static McpTool Tool(string name, string description, Func<IReadOnlyDictionary<string, object>, string> handler, params (string, string)[] arguments) =>
            new McpTool(name, description, Safe(handler), true, arguments);

        private string Names(IReadOnlyDictionary<string, object> arguments, bool views)
        {
            string filter = McpProtocol.Argument(arguments, "filter") ?? "";
            return Lines(Tables().Where(t => t.IsView == views).Select(t => t.Schema + "." + t.Name)
                .Where(n => n.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(n => n, StringComparer.OrdinalIgnoreCase), views ? "No views." : "No tables.");
        }

        private string Script(IReadOnlyDictionary<string, object> arguments)
        {
            var (schema, name) = Split(arguments);
            var details = LiveMetadata.Details(Connection(), schema, name);
            if (details == null) throw new InvalidOperationException("No object named " + schema + "." + name + " in this database.");
            return Clip(WorkbenchPackage.AiScript(details, schema, name));
        }

        private string Related(IReadOnlyDictionary<string, object> arguments)
        {
            var (schema, name) = Split(arguments);
            bool Is(string s, string n) => string.Equals(s, schema, StringComparison.OrdinalIgnoreCase) && string.Equals(n, name, StringComparison.OrdinalIgnoreCase);
            var lines = new List<string>();
            foreach (var table in Tables(required: false) ?? new SchemaTable[0])
                foreach (var key in table.ForeignKeys)
                {
                    bool outgoing = Is(table.Schema, table.Name), incoming = Is(key.ReferencedSchema, key.ReferencedTable);
                    if (!outgoing && !incoming) continue;
                    lines.Add((outgoing ? "references " : "referenced by ") + (outgoing ? key.ReferencedSchema + "." + key.ReferencedTable : table.Schema + "." + table.Name)
                        + " (" + table.Schema + "." + table.Name + "." + string.Join(",", key.Columns) + " -> " + key.ReferencedSchema + "." + key.ReferencedTable + "." + string.Join(",", key.ReferencedColumns) + ")");
                }
            lines.AddRange(LiveMetadata.Dependents(Connection(), schema, name).Select(d => "used by " + d.Schema + "." + d.Name));
            return Lines(lines.Distinct(), "Nothing references " + schema + "." + name + " and it references nothing.");
        }

        private static string Query(ActiveConnection connection, string query)
        {
            var lines = new List<string>();
            using (var server = connection.Open())
            using (var command = new SqlCommand(query, server) { CommandTimeout = 30 })
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    lines.Add(string.Join("", Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
            return Lines(lines, "None.");
        }

        private const string DatabaseQuery = @"
SELECT 'Server: ' + CONVERT(nvarchar(256), SERVERPROPERTY('ServerName')) UNION ALL
SELECT 'Database: ' + DB_NAME() UNION ALL
SELECT 'Login: ' + SUSER_SNAME() UNION ALL
SELECT 'Compatibility level: ' + CONVERT(nvarchar(10), compatibility_level) FROM sys.databases WHERE database_id = DB_ID() UNION ALL
SELECT 'Collation: ' + CONVERT(nvarchar(256), DATABASEPROPERTYEX(DB_NAME(), 'Collation')) UNION ALL
SELECT 'Edition: ' + CONVERT(nvarchar(256), SERVERPROPERTY('Edition')) UNION ALL
SELECT 'Version: ' + REPLACE(REPLACE(@@VERSION, CHAR(10), ' '), CHAR(9), ' ');";

        private const string ProcedureQuery = "SELECT SCHEMA_NAME(schema_id) + '.' + name FROM sys.procedures WHERE name LIKE '%' + @filter + '%' ORDER BY 1;";

        private string Procedures(IReadOnlyDictionary<string, object> arguments)
        {
            var lines = new List<string>();
            using (var server = Connection().Open())
            using (var command = new SqlCommand(ProcedureQuery, server) { CommandTimeout = 30 })
            {
                // Escaped so the filter is a plain substring, never a pattern.
                string filter = (McpProtocol.Argument(arguments, "filter") ?? "").Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
                command.Parameters.AddWithValue("@filter", filter);
                using (var reader = command.ExecuteReader()) while (reader.Read()) lines.Add(reader.GetString(0));
            }
            return Lines(lines, "No stored procedures.");
        }

        private string Execute(IReadOnlyDictionary<string, object> arguments)
        {
            string sql = McpProtocol.Argument(arguments, "sql");
            if (string.IsNullOrWhiteSpace(sql)) throw new InvalidOperationException("Missing argument: sql");
            var connection = OnUi(() =>
            {
                var active = package.AiConnection(required: true);
                if (package.Ai.ConfirmExecution && !package.ConfirmRun(sql, "An MCP client asks to run this SQL on the query window's connection. Run it?"))
                    throw new InvalidOperationException("The user declined to run this SQL.");
                return active;
            });
            return AiSql.Execute(connection, sql, McpProtocol.Argument(arguments, "max_rows", 200));
        }

        private List<McpTool> Tools()
        {
            const string optionalSql = "T-SQL to work on; the active query window's script when omitted";
            const string objectName = "Object name as schema.name";
            return new List<McpTool>
            {
                Tool("get_active_query_editor_contents", "Full text of the active SSMS query window.", a => OnUi(() => package.AiEditorText())),
                Tool("get_active_query_editor_selected_text", "Text selected in the active SSMS query window; empty when nothing is selected.", a => OnUi(() => package.AiEditorText(selectionOnly: true))),
                Tool("get_database_information", "Server, database, login, compatibility level, collation and SQL Server version of the active query window's connection.", a => Query(Connection(), DatabaseQuery)),
                Tool("list_tables", "Tables of the connected database as schema.name.", a => Names(a, views: false), ("filter?", "Only names containing this text")),
                Tool("list_views", "Views of the connected database as schema.name.", a => Names(a, views: true), ("filter?", "Only names containing this text")),
                Tool("list_stored_procedures", "Stored procedures of the connected database as schema.name.", Procedures, ("filter?", "Only names containing this text")),
                Tool("list_table_details", "CREATE script of a table: columns, types, keys and constraints.", Script, ("name", objectName)),
                Tool("list_view_details", "Definition of a view.", Script, ("name", objectName)),
                Tool("list_stored_procedure_details", "Definition and parameters of a stored procedure or function.", Script, ("name", objectName)),
                Tool("list_related_objects", "Foreign keys to and from a table, and the procedures, views and functions that use an object.", Related, ("name", objectName)),
                Tool("format_tsql", "Formats T-SQL with the user's Querywright formatting style and returns it. The query window is not changed.",
                    a => SqlFormatting.Format(Sql(a), package.AiSettings().Formatting), ("sql?", optionalSql)),
                Tool("list_tsql_execution_errors", "Compile errors SQL Server reports for the script, per batch, found with SET NOEXEC ON. Nothing is run.",
                    a => Lines(AiSql.Errors(Connection(), Sql(a)), "No compile errors."), ("sql?", optionalSql)),
                Tool("list_tsql_code_analysis_issues", "Querywright code analysis findings (rule, position, message) for the script. Needs no connection.",
                    a => Lines(SqlAnalysis.Analyze(Sql(a), default, package.AiSettings()).Diagnostics.Select(d => "Line " + d.Line + ", column " + d.Column + ": [" + d.Rule + "] " + d.Message), "No issues."),
                    ("sql?", optionalSql)),
                Tool("get_query_execution_plan", "Estimated execution plan XML of the script (SET SHOWPLAN_XML ON). Nothing is run.",
                    a => { string plan = AiSql.Plan(Connection(), Sql(a)); return plan.Length == 0 ? "SQL Server returned no plan." : Clip(plan); }, ("sql?", optionalSql)),
                Tool("fix_code_analysis_issues", "Applies every automatic Querywright code analysis fix to the script and returns the result. The query window is not changed.",
                    a =>
                    {
                        var (text, count) = SqlAnalysis.FixAll(Sql(a), Tables(required: false), "dbo", package.AiSettings());
                        return MiniJson.Write(new Dictionary<string, object> { ["fixed"] = count, ["sql"] = text });
                    }, ("sql?", optionalSql)),
                new McpTool("execute_sql", "Executes T-SQL for real on the active query window's connection and login, batch by batch (GO separates batches), and returns result sets, messages and rows affected as JSON. Changes are not rolled back.",
                    Safe(Execute), false, ("sql", "T-SQL to execute"), ("max_rows#?", "Rows returned per result set; 200 when omitted, at most 5000")),
            };
        }
    }
}

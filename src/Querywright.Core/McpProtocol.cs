using System;
using System.Collections.Generic;
using System.Linq;

namespace Querywright.Core
{
    /// <summary>One tool an MCP client can call. The handler returns the text shown to the model and throws
    /// <see cref="InvalidOperationException"/> for a problem the model should read (reported as a tool error, not a protocol error).</summary>
    public sealed class McpTool
    {
        public string Name { get; }
        public string Description { get; }
        /// <summary>Argument name to description; a name ending in '?' is optional, one ending in '#' is an integer.</summary>
        public IReadOnlyList<(string Name, string Description)> Arguments { get; }
        public Func<IReadOnlyDictionary<string, object?>, string> Handler { get; }
        /// <summary>False for a tool that changes data or the editor; sent to clients as the readOnlyHint annotation.</summary>
        public bool ReadOnly { get; }

        public McpTool(string name, string description, Func<IReadOnlyDictionary<string, object?>, string> handler, bool readOnly = true, params (string Name, string Description)[] arguments)
        {
            Name = name; Description = description; Handler = handler; ReadOnly = readOnly; Arguments = arguments;
        }

        internal object Describe()
        {
            var properties = new Dictionary<string, object?>();
            var required = new List<object?>();
            foreach (var (raw, description) in Arguments)
            {
                string name = raw.TrimEnd('?', '#');
                properties[name] = new Dictionary<string, object?> { ["type"] = raw.Contains("#") ? "integer" : "string", ["description"] = description };
                if (!raw.Contains("?")) required.Add(name);
            }
            return new Dictionary<string, object?>
            {
                ["name"] = Name, ["description"] = Description,
                ["inputSchema"] = new Dictionary<string, object?> { ["type"] = "object", ["properties"] = properties, ["required"] = required },
                ["annotations"] = new Dictionary<string, object?> { ["readOnlyHint"] = ReadOnly, ["destructiveHint"] = !ReadOnly }
            };
        }
    }

    /// <summary>Model Context Protocol over JSON-RPC 2.0: initialize, ping, tools/list and tools/call. Transport-free;
    /// the host passes each request body in and writes the result back.</summary>
    public sealed class McpProtocol
    {
        private static readonly string[] Versions = { "2025-06-18", "2025-03-26", "2024-11-05" };
        private readonly string name, version;
        private readonly Dictionary<string, McpTool> tools;

        public McpProtocol(string name, string version, IEnumerable<McpTool> tools)
        {
            this.name = name; this.version = version;
            this.tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        }

        /// <summary>Response JSON, or null when the message needs none (a notification).</summary>
        public string? Handle(string request)
        {
            object? message;
            try { message = MiniJson.Parse(request); }
            catch (FormatException) { return Error(null, -32700, "Parse error"); }
            if (message is List<object?> batch)
            {
                if (batch.Count == 0) return Error(null, -32600, "Invalid request");
                var answers = batch.Select(One).Where(a => a != null).ToList();
                return answers.Count == 0 ? null : MiniJson.Write(answers);
            }
            var answer = One(message);
            return answer == null ? null : MiniJson.Write(answer);
        }

        private static string Error(object? id, int code, string message) => MiniJson.Write(Failure(id, code, message));

        private static Dictionary<string, object?> Failure(object? id, int code, string message) => new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0", ["id"] = id,
            ["error"] = new Dictionary<string, object?> { ["code"] = code, ["message"] = message }
        };

        private Dictionary<string, object?>? One(object? message)
        {
            if (!(message is Dictionary<string, object?> call) || !(MiniJson.Get(call, "method") is string method))
                return Failure(MiniJson.Get(message, "id"), -32600, "Invalid request");
            bool notification = !call.ContainsKey("id");
            object? id = MiniJson.Get(call, "id");
            var parameters = MiniJson.Get(call, "params");
            object? result;
            switch (method)
            {
                case "initialize":
                    string? asked = MiniJson.Text(parameters, "protocolVersion");
                    result = new Dictionary<string, object?>
                    {
                        ["protocolVersion"] = asked != null && Versions.Contains(asked) ? asked : Versions[0],
                        ["capabilities"] = new Dictionary<string, object?> { ["tools"] = new Dictionary<string, object?> { ["listChanged"] = false } },
                        ["serverInfo"] = new Dictionary<string, object?> { ["name"] = name, ["version"] = version },
                        ["instructions"] = "Tools act on the SQL Server Management Studio window the user has open: its active query editor and that editor's database connection."
                    };
                    break;
                case "ping": result = new Dictionary<string, object?>(); break;
                case "tools/list":
                    result = new Dictionary<string, object?> { ["tools"] = tools.Values.Select(t => t.Describe()).ToList() };
                    break;
                case "tools/call":
                    string? toolName = MiniJson.Text(parameters, "name");
                    if (toolName == null) return notification ? null : Failure(id, -32602, "Missing tool name");
                    if (!tools.TryGetValue(toolName, out var tool)) return notification ? null : Failure(id, -32602, "Unknown tool: " + toolName);
                    var arguments = MiniJson.Get(parameters, "arguments") as Dictionary<string, object?> ?? new Dictionary<string, object?>();
                    string? missing = tool.Arguments.Select(a => a.Name).FirstOrDefault(a => !a.Contains("?") && !arguments.ContainsKey(a.TrimEnd('#')));
                    string text; bool failed = false;
                    if (missing != null) { text = "Missing argument: " + missing.TrimEnd('#'); failed = true; }
                    else
                        try { text = tool.Handler(arguments); }
                        // Tool problems go back as content the model can read and react to.
                        catch (Exception error) when (error is InvalidOperationException || error is FormatException || error is ArgumentException) { text = error.Message; failed = true; }
                    result = new Dictionary<string, object?>
                    {
                        ["content"] = new List<object?> { new Dictionary<string, object?> { ["type"] = "text", ["text"] = text } },
                        ["isError"] = failed
                    };
                    break;
                default:
                    return notification ? null : Failure(id, -32601, "Method not found: " + method);
            }
            return notification ? null : new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }

        /// <summary>String argument, or null when absent.</summary>
        public static string? Argument(IReadOnlyDictionary<string, object?> arguments, string name) =>
            arguments.TryGetValue(name, out var value) ? value as string ?? (value == null ? null : MiniJson.Write(value)) : null;

        /// <summary>Integer argument, or the fallback when absent or out of range (an unchecked cast of 1e12 or NaN gives int.MinValue).</summary>
        public static int Argument(IReadOnlyDictionary<string, object?> arguments, string name, int fallback) =>
            arguments.TryGetValue(name, out var value) && value is double number && number >= int.MinValue && number <= int.MaxValue ? (int)number : fallback;
    }
}

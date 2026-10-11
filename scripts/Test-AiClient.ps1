# Loopback HTTP fixtures only. No provider requests, SQL, stored key reads or user file writes.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$resolver = [ResolveEventHandler] {
    param($sender, $eventArgs)
    $ide = 'C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE'
    $name = [Reflection.AssemblyName]::new($eventArgs.Name).Name + '.dll'
    foreach ($folder in 'PublicAssemblies', 'PrivateAssemblies', '') {
        $path = Join-Path (Join-Path $ide $folder) $name
        if (Test-Path -LiteralPath $path) { return [Reflection.Assembly]::LoadFrom($path) }
    }
    return $null
}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
$bin = Join-Path $PSScriptRoot '../src/Querywright.Ssms/bin/Debug/net472'
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $bin 'Querywright.Ssms.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $bin 'Querywright.Core.dll'))
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public sealed class AiFixture : IDisposable
{
    private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
    private TcpClient client;
    public readonly List<bool> Streaming = new List<bool>();
    public readonly List<string> Paths = new List<string>();
    public readonly List<string> Bodies = new List<string>();
    public readonly List<bool> AnthropicVersion = new List<bool>();
    public readonly Task Worker;
    public readonly string Url;
    public AiFixture(string mode)
    {
        listener.Start(); Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        Worker = Task.Run(() =>
        {
            try
            {
                int requests = mode == "fallback" || mode == "models" ? 2 : mode == "reasonfallback" || mode == "anthropicmodels" ? 3 : 1;
                for (int attempt = 0; attempt < requests; attempt++)
                {
                    using (client = listener.AcceptTcpClient())
                    using (var stream = client.GetStream())
                    {
                        string path = ReadLine(stream).Split(' ')[1];
                        int length = 0; string line; bool version = false;
                        while ((line = ReadLine(stream)).Length > 0)
                        {
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Substring(15).Trim());
                            if (line.Equals("anthropic-version: 2023-06-01", StringComparison.OrdinalIgnoreCase)) version = true;
                        }
                        byte[] body = new byte[length]; int at = 0;
                        while (at < length) { int read = stream.Read(body, at, length - at); if (read == 0) throw new IOException("Truncated request"); at += read; }
                        string json = Encoding.UTF8.GetString(body);
                        lock (Streaming) { Streaming.Add(json.Contains("\"stream\":true")); Paths.Add(path); Bodies.Add(json); AnthropicVersion.Add(version); }
                        if (mode == "redirect")
                            Write(stream, "HTTP/1.1 302 Found\r\nLocation: " + Url + "/redirect-target\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
                        else if (mode == "oversize")
                            Write(stream, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n" + new string(' ', 2000001));
                        else if (mode == "largeline")
                            Write(stream, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\ndata: " + new string(' ', 1000001));
                        else if (mode == "models" || mode == "anthropicmodels")
                        {
                            string models = attempt == 0
                                ? "{\"data\":[{\"id\":\"zeta\"},{\"id\":\"alpha\"},{\"id\":\"alpha\"}],\"has_more\":" + (mode == "anthropicmodels" ? "true" : "false") + ",\"last_id\":\"page/one\"}"
                                : mode == "anthropicmodels" && attempt == 1
                                    ? "{\"data\":[{\"id\":\"beta\"},{\"id\":\"alpha\"}],\"has_more\":false,\"last_id\":\"beta\"}"
                                    : "{\"data\":[{\"id\":\"fresh\"}],\"has_more\":false}";
                            Write(stream, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n" + models);
                        }
                        else if (mode == "reasonauth" || mode == "reasoncredit" || mode == "reasonserver" || mode == "reasonbad400" || mode == "reasonfallback" && attempt == 0)
                        {
                            string status = mode == "reasonauth" ? "401 Unauthorized" : mode == "reasonserver" ? "500 Internal Server Error" : "400 Bad Request";
                            string message = mode == "reasoncredit" ? "reasoning_effort and stream not supported; credit exhausted credential echo"
                                : mode == "reasonbad400" ? "model not supported credential echo" : "reasoning_effort not supported credential echo";
                            Write(stream, "HTTP/1.1 " + status + "\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n{\"error\":{\"message\":\"" + message + "\"}}");
                        }
                        else if (mode == "reasonlength" || mode == "reasonlengthplain")
                        {
                            string usage = "\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":4096,\"completion_tokens_details\":{\"reasoning_tokens\":4096}}";
                            string reply = mode == "reasonlengthplain"
                                ? "{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"\",\"reasoning_content\":\"Private thinking\"}}]," + usage + "}"
                                : "data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"Private thinking\"}}]}\n\ndata: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"length\"}]," + usage + "}\n\ndata: [DONE]\n\n";
                            Write(stream, "HTTP/1.1 200 OK\r\nContent-Type: " + (mode == "reasonlengthplain" ? "application/json" : "text/event-stream") + "\r\nConnection: close\r\n\r\n" + reply);
                        }
                        else if (mode == "reasonfallback" || mode == "reasonplain" || mode == "reasonplainanthropic")
                        {
                            string reply = mode == "reasonplainanthropic"
                                ? "{\"content\":[{\"type\":\"thinking\",\"thinking\":\"Private thinking\",\"signature\":\"fixture-signature\"},{\"type\":\"text\",\"text\":\"SELECT 1;\"}],\"usage\":{\"input_tokens\":11,\"output_tokens\":21,\"output_tokens_details\":{\"thinking_tokens\":7}}}"
                                : "{\"choices\":[{\"message\":{\"content\":\"SELECT 1;\",\"reasoning_content\":\"Private thinking\"}}],\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":21,\"completion_tokens_details\":{\"reasoning_tokens\":7}}}";
                            Write(stream, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n" + reply);
                        }
                        else if (mode == "auth" || (mode == "fallback" && attempt == 0))
                        {
                            Write(stream, "HTTP/1.1 " + (mode == "auth" ? "401 Unauthorized" : "422 Unprocessable Entity") + "\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n{\"error\":{\"message\":\"stream not supported credential echo\"}}");
                        }
                        else if (mode == "plain" || mode == "fallback")
                            Write(stream, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n{\"choices\":[{\"message\":{\"content\":\"SELECT 1;\"}}]}");
                        else
                        {
                            bool anthropic = mode == "anthropic" || mode == "reasonanthropic";
                            bool reasoning = mode == "reasonopenai" || mode == "reasonanthropic";
                            Write(stream, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n: ping\n\n");
                            if (reasoning)
                                Write(stream, anthropic
                                    ? "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":11,\"output_tokens\":1}}}\n\nevent: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"Private \"}}\n\nevent: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"thinking\"}}\n\nevent: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"signature_delta\",\"signature\":\"fixture-signature\"}}\n\n"
                                    : "data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"Private \"}}]}\n\ndata: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"thinking\"}}]}\n\n");
                            string first = anthropic
                                ? "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"SELECT \"}}\n\n"
                                : "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"SELECT \"}}]}\n\n";
                            if (mode == "cr") first = first.Replace("\n", "\r");
                            if (mode == "splitcrlf") first = first.Replace("\n", "\r\n");
                            Write(stream, mode == "truncated" ? first.TrimEnd('\n') : first);
                            Thread.Sleep(mode == "cancel" ? 2000 : 80);
                            if (mode == "truncated" || mode == "cancel") continue;
                            string tail = anthropic
                                ? "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"1;\"}}\n\n"
                                : "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"1;\"}}]}\n\n";
                            if (reasoning)
                                tail += anthropic
                                    ? "event: message_delta\ndata: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":13}}\n\nevent: message_delta\ndata: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":21,\"output_tokens_details\":{\"thinking_tokens\":7}}}\n\n"
                                    : "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":21,\"completion_tokens_details\":{\"reasoning_tokens\":7}}}\n\n";
                            tail += anthropic ? "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n" : "data: [DONE]\n\n";
                            if (mode == "cr") tail = tail.Replace("\n", "\r");
                            if (mode == "splitcrlf") tail = tail.Replace("\n", "\r\n");
                            byte[] bytes = Encoding.UTF8.GetBytes(tail);
                            for (int n = 0; n < bytes.Length; n += 7) stream.Write(bytes, n, Math.Min(7, bytes.Length - n));
                        }
                    }
                }
            }
            catch (Exception error) { if (!(error is SocketException) && !(error is ObjectDisposedException) && !(error is IOException)) throw; }
        });
    }
    private static string ReadLine(Stream stream)
    {
        var bytes = new List<byte>(); int value;
        while ((value = stream.ReadByte()) >= 0 && value != '\n') if (value != '\r') bytes.Add((byte)value);
        return Encoding.ASCII.GetString(bytes.ToArray());
    }
    private static void Write(Stream stream, string text) { byte[] bytes = Encoding.UTF8.GetBytes(text); stream.Write(bytes, 0, bytes.Length); stream.Flush(); }
    public void Dispose() { listener.Stop(); if (client != null) client.Close(); Worker.Wait(3000); }
}
public sealed class AiFixtureProgress : IProgress<string>
{
    private readonly StringBuilder text = new StringBuilder();
    public int Reports;
    public string Text { get { lock (text) return text.ToString(); } }
    public void Report(string delta) { lock (text) { text.Append(delta); Reports++; } }
}
'@
function Check($condition, $message) { if (!$condition) { throw $message } }
$keyPath = $assembly.GetType('Querywright.Ssms.AiKey', $true).GetField('File', [Reflection.BindingFlags]'NonPublic,Static')
$originalPath = $keyPath.GetValue($null)
$ask = $assembly.GetType('Querywright.Ssms.AiClient', $true).GetMethod('AskAsync', [Reflection.BindingFlags]'NonPublic,Static')
$reply = $assembly.GetType('Querywright.Ssms.AiClient', $true).GetMethod('ReplyAsync', [Reflection.BindingFlags]'NonPublic,Static')
$models = $assembly.GetType('Querywright.Ssms.AiClient', $true).GetMethod('ModelsAsync', [Reflection.BindingFlags]'NonPublic,Static')
$diagnostics = $assembly.GetType('Querywright.Ssms.AiClient', $true).GetField('DiagnosticLog', [Reflection.BindingFlags]'NonPublic,Static').GetValue($null)
$processLogs = $assembly.GetType('Querywright.Ssms.Diagnostics', $true)
$capture = $processLogs.GetProperty('Enabled', [Reflection.BindingFlags]'NonPublic,Static')
$messages = [Collections.Generic.List[Querywright.Core.AiMessage]]::new()
$messages.Add([Querywright.Core.AiMessage]::new('user', 'fixture only'))
try {
    # Redirect only this test process's private field to a nonexistent path; never read the real saved key.
    $keyPath.SetValue($null, (Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N') + '.missing')))
    foreach ($mode in 'openai', 'anthropic', 'fallback', 'plain', 'auth', 'truncated', 'cancel', 'redirect', 'oversize', 'largeline', 'cr', 'splitcrlf') {
        $server = [AiFixture]::new($mode)
        $progress = [AiFixtureProgress]::new()
        $cancel = [Threading.CancellationTokenSource]::new()
        try {
            $format = [Enum]::Parse($assembly.GetType('Querywright.Ssms.AiApiFormat'), $(if ($mode -in 'anthropic', 'redirect') { 'Anthropic' } else { 'OpenAiCompatible' }))
            $task = $ask.Invoke($null, @($format, $server.Url, 'fixture', $messages, $cancel.Token, 4096, $progress))
            $cancel.CancelAfter(6000)
            if ($mode -eq 'cancel') {
                $deadline = [DateTime]::UtcNow.AddSeconds(5)
                while ($progress.Text.Length -eq 0 -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 10 }
                Check ($progress.Text -eq 'SELECT ') 'Cancellation fixture must receive a delta first'
                $clock = [Diagnostics.Stopwatch]::StartNew(); $cancel.Cancel()
                try { $task.GetAwaiter().GetResult(); throw 'Cancellation unexpectedly succeeded' }
                catch { Check $task.IsCanceled 'Stop must cancel the HTTP read'; Check ($clock.ElapsedMilliseconds -lt 1000) 'Stop must promptly cancel a stalled stream'; Check ($progress.Text -eq 'SELECT ') 'Stop must preserve received text' }
            } elseif ($mode -in 'auth', 'truncated', 'redirect', 'oversize', 'largeline') {
                try { $task.GetAwaiter().GetResult(); throw 'Failure fixture unexpectedly succeeded' }
                catch {
                    Check $task.IsFaulted 'Failure fixture must fault'
                    Check (!$task.Exception.ToString().Contains('credential echo')) 'Provider errors must not be echoed'
                    if ($mode -eq 'truncated') { Check ($progress.Text -eq 'SELECT ') 'Truncation must preserve partial text' }
                    if ($mode -eq 'redirect') { Check ($task.Exception.ToString().Contains('HTTP 302')) 'Provider redirects must fail without following Location' }
                    if ($mode -eq 'largeline') { Check ($task.Exception.ToString().Contains('oversized stream line')) 'SSE lines must be bounded before buffering unlimited text' }
                }
            } else {
                Check ($task.GetAwaiter().GetResult() -eq 'SELECT 1;') "$mode answer must be complete"
                if ($mode -in 'openai', 'anthropic', 'cr', 'splitcrlf') { Check ($progress.Text -eq 'SELECT 1;' -and $progress.Reports -eq 2) "$mode deltas must arrive in order" }
            }
            Check ($server.Worker.Wait(5000)) 'Fixture worker timed out'
            if ($mode -eq 'fallback') { Check ($server.Streaming.Count -eq 2 -and $server.Streaming[0] -and !$server.Streaming[1]) 'Fallback must retry once with stream:false' }
            else { Check ($server.Streaming.Count -eq 1 -and $server.Streaming[0]) 'Other fixtures must use one streaming request' }
        } finally { $cancel.Dispose(); $server.Dispose() }
    }
    foreach ($mode in 'reasonopenai', 'reasonanthropic', 'reasonplain', 'reasonplainanthropic', 'reasonfallback', 'reasonauth', 'reasoncredit', 'reasonserver', 'reasonbad400') {
        $server = [AiFixture]::new($mode)
        $progress = [AiFixtureProgress]::new()
        $thoughts = [AiFixtureProgress]::new()
        $cancel = [Threading.CancellationTokenSource]::new()
        try {
            $cancel.CancelAfter(6000)
            $format = [Enum]::Parse($assembly.GetType('Querywright.Ssms.AiApiFormat'), $(if ($mode.EndsWith('anthropic')) { 'Anthropic' } else { 'OpenAiCompatible' }))
            $textProgress = $(if ($mode -in 'reasonplain', 'reasonplainanthropic') { $null } else { $progress })
            $extra = $(if ($mode -eq 'reasonfallback') { '{"provider":{"thinking":true},"reasoning":{"effort":"high"},"fixture_option":true,"temperature":null}' } else { '' })
            $task = $reply.Invoke($null, @($format, $server.Url, 'fixture', $messages, $cancel.Token, 4096, $textProgress, [Querywright.Core.AiReasoningLevel]::High, $extra, $thoughts))
            if ($mode -in 'reasonauth', 'reasoncredit', 'reasonserver', 'reasonbad400') {
                try { $task.GetAwaiter().GetResult(); throw 'Reasoning failure unexpectedly succeeded' }
                catch { Check $task.IsFaulted "$mode must fault without retry"; Check (!$task.Exception.ToString().Contains('credential echo')) 'Provider errors must not be echoed' }
            } else {
                $result = $task.GetAwaiter().GetResult()
                Check ($result.Text -eq 'SELECT 1;' -and $result.Reasoning -eq 'Private thinking') "$mode must separate answer and reasoning"
                Check ($result.InputTokens -eq 11 -and $result.OutputTokens -eq 21 -and $result.ReasoningTokens -eq 7) "$mode must report token usage without adding cumulative counts"
                if ($mode -in 'reasonopenai', 'reasonanthropic') {
                    Check ($progress.Text -eq 'SELECT 1;' -and $progress.Reports -eq 2) "$mode must stream answer separately"
                    Check ($thoughts.Text -eq 'Private thinking' -and $thoughts.Reports -eq 2) "$mode must stream reasoning separately and omit signatures"
                }
                if ($mode -eq 'reasonfallback') {
                    Check $result.ReasoningUnsupported 'Explicit rejection must report unsupported reasoning'
                    $second = $reply.Invoke($null, @($format, $server.Url, 'fixture', $messages, $cancel.Token, 4096, $progress, [Querywright.Core.AiReasoningLevel]::High, $extra, $thoughts)).GetAwaiter().GetResult()
                    Check ($second.ReasoningUnsupported -and $second.Text -eq 'SELECT 1;') 'Session suppression must remain visible'
                    $firstBody = $server.Bodies[0] | ConvertFrom-Json
                    Check ($firstBody.reasoning_effort -eq 'high') 'First request must contain requested reasoning'
                    foreach ($body in $server.Bodies.GetRange(1, 2)) {
                        $request = $body | ConvertFrom-Json
                        Check (!$request.PSObject.Properties['reasoning_effort'] -and !$request.PSObject.Properties['thinking'] -and !$request.PSObject.Properties['reasoning'] -and !$request.provider.PSObject.Properties['thinking']) 'Retry and next request must omit mapped and extra reasoning fields'
                        Check $request.fixture_option 'Reasoning fallback must retain unrelated provider options'
                    }
                }
            }
            Check ($server.Worker.Wait(5000)) 'Reasoning fixture worker timed out'
            Check ($server.Streaming.Count -eq $(if ($mode -eq 'reasonfallback') { 3 } else { 1 })) "$mode request count must match retry policy"
            foreach ($streaming in $server.Streaming) { Check ($streaming -eq ($mode -notin 'reasonplain', 'reasonplainanthropic')) "$mode must retain requested stream setting" }
        } finally { $cancel.Dispose(); $server.Dispose() }
    }
    $capture.SetValue($null, $true)
    foreach ($mode in 'reasonlength', 'reasonlengthplain') {
        $server = [AiFixture]::new($mode); $progress = [AiFixtureProgress]::new(); $thoughts = [AiFixtureProgress]::new()
        $cancel = [Threading.CancellationTokenSource]::new(); $cancel.CancelAfter(6000)
        try {
            $format = [Enum]::Parse($assembly.GetType('Querywright.Ssms.AiApiFormat'), 'OpenAiCompatible')
            $task = $reply.Invoke($null, @($format, $server.Url, 'fixture', $messages, $cancel.Token, 4096, $(if ($mode -eq 'reasonlength') { $progress } else { $null }), [Querywright.Core.AiReasoningLevel]::High, '', $thoughts))
            try { $task.GetAwaiter().GetResult(); throw 'Length fixture unexpectedly succeeded' }
            catch { Check ($task.IsFaulted -and $task.Exception.ToString().Contains('token limit') -and $task.Exception.ToString().Contains('Out: 4096')) 'Reasoning-only length exhaustion must show cause and usage' }
            Check ($server.Worker.Wait(5000) -and $server.Bodies.Count -eq 1) 'Token exhaustion must not silently bill a retry'
            $entry = $diagnostics.Entries[$diagnostics.Entries.Count - 1]
            Check ($entry.FinishReason -eq 'length' -and $entry.OutputTokens -eq 4096 -and $entry.ReasoningTokens -eq 4096 -and $entry.AnswerCharacters -eq 0 -and $entry.ReasoningCharacters -eq 16) 'Diagnostics must retain finish reason, usage and character counts from failed response'
            Check ($entry.Outcome -eq 'empty' -and $entry.ErrorCategory -eq 'empty_answer' -and $entry.MaxOutputTokens -eq 4096 -and $entry.HttpStatus -eq 200) 'Diagnostics must identify successful HTTP response with empty final answer'
            if ($mode -eq 'reasonlength') { Check ($thoughts.Text -eq 'Private thinking' -and $progress.Text.Length -eq 0) 'Stream failure must preserve reasoning separately' }
        } finally { $cancel.Dispose(); $server.Dispose() }
    }
    $verbose = $processLogs.GetMethod('Export', [Reflection.BindingFlags]'NonPublic,Static').Invoke($null, @())
    Check ($verbose.Contains('finish_reason') -and $verbose.Contains('Private thinking') -and $verbose.Contains('fixture only')) 'Verbose diagnostics must include request text, raw stream fields and response text'
    $capture.SetValue($null, $false)
    foreach ($mode in 'models', 'anthropicmodels') {
        $server = [AiFixture]::new($mode)
        $cancel = [Threading.CancellationTokenSource]::new()
        try {
            $cancel.CancelAfter(6000)
            $anthropic = $mode -eq 'anthropicmodels'
            $format = [Enum]::Parse($assembly.GetType('Querywright.Ssms.AiApiFormat'), $(if ($anthropic) { 'Anthropic' } else { 'OpenAiCompatible' }))
            $first = $models.Invoke($null, @($format, $server.Url, $cancel.Token, $false)).GetAwaiter().GetResult()
            Check (($first -join ',') -eq $(if ($anthropic) { 'alpha,beta,zeta' } else { 'alpha,zeta' })) "$mode must merge, deduplicate and sort discovery results"
            $cached = $models.Invoke($null, @($format, $server.Url, $cancel.Token, $false)).GetAwaiter().GetResult()
            Check ([object]::ReferenceEquals($first, $cached)) 'Second discovery must return session cache'
            $fresh = $models.Invoke($null, @($format, $server.Url, $cancel.Token, $true)).GetAwaiter().GetResult()
            Check (($fresh -join ',') -eq 'fresh') 'Refresh must replace cached model list'
            Check ($server.Worker.Wait(5000)) 'Model fixture worker timed out'
            Check ($server.Paths.Count -eq $(if ($anthropic) { 3 } else { 2 })) 'Cache must avoid HTTP; refresh must make a request'
            Check ($server.Paths[0] -eq $(if ($anthropic) { '/v1/models' } else { '/models' })) 'Discovery must use correct format endpoint'
            if ($anthropic) { Check ($server.Paths[1] -eq '/v1/models?after_id=page%2Fone') 'Anthropic next page must URL-escape last_id' }
            foreach ($body in $server.Bodies) { Check ($body.Length -eq 0) 'Discovery must use GET without a request body' }
            foreach ($version in $server.AnthropicVersion) { Check ($version -eq $anthropic) 'Anthropic discovery must retain version header' }
        } finally { $cancel.Dispose(); $server.Dispose() }
    }
} finally { $keyPath.SetValue($null, $originalPath); $capture.SetValue($null, $false); [AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver) }
Write-Host 'PASS: AI HTTP fixtures for both formats, reasoning and usage, JSON fallback, rejection/session suppression, no auth/credit/server retries or redirects, bounded responses, model cache/refresh/pagination, truncation and prompt Stop. No provider or database used.'

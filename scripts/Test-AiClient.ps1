# Loopback HTTP fixtures only. No provider requests, SQL, stored key reads or user file writes.
$ErrorActionPreference = 'Stop'
$bin = Join-Path $PSScriptRoot '../src/Querywright.Ssms/bin/Debug/net472'
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $bin 'Querywright.Ssms.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $bin 'Querywright.Core.dll'))
Add-Type -AssemblyName System.Net.Http
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
    public readonly Task Worker;
    public readonly string Url;
    public AiFixture(string mode)
    {
        listener.Start(); Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        Worker = Task.Run(() =>
        {
            try
            {
                for (int attempt = 0; attempt < (mode == "fallback" ? 2 : 1); attempt++)
                {
                    using (client = listener.AcceptTcpClient())
                    using (var stream = client.GetStream())
                    {
                        int length = 0; string line;
                        while ((line = ReadLine(stream)).Length > 0)
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Substring(15).Trim());
                        byte[] body = new byte[length]; int at = 0;
                        while (at < length) { int read = stream.Read(body, at, length - at); if (read == 0) throw new IOException("Truncated request"); at += read; }
                        lock (Streaming) Streaming.Add(Encoding.UTF8.GetString(body).Contains("\"stream\":true"));
                        if (mode == "auth" || (mode == "fallback" && attempt == 0))
                        {
                            Write(stream, "HTTP/1.1 " + (mode == "auth" ? "401 Unauthorized" : "422 Unprocessable Entity") + "\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n{\"error\":{\"message\":\"stream not supported credential echo\"}}");
                        }
                        else if (mode == "plain" || mode == "fallback")
                            Write(stream, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n{\"choices\":[{\"message\":{\"content\":\"SELECT 1;\"}}]}");
                        else
                        {
                            Write(stream, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n: ping\n\n");
                            string first = mode == "anthropic"
                                ? "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"SELECT \"}}\n\n"
                                : "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"SELECT \"}}]}\n\n";
                            Write(stream, mode == "truncated" ? first.TrimEnd('\n') : first);
                            Thread.Sleep(mode == "cancel" ? 2000 : 80);
                            if (mode == "truncated" || mode == "cancel") continue;
                            string tail = mode == "anthropic"
                                ? "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"1;\"}}\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n"
                                : "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"1;\"}}]}\n\ndata: [DONE]\n\n";
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
$messages = [Collections.Generic.List[Querywright.Core.AiMessage]]::new()
$messages.Add([Querywright.Core.AiMessage]::new('user', 'fixture only'))
try {
    # Redirect only this test process's private field to a nonexistent path; never read the real saved key.
    $keyPath.SetValue($null, (Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N') + '.missing')))
    foreach ($mode in 'openai', 'anthropic', 'fallback', 'plain', 'auth', 'truncated', 'cancel') {
        $server = [AiFixture]::new($mode)
        $progress = [AiFixtureProgress]::new()
        $cancel = [Threading.CancellationTokenSource]::new()
        try {
            $format = [Enum]::Parse($assembly.GetType('Querywright.Ssms.AiApiFormat'), $(if ($mode -eq 'anthropic') { 'Anthropic' } else { 'OpenAiCompatible' }))
            $task = $ask.Invoke($null, @($format, $server.Url, 'fixture', $messages, $cancel.Token, 4096, $progress))
            if ($mode -eq 'cancel') {
                $deadline = [DateTime]::UtcNow.AddSeconds(5)
                while ($progress.Text.Length -eq 0 -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 10 }
                Check ($progress.Text -eq 'SELECT ') 'Cancellation fixture must receive a delta first'
                $clock = [Diagnostics.Stopwatch]::StartNew(); $cancel.Cancel()
                try { $task.GetAwaiter().GetResult(); throw 'Cancellation unexpectedly succeeded' }
                catch { Check $task.IsCanceled 'Stop must cancel the HTTP read'; Check ($clock.ElapsedMilliseconds -lt 1000) 'Stop must promptly cancel a stalled stream'; Check ($progress.Text -eq 'SELECT ') 'Stop must preserve received text' }
            } elseif ($mode -eq 'auth' -or $mode -eq 'truncated') {
                try { $task.GetAwaiter().GetResult(); throw 'Failure fixture unexpectedly succeeded' }
                catch {
                    Check $task.IsFaulted 'Failure fixture must fault'
                    Check (!$task.Exception.ToString().Contains('credential echo')) 'Provider errors must not be echoed'
                    if ($mode -eq 'truncated') { Check ($progress.Text -eq 'SELECT ') 'Truncation must preserve partial text' }
                }
            } else {
                Check ($task.GetAwaiter().GetResult() -eq 'SELECT 1;') "$mode answer must be complete"
                if ($mode -eq 'openai' -or $mode -eq 'anthropic') { Check ($progress.Text -eq 'SELECT 1;' -and $progress.Reports -eq 2) "$mode deltas must arrive in order" }
            }
            Check ($server.Worker.Wait(5000)) 'Fixture worker timed out'
            if ($mode -eq 'fallback') { Check ($server.Streaming.Count -eq 2 -and $server.Streaming[0] -and !$server.Streaming[1]) 'Fallback must retry once with stream:false' }
            else { Check ($server.Streaming.Count -eq 1 -and $server.Streaming[0]) 'Other fixtures must use one streaming request' }
        } finally { $cancel.Dispose(); $server.Dispose() }
    }
} finally { $keyPath.SetValue($null, $originalPath) }
Write-Host 'PASS: AI HTTP fixtures for both formats, JSON fallback, explicit rejection, auth failure, truncation and prompt Stop. No provider or database used.'

# In-process fixtures only. No provider, database, user settings, files, or native SSMS process.
param([string]$HostIde = 'C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$resolver = [ResolveEventHandler] {
    param($sender, $eventArgs)
    $name = [Reflection.AssemblyName]::new($eventArgs.Name).Name + '.dll'
    foreach ($folder in 'PublicAssemblies', 'PrivateAssemblies', '') {
        $path = Join-Path (Join-Path $HostIde $folder) $name
        if (Test-Path -LiteralPath $path) { return [Reflection.Assembly]::LoadFrom($path) }
    }
    return $null
}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
function Check($condition, $message) { if (!$condition) { throw $message } }
try {
    $bin = Join-Path $PSScriptRoot '../src/Querywright.Ssms/bin/Debug/net472'
    [void][Reflection.Assembly]::LoadFrom((Join-Path $bin 'Querywright.Core.dll'))
    $assembly = [Reflection.Assembly]::LoadFrom((Join-Path $bin 'Querywright.Ssms.dll'))
    $type = $assembly.GetType('Querywright.Ssms.Diagnostics', $true)
    $flags = [Reflection.BindingFlags]'Static,NonPublic'
    $enabled = $type.GetProperty('Enabled', $flags)
    $write = $type.GetMethod('Write', $flags)
    $export = $type.GetMethod('Export', $flags)
    $validate = $type.GetMethod('ValidateImport', $flags)
    function Log($text) { [void]$write.Invoke($null, @('fixture', 'query_result', 'success', 12L, $null, $text)) }
    function Snapshot { $export.Invoke($null, @()) }
    Check (!$enabled.GetValue($null)) 'Verbose capture must default off'
    Log 'SELECT off_capture;'
    Check ((Snapshot | ConvertFrom-Json).events.Count -eq 0) 'Off must capture nothing'
    $enabled.SetValue($null, $true)
    $fixtureKey = 'fixture-provider-key-canary'
    Log "SELECT '$fixtureKey' AS Value;`nResult text: fixture-result-canary"
    [void]$type.GetMethod('SetSecret', $flags).Invoke($null, @($fixtureKey))
    Log 'Authorization: Bearer fixture-header-key; Password=fixture-password; {"api_key":"fixture-json-key"}'
    $json = Snapshot
    Check ($json.Contains('SELECT') -and $json.Contains('fixture-result-canary')) 'Requested query and result text must be retained'
    foreach ($secret in $fixtureKey, 'fixture-header-key', 'fixture-password', 'fixture-json-key') { Check (!$json.Contains($secret)) 'Known keys and credential patterns must be redacted, including earlier entries' }
    Check ($json.Contains('[REDACTED]')) 'Redaction must remain visible'
    $roundTrip = $validate.Invoke($null, @($json))
    Check (($roundTrip | ConvertFrom-Json).events.Count -eq 2) 'Export must import into detached validated view'
    $modified = $json | ConvertFrom-Json
    $modified | Add-Member NoteProperty execute_sql 'DELETE FROM fixture;'
    $modified.events[0] | Add-Member NoteProperty unknown_secret 'fixture-unknown-canary'
    $safe = $validate.Invoke($null, @([string]($modified | ConvertTo-Json -Depth 20)))
    Check (!$safe.Contains('fixture-unknown-canary') -and !$safe.Contains('DELETE FROM')) 'Unknown imported fields must be dropped'
    Check ((Snapshot) -eq $json -and $enabled.GetValue($null)) 'Import must never mutate active logs or options'
    $modified.events[0].durationMilliseconds = -1
    try { [void]$validate.Invoke($null, @([string]($modified | ConvertTo-Json -Depth 20))); throw 'Invalid import accepted' }
    catch { Check ($_.Exception.ToString().Contains('Invalid diagnostic field')) 'Invalid imported metadata must fail validation' }
    Check ((Snapshot) -eq $json) 'Rejected import must leave active logs intact'
    $ai = [Querywright.Core.AiDiagnostics]::new()
    $entry = [Querywright.Core.AiDiagnosticEntry]::new(); $entry.Model = 'fixture'; $entry.MaxOutputTokens = 4096
    $ai.Add($entry); Check ($ai.Entries.Count -eq 0) 'AI metadata capture defaults off'
    $ai.Enabled = $true; $ai.Add($entry); $entry.Model = 'changed'
    Check ($ai.Entries[0].Model -eq 'fixture') 'AI metadata must clone entries'
    $imported = [Querywright.Core.AiDiagnostics]::new(); $imported.Import($ai.Export())
    Check ($imported.Entries.Count -eq 1 -and !$imported.Enabled) 'Import must work without enabling capture'
    1..110 | ForEach-Object { $ai.Add($entry) }; Check ($ai.Entries.Count -eq 100) 'AI metadata ring must remain bounded'
    $ai.Enabled = $false; Check ($ai.Entries.Count -eq 0) 'Turning off must clear AI metadata'
    Log ('unicode-fixture-' + ('漢' * 60000))
    $last = (Snapshot | ConvertFrom-Json).events[-1].text
    Check ($last.Contains('[truncated]') -and [Text.Encoding]::UTF8.GetByteCount($last) -le 131072) 'Large text must have explicit truncation within UTF-8 byte limit'
    1..510 | ForEach-Object { Log "ring-fixture-$_" }
    Check ((Snapshot | ConvertFrom-Json).events.Count -eq 500) 'Process event ring must remain bounded'
    1..60 | ForEach-Object { Log ('buffer-fixture-' + ('X' * 80000)) }
    $bounded = Snapshot
    Check (($bounded | ConvertFrom-Json).events.Count -lt 60 -and [Text.Encoding]::UTF8.GetByteCount($bounded) -lt 4500000) 'Serialized text buffer must evict old events by byte budget'
    $enabled.SetValue($null, $false)
    Check ((Snapshot | ConvertFrom-Json).events.Count -eq 0) 'Turning off must clear process logs'
    Write-Host 'PASS: diagnostics opt-in, query/result capture, credential redaction, detached import, rejected imports, cloned metadata, bounded rings, UTF-8 truncation, and Off clears logs.'
} finally {
    if ($enabled) { $enabled.SetValue($null, $false) }
    [AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)
}

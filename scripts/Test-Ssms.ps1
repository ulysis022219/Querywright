# End-to-end smoke test on a disposable Windows machine (CI). Drives the real SSMS 22 UI with keystrokes.
# Never run on a workstation with open SSMS sessions: it kills SSMS processes it started and sends keys to the desktop.
param(
    [Parameter(Mandatory)][string]$Package,
    [string]$Out = (Join-Path $PWD 'e2e')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing, Microsoft.VisualBasic
New-Item -ItemType Directory -Force $Out | Out-Null
$ide = 'C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE'
$ssms = Join-Path $ide 'SSMS.exe'
$shot = 0
function Snap([string]$name) {
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
    [System.Drawing.Graphics]::FromImage($bitmap).CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $script:shot++
    $bitmap.Save((Join-Path $Out ('{0:00}-{1}.png' -f $script:shot, $name)))
}
function Keys([string]$keys, [int]$wait = 800) { [System.Windows.Forms.SendKeys]::SendWait($keys); Start-Sleep -Milliseconds $wait }

if (-not (Test-Path $ssms)) {
    Write-Output 'Installing SSMS 22...'
    $boot = Join-Path $env:TEMP 'vs_SSMS.exe'
    Invoke-WebRequest 'https://aka.ms/ssms/22/release/vs_SSMS.exe' -OutFile $boot
    $p = Start-Process $boot -ArgumentList '--quiet', '--wait', '--norestart', '--nocache' -PassThru -Wait
    if (-not (Test-Path $ssms)) { throw "SSMS install failed (exit $($p.ExitCode))." }
}
Write-Output "SSMS $([Diagnostics.FileVersionInfo]::GetVersionInfo($ssms).ProductVersion)"

& (Join-Path $PSScriptRoot 'Install-Development.ps1') -SsmsDirectory $ide -Package $Package

$results = [ordered]@{}
$sql = Join-Path $Out 'snippet.sql'
[IO.File]::WriteAllText($sql, 'ssf')
$process = Start-Process $ssms -ArgumentList "`"$sql`"", '-nosplash', '/log' -PassThru
try {
    Start-Sleep 60
    Snap 'started'
    # Dismiss first-run and connection prompts; a disconnected editor is enough for Tab expansion.
    1..3 | ForEach-Object { [void][Microsoft.VisualBasic.Interaction]::AppActivate($process.Id); Keys '{ESC}' 1500 }
    Snap 'dialogs-dismissed'
    [void][Microsoft.VisualBasic.Interaction]::AppActivate($process.Id)
    Keys '^{END}'
    Keys '{TAB}' 2000
    Snap 'after-tab'
    Keys '^s' 3000
    $text = [IO.File]::ReadAllText($sql)
    $results['ssf + Tab'] = if ($text -eq 'SELECT * FROM ') { 'PASS' } else { "FAIL: file contains [$text]" }
} finally {
    Snap 'final'
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    Get-ChildItem "$env:APPDATA\Microsoft\SSMS" -Recurse -Filter ActivityLog.xml -ErrorAction SilentlyContinue |
        Copy-Item -Destination $Out
}
$log = Get-ChildItem $Out -Filter ActivityLog.xml | Select-Object -First 1
if ($log) {
    [xml]$activity = Get-Content $log.FullName
    $ours = $activity.activity.entry | Where-Object { $_.type -eq 'Error' -and ($_.description + $_.source + $_.path) -match 'Querywright|a13c1b0c' }
    $results['package load errors'] = if ($ours) { 'FAIL: ' + (($ours | ForEach-Object description) -join ' | ') } else { 'PASS' }
}
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
if ($results.Values -match '^FAIL') { exit 1 }

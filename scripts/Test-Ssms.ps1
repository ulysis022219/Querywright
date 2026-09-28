# End-to-end smoke test on a disposable Windows machine (CI). Drives the real SSMS 22 UI with keystrokes.
# Never run on a workstation with open SSMS sessions: it kills SSMS processes it started and sends keys to the desktop.
param(
    [Parameter(Mandatory)][string]$Package,
    [string]$Out = (Join-Path $PWD 'e2e')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing, Microsoft.VisualBasic, UIAutomationClient, UIAutomationTypes
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
function Windows([int]$id) {
    # Top-level window titles reveal blocking dialogs (sign-in, connect) in the CI log.
    Get-Process -Id $id -ErrorAction SilentlyContinue | ForEach-Object { $_.Refresh(); "main window: [$($_.MainWindowTitle)]" }
    [System.Windows.Automation.AutomationElement]::RootElement.FindAll('Children', [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.ProcessId -eq $id } | ForEach-Object { "window: [$($_.Current.Name)] class $($_.Current.ClassName)" }
    $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
    if ($focused) { "focus: [$($focused.Current.Name)] class $($focused.Current.ClassName) pid $($focused.Current.ProcessId)" }
}
function Thumbnail([string]$name) {
    # Low-resolution screenshot inlined in the log: CI artifacts may be unreachable for whoever reads the log.
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $full = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
    [System.Drawing.Graphics]::FromImage($full).CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $small = New-Object System.Drawing.Bitmap $full, 960, ([int](960 * $bounds.Height / $bounds.Width))
    $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object MimeType -eq 'image/jpeg'
    $quality = New-Object System.Drawing.Imaging.EncoderParameters 1
    $quality.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), 35L
    $stream = New-Object IO.MemoryStream
    $small.Save($stream, $codec, $quality)
    $b64 = [Convert]::ToBase64String($stream.ToArray())
    "THUMBNAIL-BEGIN $name"
    for ($i = 0; $i -lt $b64.Length; $i += 4000) { $b64.Substring($i, [Math]::Min(4000, $b64.Length - $i)) }
    "THUMBNAIL-END $name"
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
    Windows $process.Id
    Thumbnail 'after-tab'
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
    $activity.activity.entry | Where-Object { ($_.description + $_.source + $_.path) -match 'Querywright|a13c1b0c' } |
        ForEach-Object { "activity: $($_.type) $($_.source): $($_.description)" }
    $ours = $activity.activity.entry | Where-Object { $_.type -eq 'Error' -and ($_.description + $_.source + $_.path) -match 'Querywright|a13c1b0c' }
    $results['package load errors'] = if ($ours) { 'FAIL: ' + (($ours | ForEach-Object description) -join ' | ') } else { 'PASS' }
}
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
if ($results.Values -match '^FAIL') { exit 1 }

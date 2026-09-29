# End-to-end test on a disposable Windows machine (CI): installs the package into the real SSMS 22 and replays editor
# commands through the package's self-test. Never run on a workstation with open SSMS sessions: it kills SSMS processes.
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
function Invoke-Named([int]$id, [string]$pattern) {
    # Invokes the first control of the process whose name matches; true when one was found.
    $auto = [System.Windows.Automation.AutomationElement]
    foreach ($window in $auto::RootElement.FindAll('Children', [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($window.Current.ProcessId -ne $id) { continue }
        $match = $window.FindAll('Descendants', [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.Name -match $pattern -and $_.Current.ControlType.ProgrammaticName -match 'Button|Hyperlink' } |
            Select-Object -First 1
        if (-not $match) { continue }
        Write-Host "dismiss: [$($match.Current.Name)] in [$($window.Current.Name)]"
        try {
            $parent = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($match)
            $context = $parent.FindAll('Descendants', [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name } | Where-Object { $_ }
            Write-Host "dismiss context: [$($parent.Current.Name)] $($context -join ' | ')"
        } catch { }
        $invoke = $null
        if ($match.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$invoke)) { $invoke.Invoke() }
        else { try { $match.SetFocus(); Keys ' ' 0 } catch { Write-Host "dismiss failed: $_" } }
        Start-Sleep 3
        return $true
    }
    $false
}
function Dismiss([int]$id) {
    # First-run sign-in and connect prompts. ESC on the sign-in page asks to exit SSMS, so answer those by name.
    for ($i = 0; $i -lt 6; $i++) {
        # SSMS confirms command-line connections; answering No would leave the editor disconnected.
        $confirm = [System.Windows.Automation.AutomationElement]::RootElement.FindAll('Children', [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.ProcessId -eq $id } |
            ForEach-Object { $_.FindAll('Descendants', [System.Windows.Automation.Condition]::TrueCondition) } |
            Where-Object { $_.Current.Name -match '^Connect to the following server' } | Select-Object -First 1
        if ($confirm -and (Invoke-Named $id '^Yes$')) { continue }
        if (Invoke-Named $id '^No$') { continue }                        # "exit SQL Server Management Studio?"
        if (Invoke-Named $id '^Skip and add accounts later') { continue }
        $connect = [System.Windows.Automation.AutomationElement]::RootElement.FindAll('Children', [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.ProcessId -eq $id -and $_.Current.Name -match '^Connect' }
        if ($connect) { [void][Microsoft.VisualBasic.Interaction]::AppActivate($id); Keys '{ESC}' 2000; continue }
        # SSMS 22 hosts the connect prompt inside the main window; its server box takes focus.
        $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
        if ($focused -and $focused.Current.ProcessId -eq $id -and $focused.Current.Name -match 'Server Name') {
            Write-Host 'dismiss: connect prompt'; Keys '{ESC}' 2000; continue
        }
        break
    }
}

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
function Session([string]$name, [string]$text, [string[]]$extra, [string]$steps) {
    # One SSMS instance per scenario. The package's self-test (QUERYWRIGHT_SELFTEST) replays $steps through the SQL
    # editor's command chain, the same TYPECHAR/TAB/F12 commands a keypress sends, so window focus cannot break the run.
    $file = Join-Path $Out "$name.sql"
    $result = Join-Path $Out "$name.result.txt"
    [IO.File]::WriteAllText($file, $text)
    Remove-Item $result -ErrorAction SilentlyContinue
    $env:QUERYWRIGHT_SELFTEST = $result
    $env:QUERYWRIGHT_SELFTEST_STEPS = $steps
    Start-Process $ssms -ArgumentList (@("`"$file`"") + $extra + '-nosplash', '/log') | Out-Null
    try {
        for ($i = 0; $i -lt 48 -and -not (Test-Path $result); $i++) {
            Start-Sleep 5
            # First run relaunches SSMS, so follow the newest process; answer first-run prompts that could block the editor.
            $id = (Get-Process SSMS -ErrorAction SilentlyContinue | Sort-Object StartTime -Descending | Select-Object -First 1).Id
            if ($id -and $i % 2 -eq 1) { Dismiss $id }
        }
        Start-Sleep 2
        Snap "$name-done"
        Thumbnail $name | Write-Host
        if ($id) { Windows $id | Write-Host }
        if (Test-Path "$result.trace") { Get-Content "$result.trace" | ForEach-Object { "trace ${name}: $_" } | Write-Host }
        if (-not (Test-Path $result)) { return 'error: self-test wrote no result within 4 minutes' }
        return [IO.File]::ReadAllText($result)
    } catch {
        return "error: $_"
    } finally {
        Remove-Item Env:QUERYWRIGHT_SELFTEST, Env:QUERYWRIGHT_SELFTEST_STEPS -ErrorAction SilentlyContinue
        Get-Process SSMS -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep 3
        Get-ChildItem "$env:APPDATA\Microsoft\SSMS" -Recurse -Filter ActivityLog.xml -ErrorAction SilentlyContinue |
            Select-Object -First 1 | Copy-Item -Destination (Join-Path $Out "ActivityLog-$name.xml")
    }
}
function Expect([string]$name, [string]$actual, [scriptblock]$ok) {
    $results[$name] = if (& $ok $actual) { 'PASS' } else { "FAIL: file contains [$actual]" }
}

$text = Session 'snippet' 'ssf' @() 'wait:3000|end|tab|wait:1000'
Expect 'ssf + Tab' $text { param($t) $t -eq 'SELECT * FROM ' }

$text = Session 'definition' "DECLARE @abc int;`r`nSELECT @abc;" @() 'wait:3000|end|left:2|f12|wait:1000|type:Z'
Expect 'F12 local variable' $text { param($t) $t -eq "DECLARE Z int;`r`nSELECT @abc;" }

# Typing opens the suggestion list; Tab must still expand the snippet.
$text = Session 'typed-snippet' '' @() 'wait:3000|type:ssf|wait:2500|tab|wait:1000'
Expect 'typed ssf + Tab with popup' $text { param($t) $t -eq 'SELECT * FROM ' }

# Keywords come from the popup without metadata.
$text = Session 'keyword' '' @() 'wait:3000|type:SELECT 1 ORD|wait:2500|tab|wait:1000'
Expect 'keyword completion' $text { param($t) $t -match '^SELECT 1 ORDER' }

# Live metadata against LocalDB on the disposable runner (the only database this test writes to).
$server = '(localdb)\MSSQLLocalDB'
try {
    $localdb = (Get-Command SqlLocalDB.exe -ErrorAction SilentlyContinue).Source
    if (-not $localdb) {
        choco install sqllocaldb -y --no-progress | Out-Null
        $localdb = Get-ChildItem 'C:\Program Files\Microsoft SQL Server\*\Tools\Binn\SqlLocalDB.exe' | Select-Object -Last 1 -ExpandProperty FullName
    }
    & $localdb create MSSQLLocalDB 2>&1 | Out-Null
    & $localdb start MSSQLLocalDB | Out-Null
    $connection = New-Object System.Data.SqlClient.SqlConnection "Server=$server;Integrated Security=true;Initial Catalog=master"
    $connection.Open()
    foreach ($statement in "IF DB_ID('QwTest') IS NULL CREATE DATABASE QwTest;",
        "USE QwTest; IF OBJECT_ID('dbo.People') IS NULL CREATE TABLE dbo.People (Id int NOT NULL, FullName nvarchar(100) NULL);",
        "USE QwTest; IF NOT EXISTS (SELECT 1 FROM dbo.People) INSERT dbo.People VALUES (1, N'Ann O''Neil'), (2, NULL);") {
        $command = $connection.CreateCommand(); $command.CommandText = $statement; [void]$command.ExecuteNonQuery()
    }
    $connection.Close()
    $live = $true
} catch { $results['LocalDB setup'] = "FAIL: $($_.Exception.Message)"; $live = $false }
if ($live) {
    # The wait lets SSMS connect and the package load the catalog.
    $text = Session 'wildcard' "SELECT *`r`nFROM dbo.People;" @('-S', $server, '-d', 'QwTest', '-C') 'wait:40000|home|right:8|tab|wait:3000'
    Expect '* + Tab from live metadata' $text { param($t) $t -match 'FullName' -and $t -notmatch '\*' }
    $text = Session 'columns' "SELECT  FROM dbo.People p;" @('-S', $server, '-d', 'QwTest', '-C') 'wait:40000|home|right:7|type:p.Ful|wait:3000|tab|wait:1000'
    Expect 'column completion from live metadata' $text { param($t) $t -match 'SELECT p\.FullName ?FROM' }
    $text = Session 'insert-fill' "INSERT INTO dbo.People" @('-S', $server, '-d', 'QwTest', '-C') 'wait:40000|end|tab|wait:3000'
    Expect 'INSERT + Tab fills columns from live metadata' $text { param($t) $t -match 'Id' -and $t -match 'FullName' -and $t -match 'VALUES' }
    # Results grid: run a read-only SELECT, focus the grid, then "Script as INSERT" (0x118) opens a new window.
    $text = Session 'grid-insert' "SELECT Id, FullName FROM dbo.People ORDER BY Id;" @('-S', $server, '-d', 'QwTest', '-C') 'wait:30000|exec|wait:10000|grid|cmd:118|wait:5000|latest'
    Expect 'results grid script as INSERT' $text { param($t) $t -match 'CREATE TABLE #Results' -and $t -match "\(1, N'Ann O''Neil'\)" -and $t -match '\(2, NULL\)' }
}

foreach ($log in Get-ChildItem $Out -Filter 'ActivityLog-*.xml') {
    [xml]$activity = Get-Content $log.FullName
    $activity.activity.entry | Where-Object { ($_.description + $_.source + $_.path) -match 'Querywright|a13c1b0c' } |
        ForEach-Object { "$($log.BaseName): $($_.type) $($_.source): $($_.description)" }
    $ours = $activity.activity.entry | Where-Object { $_.type -eq 'Error' -and ($_.description + $_.source + $_.path) -match 'Querywright|a13c1b0c' }
    $results["package errors ($($log.BaseName))"] = if ($ours) { 'FAIL: ' + (($ours | ForEach-Object description) -join ' | ') } else { 'PASS' }
}
$results.GetEnumerator() | ForEach-Object { Write-Output ("{0}: {1}" -f $_.Key, $_.Value) }
if ($results.Values -match '^FAIL') { exit 1 }

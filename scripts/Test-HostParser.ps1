# Run with Windows PowerShell so the runtime matches the SSMS .NET Framework host.
param([string]$SsmsDirectory = 'C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE')
$ErrorActionPreference = 'Stop'
$parser = Join-Path $SsmsDirectory 'Extensions\Application\Microsoft.SqlServer.TransactSql.ScriptDom.dll'
$core = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Querywright.Ssms\bin\Debug\net472\Querywright.Core.dll'
[void][Reflection.Assembly]::LoadFrom($parser)
[void][Reflection.Assembly]::LoadFrom($core)
$cancel = [Threading.CancellationToken]::None
$samples = @(
    "-- keep`r`nselect @sample as sample_value, N'日本語' as label;`r`nGO 3`r`nselect 2;`r`nGO`r`n",
    'declare @sample int=1; select @sample as sample_value,db_name() as database_name;'
)
foreach ($sql in $samples) {
    $formatted = [Querywright.Core.SqlFormatting]::Format($sql, $null, $cancel)
    if ([string]::IsNullOrWhiteSpace($formatted)) { throw 'Host parser returned empty SQL.' }
    if ($formatted -cne [Querywright.Core.SqlFormatting]::Format($formatted, $null, $cancel)) { throw 'Host formatting is not idempotent.' }
    if ($sql.Contains('GO 3') -and (-not $formatted.Contains('GO 3') -or -not $formatted.Contains('-- keep'))) { throw 'Host formatter lost batch repetition or comment.' }
}
Write-Output "PASS: formatting, protected tokens and idempotence using SSMS ScriptDOM $([Diagnostics.FileVersionInfo]::GetVersionInfo($parser).FileVersion)."

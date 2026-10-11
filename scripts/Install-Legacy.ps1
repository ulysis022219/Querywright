param(
    # Common7\IDE of SSMS 18, 19 or 20. Default: the newest one found.
    [string]$SsmsDirectory,
    [string]$Build,
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Build) { $Build = Join-Path $root 'src\Querywright.Ssms.Legacy\bin\Debug\net472' }
if (-not $SsmsDirectory) {
    $SsmsDirectory = 20, 19, 18 | ForEach-Object { Join-Path ${env:ProgramFiles(x86)} "Microsoft SQL Server Management Studio $_\Common7\IDE" } |
        Where-Object { Test-Path -LiteralPath (Join-Path $_ 'Ssms.exe') } | Select-Object -First 1
    if (-not $SsmsDirectory) { throw 'SSMS 18, 19 or 20 not found. Pass -SsmsDirectory.' }
}
$ssms = Join-Path $SsmsDirectory 'Ssms.exe'
if (-not (Test-Path -LiteralPath $ssms)) { throw "Ssms.exe not found in $SsmsDirectory." }
$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run from an elevated prompt: the SSMS folder is under Program Files.' }
if (Get-Process ssms -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $ssms }) { throw 'Close SSMS after saving your work first. No processes were stopped.' }
$extensions = Join-Path $SsmsDirectory 'Extensions'
$target = Join-Path $extensions 'Querywright'
if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
if (-not $Uninstall) {
    foreach ($file in 'Querywright.Ssms.dll', 'Querywright.Ssms.pkgdef', 'extension.vsixmanifest') {
        if (-not (Test-Path -LiteralPath (Join-Path $Build $file))) { throw "Build src\Querywright.Ssms.Legacy first: $file is missing." }
    }
    Copy-Item -LiteralPath $Build -Destination $target -Recurse
    Get-ChildItem -LiteralPath $target -Filter *.pdb | Remove-Item -Force
}
# The shell rebuilds its extension and MEF caches when this marker changes.
Set-Content -LiteralPath (Join-Path $extensions 'extensions.configurationchanged') -Value ''
Write-Output "$(if ($Uninstall) { 'Removed from' } else { 'Installed to' }) $target. Start SSMS."

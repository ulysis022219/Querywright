param(
    [string]$SsmsDirectory = 'C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE',
    [string]$Package,
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
# Release zip: the VSIX sits next to this script. Source tree: the build output.
if (-not $Package) {
    $Package = Join-Path $PSScriptRoot 'Querywright.Ssms.vsix'
    if (-not (Test-Path -LiteralPath $Package)) { $Package = Join-Path $root 'src\Querywright.Ssms\bin\Debug\net472\Querywright.Ssms.vsix' }
}
$package = $Package
$installer = Join-Path $SsmsDirectory 'VSIXInstaller.exe'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio installer discovery tool not found.' }
$instances = & $vswhere -products Microsoft.VisualStudio.Product.Ssms -format json | ConvertFrom-Json
$instance = @($instances | Where-Object { $_.productPath -eq (Join-Path $SsmsDirectory 'SSMS.exe') -and $_.isComplete })
if ($instance.Count -ne 1) { throw 'Expected one complete SSMS instance matching the requested directory.' }
if (-not (Test-Path -LiteralPath $installer)) { throw 'SSMS VSIX installer not found.' }
if (-not $Uninstall -and -not (Test-Path -LiteralPath $package)) { throw 'Build the VSIX first.' }
$running = Get-Process ssms -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $SsmsDirectory 'SSMS.exe') }
if ($running) { throw 'Close SSMS 22 after saving your work before installation. No processes were stopped.' }
if (-not $Uninstall) { & "$PSScriptRoot\Test-Package.ps1" -Path $package }
$log = Join-Path $env:TEMP 'Querywright-vsix-install.log'
$target = if ($Uninstall) { '/uninstall:SqlWorkbench.a13c1b0c-af94-4f53-8d06-edf816e39450' } else { '"' + $package + '"' }
$arguments = @('/quiet', ('/instanceIds:' + $instance[0].instanceId), ('/logFile:"' + $log + '"'), $target)
$result = Start-Process -FilePath $installer -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden
Write-Output "VSIX installer exit code: $($result.ExitCode)."
if (Test-Path -LiteralPath $log) { Write-Output "Log: $log" }
else { Write-Output 'Bootstrap installer did not create the requested log. Check %TEMP%\dd_VSIXInstaller_*.log.' }
# 1001: this exact version is already installed (installing a newer build upgrades in place).
if ($result.ExitCode -eq 1001) { Write-Output 'This version is already installed; nothing to update.'; exit 0 }
if ($result.ExitCode -ne 0) { throw "VSIX $(if ($Uninstall) { 'uninstall' } else { 'installation' }) failed with exit code $($result.ExitCode)." }

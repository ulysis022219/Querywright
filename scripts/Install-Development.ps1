param([string]$SsmsDirectory = 'C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$package = Join-Path $root 'src\SqlWorkbench.Ssms\bin\Debug\net472\SqlWorkbench.Ssms.vsix'
$installer = Join-Path $SsmsDirectory 'VSIXInstaller.exe'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio installer discovery tool not found.' }
$instances = & $vswhere -products Microsoft.VisualStudio.Product.Ssms -format json | ConvertFrom-Json
$instance = @($instances | Where-Object { $_.productPath -eq (Join-Path $SsmsDirectory 'SSMS.exe') -and $_.isComplete })
if ($instance.Count -ne 1) { throw 'Expected one complete SSMS instance matching the requested directory.' }
if (-not (Test-Path -LiteralPath $installer)) { throw 'SSMS VSIX installer not found.' }
if (-not (Test-Path -LiteralPath $package)) { throw 'Build the VSIX first.' }
$running = Get-Process ssms -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $SsmsDirectory 'SSMS.exe') }
if ($running) { throw 'Close SSMS 22 after saving your work before installation. No processes were stopped.' }
& "$PSScriptRoot\Test-Package.ps1" -Path $package
$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$log = Join-Path $artifacts 'vsix-install.log'
$arguments = @('/quiet', ('/instanceIds:' + $instance[0].instanceId), ('/logFile:"' + $log + '"'), ('"' + $package + '"'))
$result = Start-Process -FilePath $installer -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden
Write-Output "VSIX installer exit code: $($result.ExitCode)."
if (Test-Path -LiteralPath $log) { Write-Output "Log: $log" }
else { Write-Output 'Bootstrap installer did not create the requested log. Check %TEMP%\dd_VSIXInstaller_*.log.' }
if ($result.ExitCode -ne 0) { throw "VSIX installation failed with exit code $($result.ExitCode)." }

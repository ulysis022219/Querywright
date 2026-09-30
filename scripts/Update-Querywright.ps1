param(
    [Parameter(Mandatory = $true)][ValidatePattern('\Av[0-9]+\.[0-9]+\.[0-9]+\z')][string]$Tag,
    [Parameter(Mandatory = $true)][string]$SsmsDirectory
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Update-Release.ps1"
$mutex = [Threading.Mutex]::new($false, 'Local\Querywright.ReleaseUpdate')
$owned = $false
$result = 1
try {
    try { $owned = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $owned = $true }
    if (-not $owned) { throw 'Another Querywright updater is already running. Use its window.' }
    $ssms = Join-Path $SsmsDirectory 'SSMS.exe'
    if (-not (Test-Path -LiteralPath $ssms) -or -not (Test-Path -LiteralPath (Join-Path $SsmsDirectory 'VSIXInstaller.exe'))) {
        throw 'The originating SSMS installation could not be found.'
    }
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Write-Host "Downloading Querywright $Tag... You can keep working until the download is verified."
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/ulysis022219/SqlWorkbench/releases/tags/$Tag" `
        -Headers @{ 'User-Agent' = 'Querywright-update'; 'Accept' = 'application/vnd.github+json' } -TimeoutSec 30
    $asset = Get-QuerywrightReleaseAsset -Release $release -Tag $Tag
    $archive = Join-Path $PSScriptRoot 'release.zip'
    $package = Join-Path $PSScriptRoot 'Querywright.Ssms.vsix'
    Save-QuerywrightArchive -Uri $asset.browser_download_url -Destination $archive
    Expand-QuerywrightUpdate -Archive $archive -Destination $package -Digest $asset.digest -Tag $Tag
    Write-Host 'Download verified. Save your work and close all windows of this SSMS installation.'
    Write-Host 'This updater will wait without closing SSMS or discarding any work. Close this window to cancel.'
    # Wait for every process from the originating installation, including additional SSMS windows.
    while (@(Get-Process ssms -ErrorAction SilentlyContinue | Where-Object { -not $_.Path -or $_.Path -ieq $ssms }).Count -gt 0) {
        Start-Sleep -Seconds 2
    }
    Write-Host 'Installing update... Do not reopen SSMS until installation finishes.'
    $powershell = Join-Path $PSHOME 'powershell.exe'
    & $powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot/Install-Development.ps1" -SsmsDirectory $SsmsDirectory -Package $package
    if ($LASTEXITCODE -ne 0) { throw "Installer failed (exit code $LASTEXITCODE). See the installer log reported above." }
    Remove-Item -LiteralPath $archive, $package -Force
    Write-Host "Querywright $Tag is installed. You can now reopen SSMS. Your settings are preserved."
    $result = 0
} catch {
    Write-Host "Update failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "You can retry, or use the manual installer at https://github.com/ulysis022219/SqlWorkbench/releases/tag/$Tag"
} finally {
    if ($owned) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
Read-Host 'Press Enter to close'
exit $result

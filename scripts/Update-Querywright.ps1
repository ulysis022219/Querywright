param(
    [Parameter(Mandatory = $true)][ValidatePattern('\Av[0-9]+\.[0-9]+\.[0-9]+\z')][string]$Tag,
    [Parameter(Mandatory = $true)][string]$SsmsDirectory
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Update-Release.ps1"
$Host.UI.RawUI.WindowTitle = "Querywright updater - $Tag"
function Step([string]$Number, [string]$Text) { Write-Host; Write-Host "[$Number/4] $Text" -ForegroundColor Cyan }
function Note([string]$Text) { Write-Host "      $Text" -ForegroundColor Gray }
function Ok([string]$Text) { Write-Host "      OK  $Text" -ForegroundColor Green }
Write-Host
Write-Host '  ============================================' -ForegroundColor DarkCyan
Write-Host "   Querywright updater  $Tag" -ForegroundColor White
Write-Host '  ============================================' -ForegroundColor DarkCyan
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
    Step 1 "Downloading Querywright $Tag"
    Note 'You can keep working in SSMS while this runs.'
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/ulysis022219/Querywright/releases/tags/$Tag" `
        -Headers @{ 'User-Agent' = 'Querywright-update'; 'Accept' = 'application/vnd.github+json' } -TimeoutSec 30
    $asset = Get-QuerywrightReleaseAsset -Release $release -Tag $Tag
    $archive = Join-Path $PSScriptRoot 'release.zip'
    $package = Join-Path $PSScriptRoot 'Querywright.Ssms.vsix'
    Save-QuerywrightArchive -Uri $asset.browser_download_url -Destination $archive
    Ok 'Downloaded.'
    Step 2 'Verifying package'
    Expand-QuerywrightUpdate -Archive $archive -Destination $package -Digest $asset.digest -Tag $Tag
    Ok 'SHA-256 checksum and package contents verified.'
    Step 3 'Waiting for SSMS to close'
    Note 'Save your work and close every window of this SSMS installation.'
    Note 'Nothing is closed for you. Close this window to cancel the update.'
    # Wait for every process from the originating installation, including additional SSMS windows.
    while (@(Get-Process ssms -ErrorAction SilentlyContinue | Where-Object { -not $_.Path -or $_.Path -ieq $ssms }).Count -gt 0) {
        Start-Sleep -Seconds 2
    }
    Ok 'SSMS is closed.'
    Step 4 'Installing'
    Note 'Do not reopen SSMS until this finishes.'
    $powershell = Join-Path $PSHOME 'powershell.exe'
    & $powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot/Install-Development.ps1" -SsmsDirectory $SsmsDirectory -Package $package | ForEach-Object { Note $_ }
    if ($LASTEXITCODE -ne 0) { throw "Installer failed (exit code $LASTEXITCODE). See the installer log reported above." }
    Remove-Item -LiteralPath $archive, $package -Force
    Write-Host
    Write-Host "  SUCCESS  Querywright $Tag is installed." -ForegroundColor Green
    Write-Host '           Reopen SSMS to use it. Your settings are kept.'
    $result = 0
} catch {
    Write-Host
    Write-Host "  FAILED   $($_.Exception.Message)" -ForegroundColor Red
    Write-Host '           Nothing was changed unless the installer reported otherwise above.'
    Write-Host "           Retry, or install manually: https://github.com/ulysis022219/Querywright/releases/tag/$Tag"
} finally {
    if ($owned) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
Write-Host
Read-Host 'Press Enter to close'
exit $result

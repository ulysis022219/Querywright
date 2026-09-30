$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Update-Release.ps1"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$script:checks = 0
function Reject([scriptblock]$Action, [string]$Expected) {
    $caught = $null
    try { & $Action } catch { $caught = $_.Exception.Message }
    if (-not $caught -or $caught -notlike "*$Expected*") { throw "Expected '$Expected', got '$caught'." }
    $script:checks++
}
function New-Release {
    return [pscustomobject]@{
        tag_name = 'v1.2.3'; draft = $false; prerelease = $false
        assets = @([pscustomobject]@{
            name = 'Querywright-ssms22.zip'; state = 'uploaded'; digest = 'sha256:' + ('a' * 64)
            browser_download_url = 'https://github.com/ulysis022219/SqlWorkbench/releases/download/v1.2.3/Querywright-ssms22.zip'
        })
    }
}
$release = New-Release
$asset = Get-QuerywrightReleaseAsset $release 'v1.2.3'
if ($asset.name -ne 'Querywright-ssms22.zip') { throw 'Valid release was not selected.' }
$script:checks++
foreach ($tag in @('v1.2.3/other', 'v1.2.3-beta', 'v1.2', 'v1.2.4', 'V1.2.3', "v1.2.3`n", 'v1.2.3";whoami')) {
    Reject { Get-QuerywrightReleaseAsset $release $tag } 'published stable'
}
$release.draft = $true
Reject { Get-QuerywrightReleaseAsset $release 'v1.2.3' } 'published stable'
$release = New-Release
$release.prerelease = $true
Reject { Get-QuerywrightReleaseAsset $release 'v1.2.3' } 'published stable'
$release = New-Release
$release.assets = @()
Reject { Get-QuerywrightReleaseAsset $release 'v1.2.3' } 'exactly one'
$release = New-Release
$release.assets += $release.assets[0]
Reject { Get-QuerywrightReleaseAsset $release 'v1.2.3' } 'exactly one'
$release = New-Release
$release.assets[0].browser_download_url = 'https://example.com/Querywright-ssms22.zip'
Reject { Get-QuerywrightReleaseAsset $release 'v1.2.3' } 'download location'
$release = New-Release
$release.assets[0].digest = $null
Reject { Get-QuerywrightReleaseAsset $release 'v1.2.3' } 'no SHA-256'
foreach ($uri in @('http://github.com/file', 'https://github.com.attacker.example/file',
    'https://github.com@attacker.example/file', 'https://github.com:8443/file',
    'file:///tmp/release.zip', 'https://release-assets.githubusercontent.com.attacker.example/file')) {
    Reject { Assert-QuerywrightDownloadUri ([uri]$uri) } 'Untrusted'
}
Assert-QuerywrightDownloadUri ([uri]'https://release-assets.githubusercontent.com/example?signature=value')
$script:checks++
$inputStream = [IO.MemoryStream]::new([byte[]](1, 2, 3, 4))
$outputStream = [IO.MemoryStream]::new()
try {
    Reject { Copy-QuerywrightDownload $inputStream $outputStream ([Threading.CancellationToken]::None) 3 } 'size limit'
    if ($outputStream.Length -ne 0) { throw 'Oversized data was written.' }
    $inputStream.Position = 0
    Copy-QuerywrightDownload $inputStream $outputStream ([Threading.CancellationToken]::None) 4
    if ($outputStream.Length -ne 4) { throw 'Valid download was truncated.' }
    $script:checks++
} finally { $inputStream.Dispose(); $outputStream.Dispose() }

$temp = Join-Path ([IO.Path]::GetTempPath()) ('Querywright-update-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
function New-TestZip([string]$Path, $Entries) {
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $Entries.Keys) {
            $writer = [IO.StreamWriter]::new($zip.CreateEntry($name).Open())
            try { $writer.Write([string]$Entries[$name]) } finally { $writer.Dispose() }
        }
    } finally { $zip.Dispose() }
}
try {
    $vsix = Join-Path $temp 'fixture.vsix'
    $manifest = [IO.File]::ReadAllText("$PSScriptRoot/../src/Querywright.Ssms/source.extension.vsixmanifest").Replace('Version="1.0.0"', 'Version="1.2.3.45"')
    $entries = @{ 'extension.vsixmanifest' = $manifest }
    foreach ($name in @('Querywright.Ssms.dll', 'Querywright.Ssms.pkgdef', 'Querywright.Core.dll',
        'Microsoft.SqlServer.TransactSql.ScriptDom.dll', 'LICENSE', 'THIRD-PARTY-LICENSES/ScriptDOM.txt',
        'Updater/Update-Querywright.ps1', 'Updater/Update-Release.ps1', 'Updater/Install-Development.ps1', 'Updater/Test-Package.ps1')) {
        $entries[$name] = 'fixture'
    }
    New-TestZip $vsix $entries
    $archive = Join-Path $temp 'release.zip'
    $zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
    try {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $vsix, 'Querywright.Ssms.vsix') | Out-Null
        $zip.CreateEntry('../must-not-extract.ps1') | Out-Null
    } finally { $zip.Dispose() }
    $digest = 'sha256:' + (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    $destination = Join-Path $temp 'verified.vsix'
    Expand-QuerywrightUpdate $archive $destination $digest 'v1.2.3'
    if (-not (Test-Path -LiteralPath $destination) -or (Test-Path -LiteralPath (Join-Path (Split-Path $temp) 'must-not-extract.ps1'))) {
        throw 'Extraction failed or extracted an unexpected entry.'
    }
    $script:checks++
    Reject { Expand-QuerywrightUpdate $archive (Join-Path $temp 'bad.vsix') ('sha256:' + ('0' * 64)) 'v1.2.3' } 'checksum'
    Reject { Expand-QuerywrightUpdate $archive (Join-Path $temp 'wrong-version.vsix') $digest 'v1.2.4' } 'version does not match'
    $entries['extension.vsixmanifest'] = $manifest.Replace('SqlWorkbench.a13c1b0c-af94-4f53-8d06-edf816e39450', 'Other.Extension')
    $wrong = Join-Path $temp 'wrong-identity.vsix'
    New-TestZip $wrong $entries
    Reject { & "$PSScriptRoot/Test-Package.ps1" -Path $wrong } 'extension identity'
    $entries['extension.vsixmanifest'] = $manifest
    $entries['../escape.dll'] = 'bad'
    $unsafe = Join-Path $temp 'unsafe.vsix'
    New-TestZip $unsafe $entries
    Reject { & "$PSScriptRoot/Test-Package.ps1" -Path $unsafe } 'Unsafe'
    $entries.Remove('../escape.dll')
    foreach ($path in @('folder/../escape.dll', 'folder/.. /escape.dll', 'folder/file.dll:stream', 'folder/NUL.dll', './extension.vsixmanifest')) {
        $entries[$path] = 'bad'
        $unsafePath = Join-Path $temp (([guid]::NewGuid().ToString('N')) + '.vsix')
        New-TestZip $unsafePath $entries
        Reject { & "$PSScriptRoot/Test-Package.ps1" -Path $unsafePath } 'Unsafe'
        $entries.Remove($path)
    }
    $duplicate = Join-Path $temp 'duplicate.vsix'
    New-TestZip $duplicate $entries
    $zip = [IO.Compression.ZipFile]::Open($duplicate, [IO.Compression.ZipArchiveMode]::Update)
    try { $zip.CreateEntry('EXTENSION.VSIXMANIFEST') | Out-Null } finally { $zip.Dispose() }
    Reject { & "$PSScriptRoot/Test-Package.ps1" -Path $duplicate } 'duplicate'
    $entries['extension.vsixmanifest'] = $manifest.Replace('<PackageManifest ', '<!DOCTYPE PackageManifest [<!ENTITY external SYSTEM "file:///etc/passwd">]><PackageManifest ')
    $dtd = Join-Path $temp 'dtd.vsix'
    New-TestZip $dtd $entries
    Reject { & "$PSScriptRoot/Test-Package.ps1" -Path $dtd } 'DTD'
    $missing = Join-Path $temp 'missing.zip'
    New-TestZip $missing @{ 'other.vsix' = 'not the release package' }
    $missingDigest = 'sha256:' + (Get-FileHash -LiteralPath $missing -Algorithm SHA256).Hash
    Reject { Expand-QuerywrightUpdate $missing (Join-Path $temp 'missing.vsix') $missingDigest 'v1.2.3' } 'Invalid release package'
} finally { Remove-Item -LiteralPath $temp -Recurse -Force }
Write-Output "PASS: $script:checks update validation checks. No network requests or installation."

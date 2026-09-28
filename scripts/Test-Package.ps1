param([string]$Path = "$PSScriptRoot/../src/SqlWorkbench.Ssms/bin/Debug/net472/SqlWorkbench.Ssms.vsix")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Path))
try {
    $names = @($archive.Entries.FullName)
    foreach ($required in @('extension.vsixmanifest', 'SqlWorkbench.Ssms.dll', 'SqlWorkbench.Ssms.pkgdef',
        'SqlWorkbench.Core.dll', 'Microsoft.SqlServer.TransactSql.ScriptDom.dll', 'LICENSE', 'THIRD-PARTY-LICENSES/ScriptDOM.txt')) {
        if ($names -notcontains $required) { throw "Package missing $required" }
    }
    if ($names -match '(^|/)Microsoft\.VisualStudio\..*\.dll$') { throw 'Host SDK assemblies must not be bundled.' }
    $reader = [IO.StreamReader]::new($archive.GetEntry('extension.vsixmanifest').Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $target = $manifest.PackageManifest.Installation.InstallationTarget
    if ($target.Id -ne 'Microsoft.VisualStudio.Ssms' -or $target.Version -ne '[22.0,23.0)' -or $target.ProductArchitecture -ne 'amd64') {
        throw 'Unexpected SSMS installation target.'
    }
    Write-Output 'PASS: VSIX target, required binaries, notices, and SDK exclusion. Runtime not tested.'
} finally { $archive.Dispose() }

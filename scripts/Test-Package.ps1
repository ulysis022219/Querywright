param(
    [string]$Path = "$PSScriptRoot/../src/Querywright.Ssms/bin/Debug/net472/Querywright.Ssms.vsix",
    [string]$ExpectedVersion
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Path))
try {
    $names = @($archive.Entries.FullName)
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [long]$expandedSize = 0
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        if ($name.StartsWith('/') -or $name.Contains(':') -or $name -match '(^|/)\.\.(/|$)' -or -not $seen.Add($name)) {
            throw 'Unsafe or duplicate package entry.'
        }
        foreach ($segment in $name.TrimEnd('/').Split('/')) {
            if (-not $segment -or $segment -in @('.', '..') -or $segment -match '[\x00-\x1f<>:"|?*]' -or
                $segment.TrimEnd(' ', '.') -cne $segment -or $segment -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)') {
                throw 'Unsafe Windows package path.'
            }
        }
        $expandedSize += $entry.Length
        if ($expandedSize -gt 500MB) { throw 'Package expanded size exceeds the limit.' }
    }
    foreach ($required in @('extension.vsixmanifest', 'Querywright.Ssms.dll', 'Querywright.Ssms.pkgdef',
        'Querywright.Core.dll', 'Microsoft.SqlServer.TransactSql.ScriptDom.dll', 'LICENSE', 'THIRD-PARTY-LICENSES/ScriptDOM.txt',
        'Updater/Update-Querywright.ps1', 'Updater/Update-Release.ps1', 'Updater/Install-Development.ps1', 'Updater/Test-Package.ps1')) {
        if ($names -notcontains $required) { throw "Package missing $required" }
    }
    if ($names -match '(^|/)Microsoft\.VisualStudio\..*\.dll$') { throw 'Host SDK assemblies must not be bundled.' }
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 1MB
    $stream = $archive.GetEntry('extension.vsixmanifest').Open()
    try {
        $reader = [Xml.XmlReader]::Create($stream, $settings)
        try {
            $manifest = [Xml.XmlDocument]::new()
            $manifest.XmlResolver = $null
            $manifest.Load($reader)
        } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
    if ($manifest.PackageManifest.Metadata.Identity.Id -cne 'SqlWorkbench.a13c1b0c-af94-4f53-8d06-edf816e39450') {
        throw 'Unexpected extension identity.'
    }
    if ($ExpectedVersion) {
        $version = [version]$manifest.PackageManifest.Metadata.Identity.Version
        if (('{0}.{1}.{2}' -f $version.Major, $version.Minor, $version.Build) -cne $ExpectedVersion) {
            throw 'Package version does not match the requested release.'
        }
    }
    $target = $manifest.PackageManifest.Installation.InstallationTarget
    if ($target.Id -ne 'Microsoft.VisualStudio.Ssms' -or $target.Version -ne '[22.0,23.0)' -or $target.ProductArchitecture -ne 'amd64') {
        throw 'Unexpected SSMS installation target.'
    }
    Write-Output 'PASS: VSIX target, required binaries, notices, and SDK exclusion. Runtime not tested.'
} finally { $archive.Dispose() }

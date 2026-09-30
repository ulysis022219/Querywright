# Shared validation for direct updates. No downloads, process launches or installation on import.
function Get-QuerywrightReleaseAsset {
    param($Release, [string]$Tag)
    if ($Tag -cnotmatch '\Av[0-9]+\.[0-9]+\.[0-9]+\z' -or
        $Release.tag_name -cne $Tag -or $Release.draft -ne $false -or $Release.prerelease -ne $false) {
        throw 'Expected a published stable Querywright release matching the requested version.'
    }
    $name = "QueryWright_$Tag.zip"
    $assets = @($Release.assets | Where-Object { $_.name -ceq $name })
    if ($assets.Count -ne 1) { throw "The release must contain exactly one $name asset." }
    $asset = $assets[0]
    # The repository was renamed from SqlWorkbench; GitHub may report either name.
    $expected = @('Querywright', 'SqlWorkbench') | ForEach-Object { "https://github.com/ulysis022219/$_/releases/download/$Tag/$name" }
    if ($asset.browser_download_url -cnotin $expected -or $asset.state -ne 'uploaded') {
        throw 'Unexpected release download location or incomplete asset.'
    }
    if ($asset.digest -cnotmatch '\Asha256:[a-fA-F0-9]{64}\z') {
        throw 'This release has no SHA-256 digest. Use the manual installer from the release page.'
    }
    return $asset
}

function Assert-QuerywrightDownloadUri {
    param([uri]$Uri)
    if (-not $Uri.IsAbsoluteUri -or $Uri.Scheme -cne 'https' -or $Uri.Port -ne 443 -or
        $Uri.UserInfo -or $Uri.Fragment -or $Uri.DnsSafeHost -notin @('github.com', 'release-assets.githubusercontent.com')) {
        throw 'Untrusted update download redirect.'
    }
}

function Copy-QuerywrightDownload {
    param([IO.Stream]$InputStream, [IO.Stream]$OutputStream,
        [Threading.CancellationToken]$CancellationToken, [long]$MaxBytes = 100MB)
    $buffer = New-Object byte[] 81920
    [long]$total = 0
    while (($count = $InputStream.ReadAsync($buffer, 0, $buffer.Length, $CancellationToken).GetAwaiter().GetResult()) -gt 0) {
        $total += $count
        if ($total -gt $MaxBytes) { throw 'Update download exceeds the size limit.' }
        $OutputStream.Write($buffer, 0, $count)
    }
}

function Save-QuerywrightArchive {
    param([uri]$Uri, [string]$Destination)
    Add-Type -AssemblyName System.Net.Http
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('Querywright-update')
    $timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(5))
    try {
        for ($redirects = 0; $redirects -le 5; $redirects++) {
            Assert-QuerywrightDownloadUri $Uri
            $response = $client.GetAsync($Uri, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $timeout.Token).GetAwaiter().GetResult()
            try {
                if ([int]$response.StatusCode -in @(301, 302, 303, 307, 308)) {
                    if (-not $response.Headers.Location) { throw 'Missing download redirect location.' }
                    $Uri = [uri]::new($Uri, $response.Headers.Location)
                    continue
                }
                $response.EnsureSuccessStatusCode() | Out-Null
                if ($response.Content.Headers.ContentLength -gt 100MB) { throw 'Update download exceeds the size limit.' }
                $inputStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                try {
                    $outputStream = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                    try { Copy-QuerywrightDownload $inputStream $outputStream $timeout.Token }
                    finally { $outputStream.Dispose() }
                } finally { $inputStream.Dispose() }
                return
            } finally { $response.Dispose() }
        }
        throw 'Too many update download redirects.'
    } finally { $timeout.Dispose(); $client.Dispose() }
}

function Expand-QuerywrightUpdate {
    param([string]$Archive, [string]$Destination, [string]$Digest, [string]$Tag)
    if ($Digest -cnotmatch '\Asha256:[a-fA-F0-9]{64}\z' -or
        (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash -ine $Digest.Substring(7)) {
        throw 'Release checksum verification failed. Nothing was installed.'
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        # Only extract the known VSIX. Never run scripts from the downloaded archive.
        $entries = @($zip.Entries | Where-Object { $_.FullName -ceq 'Querywright.Ssms.vsix' })
        if ($entries.Count -ne 1 -or $entries[0].Length -gt 200MB) { throw 'Invalid release package.' }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], $Destination, $false)
    } finally { $zip.Dispose() }
    & "$PSScriptRoot/Test-Package.ps1" -Path $Destination -ExpectedVersion $Tag.Substring(1) | Out-Null
}

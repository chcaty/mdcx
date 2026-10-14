#Requires -Version 7.2
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'portable-layout.ps1')
$root = Join-Path ([IO.Path]::GetTempPath()) ('mdcx-portable-layout-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$script:passed = 0
function Check([string] $Label, [scriptblock] $Action, [switch] $Reject) {
    $failed = $false
    try { & $Action } catch { if (-not $Reject) { throw }; $failed = $true }
    if ($Reject -and -not $failed) { throw "Expected rejection: $Label" }
    $script:passed++
    Write-Host "PASS: $Label"
}
function New-Fixture([string] $Name) {
    $directory = Join-Path $root $Name
    foreach ($path in @('Organizer.Desktop.exe', 'Organizer.Desktop.dll', 'Organizer.Desktop.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll',
        'python/python.exe', 'python/python313.zip', 'python/python313._pth', 'worker/main.py', 'worker/mdcx_worker/protocol.py',
        'python/Lib/site-packages/httpx/__init__.py', 'python/Lib/site-packages/certifi/cacert.pem', 'licenses/python/inventory.json',
        'licenses/python-runtime.json', 'README-WINDOWS.txt', 'source-settings.example.json', 'diagnostics/verify_bundled_http.py')) {
        $file = Join-Path $directory $path
        New-Item -ItemType Directory -Path (Split-Path $file) -Force | Out-Null
        Set-Content -LiteralPath $file -Value "synthetic packaging fixture: $path" -Encoding utf8NoBOM
    }
    '{"formatVersion":1}' | Set-Content -LiteralPath (Join-Path $directory 'portable.json') -Encoding utf8NoBOM
    Write-FixtureManifest $directory
    return $directory
}
function Write-FixtureManifest([string] $Directory) {
    [ordered]@{
        schemaVersion = 2; packageKind = 'portable'; dataMode = 'portable'; runtime = 'win-x64'; selfContained = $true
        defaultNetworkEnabled = $false; liveSourceVerified = $false; innoSetup = $null
        files = @(Get-ChildItem -LiteralPath $Directory -Recurse -File | Where-Object Name -ne 'package-manifest.json' | ForEach-Object {
            [ordered]@{ path = [IO.Path]::GetRelativePath($Directory, $_.FullName).Replace('\', '/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
        })
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Directory 'package-manifest.json') -Encoding utf8NoBOM
}
function Add-ZipEntry([string] $Archive, [string] $Name, [string] $Content = 'unexpected', [int] $Attributes = 0) {
    $zip = [IO.Compression.ZipFile]::Open($Archive, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $zip.CreateEntry($Name)
        $entry.ExternalAttributes = $Attributes
        $writer = [IO.StreamWriter]::new($entry.Open())
        try { $writer.Write($Content) } finally { $writer.Dispose() }
    } finally { $zip.Dispose() }
}
try {
    foreach ($name in @('../escape', '/absolute', 'C:/absolute', 'one\two', 'one//two', 'one/./two', 'one/../two', 'one/two.', 'one/two ', 'NUL.txt', 'one/CON', 'bad<name', 'bad>name', 'bad|name', 'bad?name', 'bad*name', 'bad"name',
        'data/index.db', 'DATA/settings', 'data', 'cache/__pycache__/file.py', 'worker/file.pyc', 'link.lnk', 'link.url', 'unins000.exe', 'app-setup.exe', 'index.db-wal', 'metadata-source.json')) {
        Check "Reject unsafe/reserved name $name" { Assert-PortableEntryName $name } -Reject
    }
    Check 'Accept bundled standard-library ZIP' { Assert-PortableEntryName 'python/python313.zip' }
    $payload = New-Fixture '应用 with spaces'
    Check 'Accept exact portable payload' { Assert-PortablePayload $payload }
    $archive = Join-Path $root 'portable.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
    Check 'Accept directly extractable root ZIP' { Assert-PortableArchive $archive }
    $wrapper = Join-Path $root 'wrapper.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $wrapper, [IO.Compression.CompressionLevel]::Optimal, $true)
    Check 'Reject enclosing program directory wrapper' { Assert-PortableArchive $wrapper } -Reject
    foreach ($case in @(
        @{ name = 'traversal'; entry = '../escape' }, @{ name = 'data'; entry = 'data/reviews.db' },
        @{ name = 'duplicate'; entry = 'ORGANIZER.DESKTOP.EXE' }, @{ name = 'unexpected'; entry = 'unexpected.txt' },
        @{ name = 'collision'; entry = 'python' }, @{ name = 'symlink'; entry = 'escape'; attributes = -1577058304 },
        @{ name = 'reparse'; entry = 'escape'; attributes = 1024 }
    )) {
        $modified = Join-Path $root ($case.name + '.zip')
        Copy-Item -LiteralPath $archive -Destination $modified
        Add-ZipEntry $modified $case.entry -Attributes $(if ($case.ContainsKey('attributes')) { $case.attributes } else { 0 })
        Check "Reject $($case.name) archive" { Assert-PortableArchive $modified } -Reject
    }
    foreach ($marker in @('{"formatVersion":2}', '{"formatVersion":1,"dataDirectory":"other"}', '{"formatVersion":"1"}', '{"formatVersion":1,"formatVersion":1}', '{}')) {
        Set-Content -LiteralPath (Join-Path $payload 'portable.json') -Value $marker -Encoding utf8NoBOM
        Write-FixtureManifest $payload
        Check "Reject invalid marker $marker" { Assert-PortablePayload $payload } -Reject
        $invalidMarkerArchive = Join-Path $root ('invalid-marker-' + [Guid]::NewGuid().ToString('N') + '.zip')
        [IO.Compression.ZipFile]::CreateFromDirectory($payload, $invalidMarkerArchive)
        Check "Reject invalid archived marker $marker" { Assert-PortableArchive $invalidMarkerArchive } -Reject
    }
    Set-Content -LiteralPath (Join-Path $payload 'portable.json') -Value ('{"formatVersion":1}' + (' ' * 4096)) -Encoding utf8NoBOM
    Write-FixtureManifest $payload
    Check 'Reject oversized marker payload' { Assert-PortablePayload $payload } -Reject
    $oversized = Join-Path $root 'oversized-marker.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $oversized)
    Check 'Reject oversized marker archive' { Assert-PortableArchive $oversized } -Reject
    Set-Content -LiteralPath (Join-Path $payload 'portable.json') -Value '{"formatVersion":1}' -Encoding utf8NoBOM
    Write-FixtureManifest $payload
    Set-Content -LiteralPath (Join-Path $payload 'worker/main.py') -Value 'altered bytes' -Encoding utf8NoBOM
    Check 'Reject changed payload bytes' { Assert-PortablePayload $payload } -Reject
    $tampered = Join-Path $root 'tampered.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $tampered)
    Check 'Reject changed archive bytes' { Assert-PortableArchive $tampered } -Reject
    $destination = Join-Path $root '升级 with spaces'
    Expand-Archive -LiteralPath $archive -DestinationPath $destination
    New-Item -ItemType Directory -Path (Join-Path $destination 'data/cache') -Force | Out-Null
    $preserved = @{}
    foreach ($relative in @('data/index.db', 'data/metadata-source.json', 'data/cache/user-cache.bin', 'user-note.txt')) {
        $path = Join-Path $destination $relative
        Set-Content -LiteralPath $path -Value "synthetic preserved $relative" -Encoding utf8NoBOM
        $preserved[$path] = (Get-FileHash -LiteralPath $path).Hash
    }
    $programReadme = Join-Path $destination 'README-WINDOWS.txt'
    $programReadmeHash = (Get-FileHash -LiteralPath $programReadme).Hash
    Set-Content -LiteralPath $programReadme -Value 'synthetic previous-build program file' -Encoding utf8NoBOM
    Expand-Archive -LiteralPath $archive -DestinationPath $destination -Force
    Check 'Overwrite upgrade replaces program files' {
        if ((Get-FileHash -LiteralPath $programReadme).Hash -ne $programReadmeHash) { throw 'Program files were not replaced.' }
    }
    Check 'Overwrite upgrade preserves data and unknown files' {
        foreach ($path in $preserved.Keys) {
            if ((Get-FileHash -LiteralPath $path).Hash -ne $preserved[$path]) { throw "Upgrade changed $path" }
        }
    }
} finally {
    # This is the new owned directory above; no user directory is accepted.
    Remove-Item -LiteralPath $root -Recurse -Force
}
Write-Host "Portable layout checks: $script:passed passed."

# Shared, offline-only validation before distributing or extracting a portable ZIP.
Set-StrictMode -Version Latest

function Assert-PortableEntryName {
    param([Parameter(Mandatory)][string] $Name)
    if ($Name -match '[\\:<>"|?*\x00-\x1f]' -or $Name.StartsWith('/') -or $Name.EndsWith('/')) {
        throw "Unsafe portable file path: $Name"
    }
    foreach ($part in $Name.Split('/')) {
        if (-not $part -or $part -in @('.', '..') -or $part -match '[. ]$' -or $part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') {
            throw "Unsafe portable file path: $Name"
        }
    }
    if ($Name.Split('/')[0] -ieq 'data' -or $Name -match '(?i)(^|/)(index\.db(?:-wal|-shm)?|metadata-source\.json|__pycache__)(/|$)' -or
        $Name -match '(?i)\.(lnk|url|pyc)$' -or $Name -match '(?i)^(unins[^/]*\.exe|[^/]*-setup\.exe)$') {
        throw "Portable package must not ship user data, caches, shortcuts or an installer: $Name"
    }
}

function Assert-PortableMarkerJson {
    param([Parameter(Mandatory)][string] $Json)
    $document = [Text.Json.JsonDocument]::Parse($Json)
    try {
        if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw 'Portable marker must be an object.' }
        $properties = @($document.RootElement.EnumerateObject())
        $version = 0
        if ($properties.Count -ne 1 -or $properties[0].Name -cne 'formatVersion' -or
            $properties[0].Value.ValueKind -ne [Text.Json.JsonValueKind]::Number -or
            -not $properties[0].Value.TryGetInt32([ref] $version) -or $version -ne 1) {
            throw 'Portable marker must be exactly {"formatVersion":1}, without duplicate properties.'
        }
    } finally { $document.Dispose() }
}

function Assert-PortableInventory {
    param([Parameter(Mandatory)] $Manifest, [Parameter(Mandatory)] $Marker, [Parameter(Mandatory)] $Names)
    if ($Manifest.schemaVersion -ne 2 -or $Manifest.packageKind -cne 'portable' -or $Manifest.dataMode -cne 'portable' -or
        $Manifest.runtime -cne 'win-x64' -or $Manifest.selfContained -ne $true -or
        $Manifest.defaultNetworkEnabled -ne $false -or $Manifest.liveSourceVerified -ne $false -or $null -ne $Manifest.innoSetup) {
        throw 'Portable manifest has unexpected runtime, data mode, compiler or network defaults.'
    }
    $markerProperties = @($Marker.PSObject.Properties.Name)
    if ($markerProperties.Count -ne 1 -or $markerProperties[0] -cne 'formatVersion' -or
        $Marker.formatVersion -isnot [long] -and $Marker.formatVersion -isnot [int] -or $Marker.formatVersion -ne 1) {
        throw 'Portable marker must be exactly {"formatVersion":1}.'
    }
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [void] $expected.Add('package-manifest.json')
    foreach ($file in $Manifest.files) {
        Assert-PortableEntryName -Name $file.path
        if (-not $expected.Add($file.path) -or $file.sha256 -cnotmatch '^[a-f0-9]{64}$' -or $file.bytes -lt 0) { throw "Invalid manifest inventory entry: $($file.path)" }
    }
    if (-not $expected.SetEquals($Names)) { throw 'Portable archive files do not exactly match the package manifest.' }
    foreach ($required in @('Organizer.Desktop.exe', 'Organizer.Desktop.dll', 'Organizer.Desktop.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll',
        'python/python.exe', 'python/python313.zip', 'python/python313._pth', 'worker/main.py', 'worker/mdcx_worker/protocol.py',
        'python/Lib/site-packages/httpx/__init__.py', 'python/Lib/site-packages/certifi/cacert.pem', 'licenses/python/inventory.json',
        'licenses/python-runtime.json', 'README-WINDOWS.txt', 'source-settings.example.json', 'diagnostics/verify_bundled_http.py', 'portable.json')) {
        if (-not $expected.Contains($required)) { throw "Portable root layout is missing $required" }
    }
}

function Assert-PortablePayload {
    param([Parameter(Mandatory)][string] $Directory)
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in Get-ChildItem -LiteralPath $Directory -Recurse -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Portable payload contains a link: $($item.FullName)" }
        $name = [IO.Path]::GetRelativePath($Directory, $item.FullName).Replace('\', '/')
        Assert-PortableEntryName -Name $name
        if (-not $item.PSIsContainer -and -not $names.Add($name)) { throw "Duplicate portable path: $name" }
    }
    $manifest = Get-Content -LiteralPath (Join-Path $Directory 'package-manifest.json') -Raw | ConvertFrom-Json
    if ((Get-Item -LiteralPath (Join-Path $Directory 'portable.json')).Length -gt 4096) { throw 'Portable marker exceeds 4096 bytes.' }
    $markerJson = Get-Content -LiteralPath (Join-Path $Directory 'portable.json') -Raw
    Assert-PortableMarkerJson -Json $markerJson
    $marker = $markerJson | ConvertFrom-Json
    Assert-PortableInventory -Manifest $manifest -Marker $marker -Names $names
    foreach ($file in $manifest.files) {
        $path = Join-Path $Directory $file.path
        if ((Get-Item -LiteralPath $path).Length -ne $file.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.sha256) {
            throw "Portable payload hash/size mismatch: $($file.path)"
        }
    }
}

function Assert-PortableArchive {
    param([Parameter(Mandatory)][string] $Archive)
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $entries = [Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $zip.Entries) {
            # Generated archives contain files, not directory entries or links.
            Assert-PortableEntryName -Name $entry.FullName
            if (($entry.ExternalAttributes -band 0x400) -ne 0 -or (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) {
                throw "Portable ZIP contains a link/reparse entry: $($entry.FullName)"
            }
            if (-not $entries.TryAdd($entry.FullName, $entry)) { throw "Duplicate portable ZIP path: $($entry.FullName)" }
        }
        foreach ($name in $entries.Keys) {
            $parts = $name.Split('/')
            for ($i = 1; $i -lt $parts.Length; $i++) {
                if ($entries.ContainsKey(($parts[0..($i - 1)] -join '/'))) { throw "Portable ZIP has a file/directory path collision: $name" }
            }
        }
        function Read-ZipJson([string] $Name) {
            if (-not $entries.ContainsKey($Name)) { throw "Portable ZIP is missing root file: $Name" }
            $maximumBytes = if ($Name -ceq 'portable.json') { 4096 } else { 4MB }
            if ($entries[$Name].Length -gt $maximumBytes) { throw "Portable metadata is too large: $Name" }
            $reader = [IO.StreamReader]::new($entries[$Name].Open())
            try {
                $json = $reader.ReadToEnd()
                if ($Name -ceq 'portable.json') { Assert-PortableMarkerJson -Json $json }
                return $json | ConvertFrom-Json
            } finally { $reader.Dispose() }
        }
        $manifest = Read-ZipJson 'package-manifest.json'
        $marker = Read-ZipJson 'portable.json'
        Assert-PortableInventory -Manifest $manifest -Marker $marker -Names $entries.Keys
        foreach ($file in $manifest.files) {
            $entry = $entries[$file.path]
            $stream = $entry.Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
            finally { $stream.Dispose() }
            if ($entry.Length -ne $file.bytes -or $hash -cne $file.sha256) { throw "Portable ZIP hash/size mismatch: $($file.path)" }
        }
    } finally { $zip.Dispose() }
}

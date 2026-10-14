# Copy provenance and license/notice files from the exact restored packages.
# No third-party license is inferred from the application's own GPL notice.
param(
    [Parameter(Mandatory)][string] $AssetsFile,
    [Parameter(Mandatory)][string] $Destination
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$assets = Get-Content -LiteralPath $AssetsFile -Raw | ConvertFrom-Json -AsHashtable
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$packageRoots = @($assets.packageFolders.Keys)
$inventory = [System.Collections.Generic.List[object]]::new()
foreach ($entry in $assets.libraries.GetEnumerator() | Sort-Object Key) {
    if ($entry.Value.type -ne 'package') { continue }
    $relative = $entry.Value.path
    $package = $null
    foreach ($root in $packageRoots) {
        $candidate = Join-Path $root $relative
        if (Test-Path -LiteralPath $candidate -PathType Container) { $package = $candidate; break }
    }
    if (-not $package) { throw "Restored package is missing: $($entry.Key)" }
    $target = Join-Path $Destination ($entry.Key -replace '/', '-')
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    $spec = @(Get-ChildItem -LiteralPath $package -Filter '*.nuspec' -File)
    if ($spec.Count -ne 1) { throw "Expected one NuGet manifest for $($entry.Key)" }
    Copy-Item -LiteralPath $spec[0].FullName -Destination $target
    [xml]$xml = Get-Content -LiteralPath $spec[0].FullName -Raw
    $licenseNode = $xml.SelectSingleNode('//*[local-name()="metadata"]/*[local-name()="license"]')
    $copyright = $xml.SelectSingleNode('//*[local-name()="metadata"]/*[local-name()="copyright"]')
    $files = @(Get-ChildItem -LiteralPath $package -Recurse -File | Where-Object {
        $_.Name -match '^(licen[cs]e|copying|notice|third[-_. ]?party[-_. ]?notices)([._ -]|$)'
    })
    if ($licenseNode -and $licenseNode.GetAttribute('type') -eq 'file') {
        $declared = Join-Path $package $licenseNode.InnerText
        if (-not (Test-Path -LiteralPath $declared -PathType Leaf)) { throw "Declared license missing for $($entry.Key)" }
        $files += Get-Item -LiteralPath $declared
    }
    $copied = @($files | Sort-Object FullName -Unique | ForEach-Object {
        $path = [IO.Path]::GetRelativePath($package, $_.FullName)
        $to = Join-Path $target $path
        New-Item -ItemType Directory -Path (Split-Path $to) -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $to
        $path
    })
    if ($copied.Count -eq 0 -and $licenseNode -and $licenseNode.GetAttribute('type') -eq 'expression') {
        $body = switch ($licenseNode.InnerText) {
            'MIT' { Get-Content -LiteralPath (Join-Path $PSScriptRoot 'licenses/MIT-body.txt') -Raw }
            'Apache-2.0' { Get-Content -LiteralPath (Join-Path $PSScriptRoot 'licenses/Apache-2.0.txt') -Raw }
            default { throw "No full license text available for $($entry.Key): $($licenseNode.InnerText)" }
        }
        if (-not $copyright -or -not $copyright.InnerText) { throw "No copyright notice available for $($entry.Key)" }
        ($copyright.InnerText + "`n`n" + $body) | Set-Content -LiteralPath (Join-Path $target 'LICENSE-from-package-metadata.txt') -Encoding utf8NoBOM
        $copied += 'LICENSE-from-package-metadata.txt'
    }
    if ($copied.Count -eq 0) { throw "No full license or notice available for $($entry.Key)" }
    $inventory.Add([ordered]@{
        package = $entry.Key
        contentHash = $entry.Value.sha512
        license = if ($licenseNode) { $licenseNode.InnerText } else { $null }
        licenseType = if ($licenseNode) { $licenseNode.GetAttribute('type') } else { $null }
        copyright = if ($copyright) { $copyright.InnerText } else { $null }
        includedNotices = $copied
        nuget = "https://www.nuget.org/packages/$($entry.Key)"
    })
}
$inventory | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Destination 'inventory.json') -Encoding utf8NoBOM
# Runtime-pack licenses are not always represented in assets.libraries.
$runtimeRoot = Join-Path $Destination 'dotnet-runtime'
New-Item -ItemType Directory -Path $runtimeRoot -Force | Out-Null
$runtimePackages = @($assets.project.frameworks.Values | ForEach-Object { $_.downloadDependencies } |
    Where-Object { $_.name -like 'Microsoft.NETCore.App.Runtime.*' })
foreach ($runtime in $runtimePackages) {
    $version = ($runtime.version.Trim('[', ']') -split ',')[0].Trim()
    foreach ($root in $packageRoots) {
        $package = Join-Path $root "$($runtime.name.ToLowerInvariant())/$version"
        if (-not (Test-Path -LiteralPath $package -PathType Container)) { continue }
        Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Name -match '(?i)license|notice' } |
            Copy-Item -Destination $runtimeRoot
    }
}

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses/README.txt') -Destination (Join-Path $Destination 'README.txt')
if (-not (Test-Path -LiteralPath (Join-Path $runtimeRoot 'LICENSE.TXT')) -or -not (Test-Path -LiteralPath (Join-Path $runtimeRoot 'THIRD-PARTY-NOTICES.TXT'))) { throw 'The .NET runtime license/third-party notices were not found.' }

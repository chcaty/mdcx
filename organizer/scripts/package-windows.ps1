#Requires -Version 7.2
[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/windows'),
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')][string] $Version = '0.1.0',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9.-]{0,60}$')][string] $BuildLabel = 'local',
    [ValidateSet('portable', 'installer')][string] $PackageKind = 'portable',
    [string] $InnoCompiler
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or -not [Environment]::Is64BitOperatingSystem) { throw 'Packaging requires x64 Windows and PowerShell 7.' }
$organizer = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repository = Split-Path $organizer
$packaging = Join-Path $organizer 'packaging/windows'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$stage = Join-Path $OutputDirectory ('.staging-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $stage 'app'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:MSBUILDDISABLENODEREUSE = '1'

# Portable packaging never discovers, requires or executes an installer compiler.
$compilerVersion = $null
if ($PackageKind -eq 'installer') {
# Optional legacy installer: use the preinstalled compiler; do not bootstrap one.
if (-not $InnoCompiler) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        $(if ($command) { $command.Source })
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) }
    $InnoCompiler = $candidates | Select-Object -First 1
}
if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler -PathType Leaf)) {
    throw 'Inno Setup 6 ISCC.exe is required. Use the documented windows-2022 runner with its preinstalled compiler.'
}
$InnoCompiler = (Resolve-Path -LiteralPath $InnoCompiler).Path
$compilerInfo = (Get-Item -LiteralPath $InnoCompiler).VersionInfo
Write-Host "Inno compiler path: $InnoCompiler"
Write-Host "Launcher metadata only: FileVersion=$($compilerInfo.FileVersion); ProductVersion=$($compilerInfo.ProductVersion)"
. (Join-Path $packaging 'inno-compiler.ps1')
$probeFile = Join-Path $stage 'compiler-probe.iss'
@'
[Setup]
AppName=MDCx compiler version probe
AppVersion=0.0.0
DefaultDirName={tmp}\MDCxCompilerProbe
CreateAppDir=no
Uninstallable=no
Output=no
'@ | Set-Content -LiteralPath $probeFile -Encoding ascii
# /O- is supported by Inno 6.x and prevents an installer being emitted.
# Unlike the PE metadata (which can belong to a shim), the engine banner
# describes the compiler actually loaded by ISCC.
$compilerBanner = (& $InnoCompiler '/O-' $probeFile 2>&1 | Out-String)
$probeExitCode = $LASTEXITCODE
Write-Host $compilerBanner
if ($probeExitCode -ne 0) { throw "Inno compiler no-output probe failed ($probeExitCode); see path, metadata and banner above." }
$compilerVersion = ConvertFrom-InnoCompilerBanner -Banner $compilerBanner
Write-Host "Using verified Inno Setup engine $compilerVersion at $InnoCompiler"
}

Push-Location $organizer
try {
    & dotnet restore src/Organizer.Desktop/Organizer.Desktop.csproj -r win-x64 --locked-mode --disable-parallel --disable-build-servers -m:1
    if ($LASTEXITCODE) { throw "Locked win-x64 restore failed ($LASTEXITCODE)." }
    & dotnet publish src/Organizer.Desktop/Organizer.Desktop.csproj -c Release -r win-x64 --self-contained true --no-restore --disable-build-servers -m:1 `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -p:UseAppHost=true -p:Version=$Version -o $payload
    if ($LASTEXITCODE) { throw "Self-contained publish failed ($LASTEXITCODE)." }
    $sdk = (& dotnet --version).Trim()
    $commit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE) { throw 'Cannot record source commit.' }
} finally { Pop-Location }

$python = Get-Content -LiteralPath (Join-Path $packaging 'python-runtime.json') -Raw | ConvertFrom-Json
$archive = Join-Path $stage $python.archive
Invoke-WebRequest -Uri $python.url -OutFile $archive -MaximumRetryCount 3 -RetryIntervalSec 2
$actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $python.sha256) { throw "Python archive SHA-256 mismatch. Expected $($python.sha256), got $actual. The archive was not extracted or run." }
$pythonDirectory = Join-Path $payload 'python'
Expand-Archive -LiteralPath $archive -DestinationPath $pythonDirectory
# Explicit paths retain embedded isolated mode. Never enable site or consult PYTHONPATH.
@($python.standardLibrary, '.', 'Lib/site-packages', '../worker', '# Isolated bundled worker; do not enable import site.') |
    Set-Content -LiteralPath (Join-Path $pythonDirectory $python.pathFile) -Encoding ascii
$worker = Join-Path $payload 'worker'
New-Item -ItemType Directory -Path $worker -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $organizer 'worker/main.py') -Destination $worker
Copy-Item -LiteralPath (Join-Path $organizer 'worker/README.md') -Destination $worker
foreach ($directory in @('mdcx_worker', 'fixtures')) {
    $source = Join-Path $organizer "worker/$directory"
    foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object { $_.Extension -ne '.pyc' -and $_.FullName -notmatch '[\\/]__pycache__[\\/]' }) {
        $relative = [IO.Path]::GetRelativePath((Join-Path $organizer 'worker'), $file.FullName)
        $target = Join-Path $worker $relative
        New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}
# Build-time wheel extraction only: pinned official URLs/hashes, no pip or setup hooks.
$licenses = Join-Path $payload 'licenses'
& (Join-Path $pythonDirectory 'python.exe') -B (Join-Path $packaging 'bundle_python_dependencies.py') `
    --lock (Join-Path $packaging 'python-dependencies.json') --requirements (Join-Path $organizer 'worker/requirements-live.txt') `
    --destination (Join-Path $pythonDirectory 'Lib/site-packages') --licenses (Join-Path $licenses 'python') --cache (Join-Path $stage 'http-wheels')
if ($LASTEXITCODE) { throw "Pinned HTTP dependency bundling failed ($LASTEXITCODE)." }
$dependencies = Get-Content -LiteralPath (Join-Path $licenses 'python/inventory.json') -Raw | ConvertFrom-Json
Copy-Item -LiteralPath (Join-Path $packaging 'source-settings.example.json') -Destination $payload
$diagnostics = Join-Path $payload 'diagnostics'
New-Item -ItemType Directory -Path $diagnostics -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $packaging 'verify_bundled_http.py') -Destination $diagnostics
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repository 'LICENSE') -Destination (Join-Path $licenses 'MDCx-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $repository 'README.md') -Destination (Join-Path $licenses 'MDCx-original-README.md')
Copy-Item -LiteralPath (Join-Path $organizer 'NOTICE.md') -Destination $licenses
Copy-Item -LiteralPath (Join-Path $pythonDirectory 'LICENSE.txt') -Destination (Join-Path $licenses 'Python-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $packaging 'python-runtime.json') -Destination $licenses
& (Join-Path $packaging 'collect-licenses.ps1') -AssetsFile (Join-Path $organizer 'src/Organizer.Desktop/obj/project.assets.json') -Destination (Join-Path $licenses 'nuget')
if ($PackageKind -eq 'portable') {
    Copy-Item -LiteralPath (Join-Path $packaging 'README-WINDOWS.txt') -Destination $payload
    Copy-Item -LiteralPath (Join-Path $packaging 'portable.json') -Destination $payload
} else {
    Copy-Item -LiteralPath (Join-Path $packaging 'README-INSTALLER.txt') -Destination (Join-Path $payload 'README-WINDOWS.txt')
}

$required = @('Organizer.Desktop.exe', 'Organizer.Desktop.dll', 'Organizer.Desktop.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll',
    'libSkiaSharp.dll', 'e_sqlite3.dll', 'python/python.exe', "python/$($python.standardLibrary)", 'worker/main.py', 'worker/mdcx_worker/protocol.py',
    'worker/fixtures/xcity-demo-001.html', 'worker/fixtures/javbus/detail-demo-001.html', 'licenses/Python-LICENSE.txt', 'licenses/MDCx-LICENSE.txt',
    'python/Lib/site-packages/httpx/__init__.py', 'python/Lib/site-packages/certifi/cacert.pem', 'licenses/python/inventory.json',
    'source-settings.example.json', 'diagnostics/verify_bundled_http.py')
if ($PackageKind -eq 'portable') { $required += 'portable.json' }
foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $path) -PathType Leaf)) { throw "Required package file missing: $path" }
}
# Prove the embedded interpreter's standard-library modules and isolated worker import.
& (Join-Path $pythonDirectory 'python.exe') -B -c 'import sys,json,asyncio,sqlite3,mdcx_worker; assert sys.flags.isolated == 1; assert "site" not in sys.modules; print(sys.version)'
if ($LASTEXITCODE) { throw "Bundled Python import/isolation check failed ($LASTEXITCODE)." }

# Network remains disabled by default. This probe uses only injected MockTransport.
& (Join-Path $pythonDirectory 'python.exe') -B (Join-Path $diagnostics 'verify_bundled_http.py') --package-root $payload
if ($LASTEXITCODE) { throw "Bundled HTTP/configuration offline probe failed ($LASTEXITCODE)." }

$manifest = [ordered]@{
    schemaVersion = 2; appVersion = $Version; buildLabel = $BuildLabel; sourceCommit = $commit
    packageKind = $PackageKind; dataMode = $(if ($PackageKind -eq 'portable') { 'portable' } else { 'installed' })
    runtime = 'win-x64'; selfContained = $true; unsigned = $true; defaultNetworkEnabled = $false; metadataProvider = 'javbus-catalog'; liveSourceVerified = $false
    dotnetSdk = $sdk; innoSetup = $compilerVersion; pythonVersion = $python.version
    pythonArchiveSha256 = $python.sha256; pythonSource = $python.url
    pythonDependencyLockSha256 = $dependencies.lockSha256
    pythonDependencies = @($dependencies.packages | ForEach-Object {
        [ordered]@{ name = $_.name; version = $_.version; url = $_.url; sha256 = $_.sha256; license = $_.license }
    })
    builtUtc = [DateTime]::UtcNow.ToString('o')
    files = @(Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = [IO.Path]::GetRelativePath($payload, $_.FullName).Replace('\', '/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $payload 'package-manifest.json') -Encoding utf8NoBOM
if ($PackageKind -eq 'portable') {
    # Ship program files only. A version upgrade must never replace user data.
    . (Join-Path $packaging 'portable-layout.ps1')
    Assert-PortablePayload -Directory $payload
    $artifact = Join-Path $OutputDirectory "MDCxOrganizer-$Version-$BuildLabel-win-x64-portable.zip"
    if (Test-Path -LiteralPath $artifact) { throw "Refusing to replace an existing build artifact: $artifact" }
    # includeBaseDirectory=false makes the executable immediately visible after
    # extracting once. Do not upload this archive inside another ZIP wrapper.
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $artifact, [IO.Compression.CompressionLevel]::Optimal, $false)
    Assert-PortableArchive -Archive $artifact
    $outputName = 'portable_zip'
} else {
    & $InnoCompiler "/DPayloadDirectory=$payload" "/DArtifactDirectory=$OutputDirectory" "/DAppVersion=$Version" "/DBuildLabel=$BuildLabel" (Join-Path $packaging 'installer.iss')
    if ($LASTEXITCODE) { throw "Installer compilation failed ($LASTEXITCODE)." }
    $artifact = Join-Path $OutputDirectory "MDCxOrganizer-$Version-$BuildLabel-win-x64-setup.exe"
    if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) { throw 'Inno Setup did not produce the expected installer.' }
    $outputName = 'installer'
}
Copy-Item -LiteralPath (Join-Path $payload 'README-WINDOWS.txt'), (Join-Path $payload 'package-manifest.json') -Destination $OutputDirectory
$hash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($artifact))" | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
Write-Host "Package ($PackageKind): $artifact"
Write-Host "SHA256: $hash"
if ($env:GITHUB_OUTPUT) {
    "$outputName=$artifact" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
    "sha256=$hash" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
    "artifact_directory=$OutputDirectory" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
}

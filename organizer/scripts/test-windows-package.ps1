#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Installer,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/windows-test'),
    [switch] $AllowNonCi
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The installer test requires Windows.' }
if ($env:GITHUB_ACTIONS -ne 'true' -and -not $AllowNonCi) {
    throw 'Run on a disposable GitHub Windows runner, or explicitly use -AllowNonCi in a disposable Windows user account.'
}
$Installer = (Resolve-Path -LiteralPath $Installer).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$testId = [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "MDCxPackageTest-$testId"
$installDirectory = Join-Path $testRoot 'Installed App'
$foreignDirectory = Join-Path $testRoot 'Unrelated Working Directory'
New-Item -ItemType Directory -Path $foreignDirectory -Force | Out-Null
$localData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$dataDirectory = Join-Path $localData 'MDCxOrganizer'
$index = Join-Path $dataDirectory 'index.db'
$sourceSettings = Join-Path $dataDirectory 'metadata-source.json'
$sentinel = Join-Path $dataDirectory "package-test-$testId.txt"
$registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{1BD456B0-B27A-44EC-B38C-550684593F2B}_is1'
$defaultProgram = Join-Path $localData 'Programs/MDCxOrganizer'
$machineRegistration = $registration -replace '^HKCU:', 'HKLM:'
if ((Test-Path -LiteralPath $registration) -or (Test-Path -LiteralPath $machineRegistration) -or (Test-Path -LiteralPath $defaultProgram) -or
    (Test-Path -LiteralPath $index) -or (Test-Path -LiteralPath $sourceSettings) -or (Test-Path -LiteralPath "$index-wal") -or (Test-Path -LiteralPath "$index-shm") -or
    (Get-Process -Name 'Organizer.Desktop' -ErrorAction SilentlyContinue)) {
    throw 'Refusing to test over an existing installation, index, or running app. Use a clean disposable Windows account.'
}
# Refuse existing junction/symlink ancestors before creating test-owned data.
foreach ($path in @($dataDirectory, $testRoot)) {
    for ($dir = [IO.DirectoryInfo]::new($path); $null -ne $dir; $dir = $dir.Parent) {
        if ($dir.Exists -and ($dir.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Unsafe reparse-point ancestor: $($dir.FullName)" }
    }
}
$dataDirectoryExisted = Test-Path -LiteralPath $dataDirectory
$indexCreated = $false
$settingsCreated = $false
$settingsHash = $null
$indexHash = $null
$installed = $false
$uninstalled = $false
$nativeReport = Join-Path $testRoot 'native-smoke.json'
$httpReport = Join-Path $testRoot 'bundled-http-smoke.json'
$result = [ordered]@{ schemaVersion = 1; status = 'failed'; installerSha256 = (Get-FileHash -LiteralPath $Installer -Algorithm SHA256).Hash.ToLowerInvariant(); installed = $false; nativeSmoke = $false; bundledHttpSmoke = $false; sourceSettingsPreserved = $false; uninstalled = $false; indexPreserved = $false; dataSentinelPreserved = $false; unknownInstalledFilePreserved = $false; noAutomaticLaunch = $false; error = $null }

function Invoke-TestProcess {
    param([string] $File, [string[]] $Arguments, [int] $TimeoutSeconds = 120, [switch] $IsolateRuntime)
    $start = [Diagnostics.ProcessStartInfo]::new($File)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $foreignDirectory
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    if ($IsolateRuntime) {
        # No SDK/system Python/checkout paths are needed by the installed executable.
        $start.Environment['PATH'] = "$env:SystemRoot\System32;$env:SystemRoot"
        $start.Environment['DOTNET_ROOT'] = Join-Path $testRoot 'no-dotnet-installed'
        $start.Environment['DOTNET_ROOT_X64'] = $start.Environment['DOTNET_ROOT']
        $start.Environment['PYTHONHOME'] = Join-Path $testRoot 'no-python-installed'
        $start.Environment['PYTHONPATH'] = Join-Path $testRoot 'untrusted-pythonpath'
        $start.Environment.Remove('ORGANIZER_PYTHON') | Out-Null
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { $process.Kill($true); $process.WaitForExit(); throw "Timed out running $([IO.Path]::GetFileName($File))." }
        if ($process.ExitCode -ne 0) { throw "$([IO.Path]::GetFileName($File)) exited $($process.ExitCode). See captured test reports/logs." }
    } finally { $process.Dispose() }
}
try {
    Invoke-TestProcess -File $Installer -Arguments @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/DIR=$installDirectory", "/LOG=$(Join-Path $OutputDirectory 'install.log')")
    $installed = $true; $result.installed = $true
    $app = Join-Path $installDirectory 'Organizer.Desktop.exe'
    $python = Join-Path $installDirectory 'python/python.exe'
    $worker = Join-Path $installDirectory 'worker/main.py'
    foreach ($path in @($app, $python, $worker, (Join-Path $installDirectory 'unins000.exe'))) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Installed file missing: $path" }
    }
    if (Get-Process -Name 'Organizer.Desktop' -ErrorAction SilentlyContinue) { throw 'Silent installation unexpectedly launched the app.' }
    $result.noAutomaticLaunch = $true
    if (-not (Test-Path -LiteralPath $registration)) { throw 'Per-user uninstall registration was not created.' }
    $registeredPath = (Get-ItemProperty -LiteralPath $registration).InstallLocation.TrimEnd('\')
    if ($registeredPath -ne $installDirectory.TrimEnd('\')) { throw 'Installer registered an unexpected destination.' }
    $machineRegistration = $registration -replace '^HKCU:', 'HKLM:'
    if (Test-Path -LiteralPath $machineRegistration) { throw 'Unexpected machine-wide uninstall registration.' }
    # Every manifest-recorded byte must survive installer extraction unchanged.
    $manifest = Get-Content -LiteralPath (Join-Path $installDirectory 'package-manifest.json') -Raw | ConvertFrom-Json
    foreach ($file in $manifest.files) {
        $path = Join-Path $installDirectory $file.path
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) { throw "Installed hash mismatch: $($file.path)" }
    }
    if ($manifest.schemaVersion -ne 2 -or $manifest.defaultNetworkEnabled -ne $false -or $manifest.liveSourceVerified -ne $false -or
        @($manifest.pythonDependencies).Count -ne 7) { throw 'Package manifest omitted HTTP runtime provenance or disabled defaults.' }
    # Run under the installed isolated interpreter; no system pip/Python/dependency path.
    Invoke-TestProcess -File $python -Arguments @('-B', (Join-Path $installDirectory 'diagnostics/verify_bundled_http.py'),
        '--package-root', $installDirectory, '--report', $httpReport) -IsolateRuntime
    $httpSmoke = Get-Content -LiteralPath $httpReport -Raw | ConvertFrom-Json
    if ($httpSmoke.status -ne 'passed' -or $httpSmoke.httpxVersion -ne '0.28.1' -or
        $httpSmoke.sourceNetworkRequests -ne 0 -or $httpSmoke.mockRequests -ne 1) { throw 'Bundled HTTP/configuration probe failed.' }
    foreach ($field in @('dependenciesBundled', 'sourceConfigDisabled', 'javbusWired', 'mockMetadataMatched', 'environmentIgnored')) {
        if ($httpSmoke.$field -ne $true) { throw "Bundled HTTP requirement failed: $field" }
    }
    $result.bundledHttpSmoke = $true
    $unknownFile = Join-Path $installDirectory "user-owned-$testId.txt"
    Set-Content -LiteralPath $unknownFile -Value $testId -Encoding ascii
    New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
    Set-Content -LiteralPath $sentinel -Value $testId -Encoding ascii
    $createIndex = 'import sqlite3,sys; c=sqlite3.connect(sys.argv[1]); c.execute("CREATE TABLE package_preservation_test(value TEXT)"); c.execute("INSERT INTO package_preservation_test VALUES(?)",(sys.argv[2],)); c.commit(); c.close()'
    Invoke-TestProcess -File $python -Arguments @('-B', '-c', $createIndex, $index, $testId) -IsolateRuntime
    $indexCreated = $true
    Copy-Item -LiteralPath (Join-Path $installDirectory 'source-settings.example.json') -Destination $sourceSettings
    $settingsCreated = $true
    $settingsHash = (Get-FileHash -LiteralPath $sourceSettings -Algorithm SHA256).Hash
    $indexHash = (Get-FileHash -LiteralPath $index -Algorithm SHA256).Hash
    $sentinelHash = (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash
    Invoke-TestProcess -File $app -Arguments @('--smoke-test-package', '--package-smoke-report', $nativeReport) -TimeoutSeconds 110 -IsolateRuntime
    $smoke = Get-Content -LiteralPath $nativeReport -Raw | ConvertFrom-Json
    if ($smoke.schemaVersion -ne 1 -or $smoke.status -ne 'passed' -or $smoke.exitCode -ne 0 -or
        $smoke.nativeBackend -ne 'Win32' -or $smoke.nativeHandleDescriptor -ne 'HWND' -or
        $smoke.applicationLifetimeExitCode -ne 0 -or $smoke.previewOperations -ne 2) { throw 'Native package smoke did not report the expected completed Win32 preview.' }
    foreach ($field in @('windowOpened', 'windowRendered', 'windowClosed', 'fixtureMatched', 'indexIsolated', 'sourceUnchanged', 'temporaryRootRemoved')) {
        if ($smoke.$field -ne $true) { throw "Native smoke requirement failed: $field" }
    }
    if ($smoke.pythonExecutable -ne $python -or $smoke.interpreterReportedExecutable -ne $python -or $smoke.workerPath -ne $worker -or
        $smoke.fixtureIdentifier -ne 'DEMO-001' -or $smoke.fixtureResponseOrigin -ne 'synthetic_uncaptured') { throw 'Native smoke did not use the installed Python/worker/neutral fixture.' }
    $expectedScreenshot = [IO.Path]::ChangeExtension($nativeReport, '.png')
    if ($smoke.screenshotPath -ne $expectedScreenshot -or -not (Test-Path -LiteralPath $expectedScreenshot -PathType Leaf)) { throw 'Native window screenshot is missing.' }
    $result.nativeSmoke = $true
    Invoke-TestProcess -File (Join-Path $installDirectory 'unins000.exe') -Arguments @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$(Join-Path $OutputDirectory 'uninstall.log')")
    $uninstalled = $true; $result.uninstalled = $true
    foreach ($path in @($app, $python, $worker)) { if (Test-Path -LiteralPath $path) { throw "Installed binary survived uninstall: $path" } }
    if (Test-Path -LiteralPath $registration) { throw 'Uninstall registration survived uninstall.' }
    $result.indexPreserved = (Test-Path -LiteralPath $index) -and ((Get-FileHash -LiteralPath $index -Algorithm SHA256).Hash -eq $indexHash)
    $result.sourceSettingsPreserved = (Test-Path -LiteralPath $sourceSettings) -and ((Get-FileHash -LiteralPath $sourceSettings -Algorithm SHA256).Hash -eq $settingsHash)
    $result.dataSentinelPreserved = (Test-Path -LiteralPath $sentinel) -and ((Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash -eq $sentinelHash)
    $result.unknownInstalledFilePreserved = (Test-Path -LiteralPath $unknownFile) -and ((Get-Content -LiteralPath $unknownFile -Raw).Trim() -eq $testId)
    if (-not $result.sourceSettingsPreserved -or -not $result.indexPreserved -or -not $result.dataSentinelPreserved -or -not $result.unknownInstalledFilePreserved) { throw 'Uninstall removed or changed test-owned user data.' }
    $result.status = 'passed'
} catch {
    $result.error = $_.Exception.Message
    throw
} finally {
    if ($installed -and -not $uninstalled -and (Test-Path -LiteralPath (Join-Path $installDirectory 'unins000.exe'))) {
        try { Invoke-TestProcess -File (Join-Path $installDirectory 'unins000.exe') -Arguments @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') } catch { Write-Warning "Cleanup uninstall failed: $_" }
    }
    foreach ($path in @($nativeReport, $httpReport, [IO.Path]::ChangeExtension($nativeReport, '.png'))) {
        if (Test-Path -LiteralPath $path -PathType Leaf) { Copy-Item -LiteralPath $path -Destination $OutputDirectory -Force }
    }
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'install-test.json') -Encoding utf8NoBOM
    # Cleanup only the exact fixture files created above, never real index/media.
    if ($settingsCreated -and (Test-Path -LiteralPath $sourceSettings) -and (Get-FileHash -LiteralPath $sourceSettings -Algorithm SHA256).Hash -eq $settingsHash) { Remove-Item -LiteralPath $sourceSettings }
    if ($indexCreated -and (Test-Path -LiteralPath $index) -and (Get-FileHash -LiteralPath $index -Algorithm SHA256).Hash -eq $indexHash) { Remove-Item -LiteralPath $index }
    if ((Test-Path -LiteralPath $sentinel) -and ((Get-Content -LiteralPath $sentinel -Raw).Trim() -eq $testId)) { Remove-Item -LiteralPath $sentinel }
    if (-not $dataDirectoryExisted -and (Test-Path -LiteralPath $dataDirectory) -and @(Get-ChildItem -LiteralPath $dataDirectory -Force).Count -eq 0) { Remove-Item -LiteralPath $dataDirectory }
    # Leave the unique temp program/smoke directory for failure diagnostics; no broad deletion.
}
Write-Host "Windows install/native-smoke/uninstall test passed. Reports: $OutputDirectory"

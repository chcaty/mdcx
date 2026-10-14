#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Archive,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/windows-test'),
    [switch] $AllowNonCi
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The native portable test requires Windows.' }
if ($env:GITHUB_ACTIONS -ne 'true' -and -not $AllowNonCi) {
    throw 'Run on a disposable GitHub Windows runner, or explicitly use -AllowNonCi in a disposable Windows user account.'
}
$Archive = (Resolve-Path -LiteralPath $Archive).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
. (Join-Path $PSScriptRoot '../packaging/windows/portable-layout.ps1')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('MDCxPortableTest-' + [Guid]::NewGuid().ToString('N'))
$appDirectory = Join-Path $testRoot '便携 应用 with spaces'
$foreignDirectory = Join-Path $testRoot 'Unrelated Working Directory'
New-Item -ItemType Directory -Path $foreignDirectory -Force | Out-Null
$dataDirectory = Join-Path $appDirectory 'data'
$localDataDirectory = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'MDCxOrganizer'
$defaultProgram = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Programs/MDCxOrganizer'
$registration = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{1BD456B0-B27A-44EC-B38C-550684593F2B}_is1'
$registryPaths = @("HKCU:\$registration", "HKLM:\$registration", "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{1BD456B0-B27A-44EC-B38C-550684593F2B}_is1")
if ((Test-Path -LiteralPath $localDataDirectory) -or (Test-Path -LiteralPath $defaultProgram) -or
    @($registryPaths | Where-Object { Test-Path -LiteralPath $_ }).Count -ne 0 -or
    (Get-Process -Name 'Organizer.Desktop' -ErrorAction SilentlyContinue)) {
    throw 'Refusing to run with an existing Organizer installation, data folder or process. Use a clean disposable Windows account.'
}
foreach ($path in @($testRoot, $OutputDirectory)) {
    for ($directory = [IO.DirectoryInfo]::new($path); $null -ne $directory; $directory = $directory.Parent) {
        if ($directory.Exists -and ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Unsafe test reparse-point ancestor: $($directory.FullName)" }
    }
}
$reports = @('native-smoke.json', 'bundled-http-smoke.json', 'portable-startup.json', 'portable-upgrade-startup.json', 'portable-readonly.json', 'portable-junction.json', 'portable-missing-marker.json')
$result = [ordered]@{
    schemaVersion = 1; status = 'failed'; archiveSha256 = (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash.ToLowerInvariant()
    rootLayout = $false; unicodeAndSpacePath = $false; bundledHttpSmoke = $false; nativeSmoke = $false; portableStartup = $false
    upgradePreservedData = $false; programFilesReplaced = $false; upgradedStartup = $false; readonlyFriendlyFailure = $false; junctionFriendlyFailure = $false; missingMarkerFriendlyFailure = $false
    noLocalAppDataFallback = $false; noInstallRegistration = $false; noShortcuts = $false; sourceSettingsPreserved = $false; error = $null
}

function Get-ShortcutSnapshot {
    # Compare only filenames/hashes in memory; never include account paths or
    # unrelated shortcut contents in the diagnostic artifact.
    $snapshot = [Collections.Generic.SortedDictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($folder in @([Environment+SpecialFolder]::StartMenu, [Environment+SpecialFolder]::CommonStartMenu,
        [Environment+SpecialFolder]::DesktopDirectory, [Environment+SpecialFolder]::CommonDesktopDirectory)) {
        $path = [Environment]::GetFolderPath($folder)
        if ($path -and (Test-Path -LiteralPath $path)) {
            foreach ($file in Get-ChildItem -LiteralPath $path -Recurse -File -Filter '*.lnk') {
                $snapshot[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            }
        }
    }
    return $snapshot
}
function Assert-NoInstalledState {
    if ((Test-Path -LiteralPath $localDataDirectory) -or (Test-Path -LiteralPath $defaultProgram)) { throw 'Portable mode wrote outside its adjacent data directory.' }
    foreach ($path in $registryPaths) { if (Test-Path -LiteralPath $path) { throw 'Portable mode created uninstall registration.' } }
}
function Invoke-TestProcess {
    param([string] $File, [string[]] $Arguments, [int] $TimeoutSeconds = 120, [int] $ExpectedExitCode = 0)
    $start = [Diagnostics.ProcessStartInfo]::new($File)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $foreignDirectory
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    # The application and probes cannot find a system SDK/Python or checkout.
    $start.Environment['PATH'] = "$env:SystemRoot\System32;$env:SystemRoot"
    $start.Environment['DOTNET_ROOT'] = Join-Path $testRoot 'no-dotnet-installed'
    $start.Environment['DOTNET_ROOT_X64'] = $start.Environment['DOTNET_ROOT']
    $start.Environment['PYTHONHOME'] = Join-Path $testRoot 'no-python-installed'
    $start.Environment['PYTHONPATH'] = Join-Path $testRoot 'untrusted-pythonpath'
    $start.Environment.Remove('ORGANIZER_PYTHON') | Out-Null
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { $process.Kill($true); $process.WaitForExit(); throw "Timed out running $([IO.Path]::GetFileName($File))." }
        if ($process.ExitCode -ne $ExpectedExitCode) { throw "$([IO.Path]::GetFileName($File)) exited $($process.ExitCode); expected $ExpectedExitCode. See native reports." }
    } finally { $process.Dispose() }
}
function Invoke-StartupProbe {
    param([string] $ReportName, [string] $ExpectedError)
    $path = Join-Path $testRoot $ReportName
    Invoke-TestProcess -File $app -Arguments @('--smoke-test-startup', '--startup-smoke-report', $path) -ExpectedExitCode $(if ($ExpectedError) { 1 } else { 0 })
    $report = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($report.schemaVersion -ne 1 -or $report.nativeBackend -ne 'Win32' -or $report.nativeHandleDescriptor -ne 'HWND' -or
        $report.windowOpened -ne $true -or $report.windowRendered -ne $true -or $report.windowClosed -ne $true -or $null -ne $report.error) {
        throw 'Startup diagnostic did not render and close its actual native window cleanly.'
    }
    $screenshot = [IO.Path]::ChangeExtension($path, '.png')
    if ($report.screenshotPath -ne $screenshot -or -not (Test-Path -LiteralPath $screenshot -PathType Leaf) -or (Get-Item -LiteralPath $screenshot).Length -le 4096) {
        throw 'Startup diagnostic did not produce its nonempty native screenshot.'
    }
    if ($ExpectedError) {
        if ($report.status -ne 'failed' -or $report.exitCode -ne 1 -or $report.applicationLifetimeExitCode -ne 1 -or
            $report.errorWindowShown -ne $true -or $report.startupErrorCode -ne $ExpectedError -or -not $report.startupError -or
            $null -ne $report.sourceNetworkingEnabled -or $report.dataMode -ne 'portable' -or $report.userDataDirectory -ne $dataDirectory) {
            throw "Startup failure did not produce the expected friendly error: $ExpectedError"
        }
    } else {
        if ($report.status -ne 'passed' -or $report.exitCode -ne 0 -or $report.applicationLifetimeExitCode -ne 0 -or
            $report.errorWindowShown -ne $false -or $null -ne $report.startupErrorCode -or $report.dataMode -ne 'portable' -or
            $report.userDataDirectory -ne $dataDirectory -or $report.databasePath -ne (Join-Path $dataDirectory 'index.db') -or
            $report.sourceSettingsPath -ne (Join-Path $dataDirectory 'metadata-source.json') -or $report.sourceNetworkingEnabled -ne $false) {
            throw 'Default portable startup did not resolve its adjacent disabled-network data layout.'
        }
    }
    Assert-NoInstalledState
}
$shortcutBefore = Get-ShortcutSnapshot
try {
    Assert-PortableArchive -Archive $Archive
    Expand-Archive -LiteralPath $Archive -DestinationPath $appDirectory
    Assert-PortablePayload -Directory $appDirectory
    $manifest = Get-Content -LiteralPath (Join-Path $appDirectory 'package-manifest.json') -Raw | ConvertFrom-Json
    $dependencies = Get-Content -LiteralPath (Join-Path $appDirectory 'licenses/python/inventory.json') -Raw | ConvertFrom-Json
    if (@($manifest.pythonDependencies).Count -ne 7 -or $manifest.pythonDependencyLockSha256 -ne $dependencies.lockSha256) {
        throw 'Portable manifest omitted its complete HTTP dependency provenance.'
    }
    if (Test-Path -LiteralPath $dataDirectory) { throw 'Fresh portable package shipped a data directory.' }
    $app = Join-Path $appDirectory 'Organizer.Desktop.exe'
    $python = Join-Path $appDirectory 'python/python.exe'
    $worker = Join-Path $appDirectory 'worker/main.py'
    $result.rootLayout = $true
    $result.unicodeAndSpacePath = $appDirectory.Contains('便携 应用 with spaces')
    Invoke-TestProcess -File $python -Arguments @('-B', (Join-Path $appDirectory 'diagnostics/verify_bundled_http.py'), '--package-root', $appDirectory,
        '--report', (Join-Path $testRoot 'bundled-http-smoke.json'))
    $http = Get-Content -LiteralPath (Join-Path $testRoot 'bundled-http-smoke.json') -Raw | ConvertFrom-Json
    if ($http.status -ne 'passed' -or $http.httpxVersion -ne '0.28.1' -or $http.sourceNetworkRequests -ne 0 -or $http.mockRequests -ne 1) { throw 'Bundled HTTP/mock/configuration probe failed.' }
    foreach ($field in @('dependenciesBundled', 'sourceConfigDisabled', 'javbusWired', 'mockMetadataMatched', 'environmentIgnored')) {
        if ($http.$field -ne $true) { throw "Bundled HTTP requirement failed: $field" }
    }
    $result.bundledHttpSmoke = $true
    $nativeReport = Join-Path $testRoot 'native-smoke.json'
    Invoke-TestProcess -File $app -Arguments @('--smoke-test-package', '--package-smoke-report', $nativeReport) -TimeoutSeconds 110
    $native = Get-Content -LiteralPath $nativeReport -Raw | ConvertFrom-Json
    if ($native.schemaVersion -ne 1 -or $native.status -ne 'passed' -or $native.exitCode -ne 0 -or
        $native.nativeBackend -ne 'Win32' -or $native.nativeHandleDescriptor -ne 'HWND' -or
        $native.applicationLifetimeExitCode -ne 0 -or $native.previewOperations -ne 2) { throw 'Native package smoke did not complete the Win32 preview.' }
    foreach ($field in @('windowOpened', 'windowRendered', 'windowClosed', 'fixtureMatched', 'indexIsolated', 'sourceUnchanged', 'temporaryRootRemoved')) {
        if ($native.$field -ne $true) { throw "Native smoke requirement failed: $field" }
    }
    if ($native.pythonExecutable -ne $python -or $native.interpreterReportedExecutable -ne $python -or $native.workerPath -ne $worker -or
        $native.fixtureIdentifier -ne 'DEMO-001' -or $native.fixtureResponseOrigin -ne 'synthetic_uncaptured') { throw 'Native smoke used an unexpected interpreter/worker/fixture.' }
    if ($native.screenshotPath -ne [IO.Path]::ChangeExtension($nativeReport, '.png') -or -not (Test-Path -LiteralPath $native.screenshotPath -PathType Leaf)) { throw 'Native screenshot is missing.' }
    $result.nativeSmoke = $true
    if (Test-Path -LiteralPath $dataDirectory) { throw 'Isolated fixture smoke unexpectedly created the portable user-data folder.' }
    Invoke-StartupProbe -ReportName 'portable-startup.json'
    $result.portableStartup = $true
    # Populate only this test-owned adjacent directory. Never open real media or
    # an existing installed app's database. Upgrade must preserve every byte.
    New-Item -ItemType Directory -Path (Join-Path $dataDirectory 'cache') -Force | Out-Null
    $index = Join-Path $dataDirectory 'index.db'
    $sourceSettings = Join-Path $dataDirectory 'metadata-source.json'
    Invoke-TestProcess -File $python -Arguments @('-B', '-c',
        'import sqlite3,sys; c=sqlite3.connect(sys.argv[1]); c.execute("CREATE TABLE portable_preservation_test(value TEXT)"); c.execute("INSERT INTO portable_preservation_test VALUES(?)",("synthetic",)); c.commit(); c.close()', $index)
    Copy-Item -LiteralPath (Join-Path $appDirectory 'source-settings.example.json') -Destination $sourceSettings
    Set-Content -LiteralPath (Join-Path $dataDirectory 'cache/synthetic-cache.txt') -Value 'test-owned cache sentinel' -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $dataDirectory 'user-preferences.txt') -Value 'test-owned preferences sentinel' -Encoding utf8NoBOM
    $unknownProgramFile = Join-Path $appDirectory 'user-note.txt'
    Set-Content -LiteralPath $unknownProgramFile -Value 'test-owned unknown program file' -Encoding utf8NoBOM
    $preserved = @{}
    foreach ($file in Get-ChildItem -LiteralPath $dataDirectory -Recurse -File) { $preserved[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName).Hash }
    $preserved[$unknownProgramFile] = (Get-FileHash -LiteralPath $unknownProgramFile).Hash
    $settingsHash = (Get-FileHash -LiteralPath $sourceSettings).Hash
    $programReadme = Join-Path $appDirectory 'README-WINDOWS.txt'
    $programReadmeHash = (Get-FileHash -LiteralPath $programReadme).Hash
    Set-Content -LiteralPath $programReadme -Value 'Synthetic previous-build program file' -Encoding utf8NoBOM
    # The documented update: close app, extract the next program archive into
    # the same folder, overwrite program files, retain data and unknown files.
    Expand-Archive -LiteralPath $Archive -DestinationPath $appDirectory -Force
    foreach ($path in $preserved.Keys) {
        if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path).Hash -ne $preserved[$path]) { throw 'Overwrite upgrade changed a synthetic user-data file.' }
    }
    $result.programFilesReplaced = (Get-FileHash -LiteralPath $programReadme).Hash -eq $programReadmeHash
    if (-not $result.programFilesReplaced) { throw 'Overwrite upgrade did not replace the previous-build program file.' }
    $result.upgradePreservedData = $true
    Invoke-StartupProbe -ReportName 'portable-upgrade-startup.json'
    $result.upgradedStartup = $true
    $result.sourceSettingsPreserved = (Get-FileHash -LiteralPath $sourceSettings).Hash -eq $settingsHash
    if (-not $result.sourceSettingsPreserved) { throw 'Upgraded startup changed saved source settings.' }
    $beforeErrors = @{}
    foreach ($file in Get-ChildItem -LiteralPath $dataDirectory -Recurse -File) { $beforeErrors[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName).Hash }
    # Deny writes to the owned data folder, including its children. Restore the
    # exact ACL in finally; never modify the app directory or any real user data.
    $originalAcl = Get-Acl -LiteralPath $dataDirectory
    try {
        $restrictedAcl = Get-Acl -LiteralPath $dataDirectory
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $rule = [Security.AccessControl.FileSystemAccessRule]::new($identity, [Security.AccessControl.FileSystemRights]::Write,
            [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit', [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Deny)
        $restrictedAcl.AddAccessRule($rule)
        Set-Acl -LiteralPath $dataDirectory -AclObject $restrictedAcl
        $writeFailed = $false
        try { [IO.File]::WriteAllText((Join-Path $dataDirectory 'must-not-be-created.txt'), 'unexpected') } catch [UnauthorizedAccessException] { $writeFailed = $true }
        if (-not $writeFailed) { throw 'Read-only ACL fixture did not deny writes.' }
        Invoke-StartupProbe -ReportName 'portable-readonly.json' -ExpectedError 'portable_data_unwritable'
        $result.readonlyFriendlyFailure = $true
    } finally { Set-Acl -LiteralPath $dataDirectory -AclObject $originalAcl }
    # Junctions require no symlink privilege. Only a new test-owned target is
    # used; native startup must reject the link before opening its database.
    $junctionTarget = Join-Path $testRoot 'Owned junction target'
    Move-Item -LiteralPath $dataDirectory -Destination $junctionTarget
    try {
        New-Item -ItemType Junction -Path $dataDirectory -Target $junctionTarget | Out-Null
        Invoke-StartupProbe -ReportName 'portable-junction.json' -ExpectedError 'portable_data_unwritable'
        $result.junctionFriendlyFailure = $true
    } finally {
        if (Test-Path -LiteralPath $dataDirectory) {
            if (-not ((Get-Item -LiteralPath $dataDirectory).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Refusing to remove a non-junction restoration path.' }
            [IO.Directory]::Delete($dataDirectory) # Remove the link itself, never recurse into its target.
        }
        Move-Item -LiteralPath $junctionTarget -Destination $dataDirectory
    }
    $marker = Join-Path $appDirectory 'portable.json'
    $savedMarker = Join-Path $testRoot 'portable-marker.saved'
    Move-Item -LiteralPath $marker -Destination $savedMarker
    try {
        Invoke-StartupProbe -ReportName 'portable-missing-marker.json' -ExpectedError 'portable_marker_invalid'
        $result.missingMarkerFriendlyFailure = $true
    } finally { Move-Item -LiteralPath $savedMarker -Destination $marker }
    foreach ($path in $beforeErrors.Keys) {
        if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path).Hash -ne $beforeErrors[$path]) { throw 'Failed startup changed a synthetic user-data file.' }
    }
    if (@(Get-ChildItem -LiteralPath $dataDirectory -Recurse -File).Count -ne $beforeErrors.Count) { throw 'Failed startup added unexpected data files.' }
    Assert-NoInstalledState
    $result.noLocalAppDataFallback = $true
    $result.noInstallRegistration = $true
    $shortcutAfter = Get-ShortcutSnapshot
    if ($shortcutBefore.Count -ne $shortcutAfter.Count) { throw 'Portable execution changed Start-menu/desktop shortcuts.' }
    foreach ($key in $shortcutBefore.Keys) {
        if (-not $shortcutAfter.ContainsKey($key) -or $shortcutAfter[$key] -ne $shortcutBefore[$key]) { throw 'Portable execution changed Start-menu/desktop shortcuts.' }
    }
    $result.noShortcuts = $true
    $result.status = 'passed'
} catch {
    $result.error = $_.Exception.Message
    throw
} finally {
    foreach ($name in $reports) {
        foreach ($path in @((Join-Path $testRoot $name), [IO.Path]::ChangeExtension((Join-Path $testRoot $name), '.png'))) {
            if (Test-Path -LiteralPath $path -PathType Leaf) { Copy-Item -LiteralPath $path -Destination $OutputDirectory -Force }
        }
    }
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'portable-test.json') -Encoding utf8NoBOM
    # Retain this unique synthetic test directory on the disposable runner for
    # troubleshooting; upload reports only, never the program/data directory.
}
Write-Host "Windows portable/native startup/upgrade/error checks passed. Reports: $OutputDirectory"

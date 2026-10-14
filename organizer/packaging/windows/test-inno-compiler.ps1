#Requires -Version 7.2
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'inno-compiler.ps1')
$passed = 0
foreach ($version in @('6.3.0', '6.7.1', '6.99.9')) {
    $banner = "Inno Setup 6 Command-Line Compiler`r`nCompiler engine version: Inno Setup $version`r`nSuccessful compile."
    if ((ConvertFrom-InnoCompilerBanner -Banner $banner) -ne $version) { throw "Wrong version: $version" }
    $passed++
}
if ((ConvertFrom-InnoCompilerBanner -Banner 'Compiler engine version: Inno Setup 6.3.3 (u)') -ne '6.3.3') { throw 'Legacy Unicode banner failed.' }
$passed++
foreach ($banner in @(
    '',
    'Inno Setup 6 Command-Line Compiler',
    'File version: 6.7.1',
    'Compiler engine version: Inno Setup 6.2.2',
    'Compiler engine version: Inno Setup 7.0.0',
    'Compiler engine version: Inno Setup 6.7.1-beta',
    "Compiler engine version: Inno Setup 6.7.1`nCompiler engine version: Inno Setup 6.7.1"
)) {
    $rejected = $false
    try { ConvertFrom-InnoCompilerBanner -Banner $banner | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw "Invalid/unsupported engine banner accepted: $banner" }
    $passed++
}
Write-Host "PASS: $passed Inno engine-version parser checks."

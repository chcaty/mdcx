# ISCC may be a launcher or Chocolatey shim. Validate the loaded engine instead
# of its PE file version. Inno 6.7.1 prints this line during a /O- compile;
# unlike newer Inno versions, that release does not support --version.
function ConvertFrom-InnoCompilerBanner {
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyString()][string] $Banner)
    $found = [regex]::Matches($Banner,
        '(?m)^Compiler engine version: Inno Setup (?<version>[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?)(?: \(u\))?\s*$')
    if ($found.Count -ne 1) { throw 'Cannot identify exactly one Inno Setup compiler engine version from the probe output.' }
    $version = [version]$found[0].Groups['version'].Value
    if ($version -lt [version]'6.3.0' -or $version -ge [version]'7.0.0') {
        throw "Inno Setup >=6.3.0 and <7.0.0 is required; detected engine $version."
    }
    return $version.ToString()
}

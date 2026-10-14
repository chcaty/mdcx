$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:MSBUILDDISABLENODEREUSE = '1'
$env:ORGANIZER_PYTHON = 'python'
dotnet restore MediaOrganizer.sln --locked-mode --disable-parallel --disable-build-servers -m:1
if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet build MediaOrganizer.sln -c Release --no-restore --disable-build-servers -m:1
if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet run --project tests/Organizer.Checks -c Release --no-build -- (Join-Path $PWD 'worker/main.py')
if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet src/Organizer.Desktop/bin/Release/net10.0/Organizer.Desktop.dll --smoke-test-ui --python python
if ($LASTEXITCODE) { exit $LASTEXITCODE }
python -m unittest discover -s worker/tests -v
if ($LASTEXITCODE) { exit $LASTEXITCODE }
python -m compileall -q worker
exit $LASTEXITCODE

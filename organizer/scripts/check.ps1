$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:MSBUILDDISABLENODEREUSE = '1'
$env:ORGANIZER_PYTHON = 'python'
$env:PYTHONUTF8 = '1'
$env:PYTHONIOENCODING = 'utf-8'
python packaging/windows/bundle_python_dependencies.py --check-only --check-installed --requirements worker/requirements-live.txt
if ($LASTEXITCODE) { exit $LASTEXITCODE }
python -m unittest discover -s packaging/windows -p 'test_*.py' -v
if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet restore MediaOrganizer.sln --locked-mode --disable-parallel --disable-build-servers -m:1
if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet build MediaOrganizer.sln -c Release --no-restore --disable-build-servers -m:1
if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet run --project tests/Organizer.Checks -c Release --no-build -- (Join-Path $PWD 'worker/main.py')
if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet src/Organizer.Desktop/bin/Release/net10.0/Organizer.Desktop.dll --smoke-test-ui --python python
if ($LASTEXITCODE) { exit $LASTEXITCODE }
python scripts/check_view.py dotnet
if ($LASTEXITCODE) { exit $LASTEXITCODE }
python scripts/check_view.py dotnet --screenshot-width 1200 --screenshot-height 800 --font-scale 1.25
if ($LASTEXITCODE) { exit $LASTEXITCODE }
python -m unittest discover -s worker/tests -v
if ($LASTEXITCODE) { exit $LASTEXITCODE }
python -m compileall -q worker
exit $LASTEXITCODE

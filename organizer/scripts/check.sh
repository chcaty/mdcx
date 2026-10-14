#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 AVALONIA_TELEMETRY_OPTOUT=1 DOTNET_GENERATE_ASPNET_CERTIFICATE=false MSBUILDDISABLENODEREUSE=1
DOTNET="${DOTNET:-dotnet}"
PYTHON="${PYTHON:-python3}"
export ORGANIZER_PYTHON="$PYTHON"
"$DOTNET" restore MediaOrganizer.sln --locked-mode --disable-parallel --disable-build-servers -m:1
"$DOTNET" build MediaOrganizer.sln -c Release --no-restore --disable-build-servers -m:1
"$DOTNET" run --project tests/Organizer.Checks -c Release --no-build -- "$PWD/worker/main.py"
"$DOTNET" src/Organizer.Desktop/bin/Release/net10.0/Organizer.Desktop.dll --smoke-test-ui --python "$PYTHON"
"$PYTHON" scripts/check_view.py "$DOTNET"
"$PYTHON" scripts/check_view.py "$DOTNET" --screenshot-width 1200 --screenshot-height 800 --font-scale 1.25
"$PYTHON" -m unittest discover -s worker/tests -v
"$PYTHON" -m compileall -q worker

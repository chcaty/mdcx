"""Run real headless Avalonia input checks using only generated temporary media."""
import json
import shutil
import subprocess
import sys
from pathlib import Path

organizer = Path(__file__).resolve().parents[1]
dotnet = sys.argv[1] if len(sys.argv) > 1 else "dotnet"
fixture = json.loads(subprocess.check_output([sys.executable, str(organizer / "scripts/create_demo.py")], text=True))
source = Path(fixture["source"])
try:
    result = subprocess.run([
        dotnet, str(organizer / "src/Organizer.Desktop/bin/Release/net10.0/Organizer.Desktop.dll"),
        "--smoke-test-view", "--demo", str(source), "--python", sys.executable,
        *sys.argv[2:],
    ], check=False, timeout=90)
    raise SystemExit(result.returncode)
finally:
    # This directory was created by our generator in this invocation, never supplied by the caller.
    if source.parent.name.startswith("mdcx-organizer-demo-") and source.name == "incoming":
        shutil.rmtree(source.parent)

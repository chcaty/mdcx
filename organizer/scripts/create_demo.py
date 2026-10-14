#!/usr/bin/env python3
"""Create neutral placeholder files only in a new temporary directory."""
import json
import tempfile
from pathlib import Path

root = Path(tempfile.mkdtemp(prefix="mdcx-organizer-demo-"))
source = root / "incoming"
source.mkdir()
for name in ["DEMO-001.mkv", "DEMO-002.1080p.mp4", "DEMO-002.2160p.mp4", "旅行日记.mkv"]:
    (source / name).write_text("Synthetic fixture. This is not playable media.\n", encoding="utf-8")
(source / "DEMO-001.zh-Hans.srt").write_text("1\n00:00:01,000 --> 00:00:02,000\nNeutral demonstration subtitle.\n", encoding="utf-8")
print(json.dumps({"source": str(source), "target": str(root / "organized-preview")}, ensure_ascii=False))

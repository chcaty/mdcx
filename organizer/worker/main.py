#!/usr/bin/env python3
"""Run from any cwd: python3 /path/to/organizer/worker/main.py.

JSONL on stdin/stdout. Diagnostics only on stderr. Fixture mode by default.
See README.md and repository LICENSE/README for licensing notices.
"""
from __future__ import annotations

import asyncio
import argparse
import json
import sys

from mdcx_worker.models import WorkerError
from mdcx_worker.protocol import MAX_LINE_BYTES, Worker, parse_line, recover_request_id
from mdcx_worker.runtime import build_runtime


def read_frame() -> tuple[bytes, bool]:
    data = sys.stdin.buffer.readline(MAX_LINE_BYTES + 1)
    oversized = len(data) > MAX_LINE_BYTES
    if oversized and not data.endswith(b"\n"):
        # Resynchronize to the next line without accumulating unbounded input.
        while part := sys.stdin.buffer.readline(MAX_LINE_BYTES + 1):
            if part.endswith(b"\n"):
                break
    return data, oversized


def emit(event: dict):
    sys.stdout.write(json.dumps(event, ensure_ascii=True, allow_nan=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()


async def main(argv=None):
    parser = argparse.ArgumentParser(description="Read-only metadata JSONL worker; no file organization commands.")
    parser.add_argument("--metadata-config", help="Explicit source-settings JSON; network is disabled unless explicitly configured.")
    options = parser.parse_args(argv)
    settings = None
    if options.metadata_config:
        from mdcx_worker.source_settings import load_source_settings
        settings = load_source_settings(options.metadata_config)
    runtime = build_runtime(settings)
    message = ("deterministic fixture transport; live network disabled" if settings is None else
               "configured metadata provider; " + ("network explicitly enabled" if settings.network_enabled else "live network disabled"))
    print("metadata worker v1: " + message, file=sys.stderr, flush=True)
    worker = Worker(runtime.matcher, emit)
    try:
        while True:
            raw, oversized = await asyncio.to_thread(read_frame)
            if not raw:
                break
            try:
                if oversized:
                    raise WorkerError("request_too_large", "Request exceeds 16 KiB")
                command = parse_line(raw, allowed_sources=runtime.sources)
            except WorkerError as error:
                request_id = recover_request_id(raw)
                worker.protocol_error(request_id, error, terminal=request_id not in worker.seen)
                continue
            worker.submit(command)
        await worker.drain()
    finally:
        await runtime.aclose()


if __name__ == "__main__":
    if sys.version_info < (3, 12):
        print("Python 3.12 or newer is required", file=sys.stderr)
        raise SystemExit(2)
    try:
        asyncio.run(main())
    except WorkerError as error:
        emit({"version": 1, "requestId": "", "type": "startup_error", "terminal": True, "error": error.payload()})
        print(f"metadata worker startup failed: {error.code}: {error.message}", file=sys.stderr)
        raise SystemExit(2) from None
    except (BrokenPipeError, KeyboardInterrupt):
        raise SystemExit(130)

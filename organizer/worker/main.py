#!/usr/bin/env python3
"""Run from any cwd: python3 /path/to/organizer/worker/main.py.

JSONL on stdin/stdout. Diagnostics only on stderr. Fixture replay only.
See README.md and repository LICENSE/README for licensing notices.
"""
from __future__ import annotations

import asyncio
import json
import sys

from mdcx_worker.models import WorkerError
from mdcx_worker.protocol import MAX_LINE_BYTES, Worker, parse_line, recover_request_id
from mdcx_worker.scheduler import Scheduler
from mdcx_worker.service import Matcher
from mdcx_worker.transports import FixtureTransport


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


async def main():
    print("metadata worker v1: deterministic fixture transport; live network disabled", file=sys.stderr, flush=True)
    worker = Worker(Matcher(Scheduler(FixtureTransport())), emit)
    while True:
        raw, oversized = await asyncio.to_thread(read_frame)
        if not raw:
            break
        try:
            if oversized:
                raise WorkerError("request_too_large", "Request exceeds 16 KiB")
            command = parse_line(raw)
        except WorkerError as error:
            request_id = recover_request_id(raw)
            worker.protocol_error(request_id, error, terminal=request_id not in worker.seen)
            continue
        worker.submit(command)
    await worker.drain()


if __name__ == "__main__":
    if sys.version_info < (3, 12):
        print("Python 3.12 or newer is required", file=sys.stderr)
        raise SystemExit(2)
    try:
        asyncio.run(main())
    except (BrokenPipeError, KeyboardInterrupt):
        raise SystemExit(130)

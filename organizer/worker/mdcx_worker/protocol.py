"""Version 1 JSONL request validation and cancellation-safe lifecycle."""
from __future__ import annotations

import asyncio
import json
import re
import unicodedata
from collections.abc import Callable
from dataclasses import dataclass

from .adapters import DEFAULT_SOURCES
from .models import WorkerError
from .service import Matcher

MAX_LINE_BYTES = 16_384
MAX_REQUESTS_PER_SESSION = 8192
MAX_ACTIVE_REQUESTS = 32
REQUEST_ID = re.compile(r"[A-Za-z0-9_.:-]{1,128}\Z")


@dataclass(frozen=True)
class Command:
    request_id: str
    command: str
    query: str = ""
    sources: tuple[str, ...] = DEFAULT_SOURCES


def _unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate key")
        result[key] = value
    return result


def _reject_constant(_):
    raise ValueError("Nonfinite JSON number")


def parse_line(raw: bytes) -> Command:
    if len(raw) > MAX_LINE_BYTES:
        raise WorkerError("request_too_large", "Request exceeds 16 KiB")
    try:
        value = json.loads(raw, object_pairs_hook=_unique_object, parse_constant=_reject_constant)
    except (ValueError, UnicodeError, RecursionError) as exc:
        raise WorkerError("invalid_request", "Expected one valid UTF-8 JSON object") from exc
    if not isinstance(value, dict):
        raise WorkerError("invalid_request", "Expected a JSON object")
    if type(value.get("version")) is not int or value["version"] != 1:
        raise WorkerError("unsupported_version", "Protocol version 1 is required")
    request_id = value.get("requestId")
    if not isinstance(request_id, str) or not REQUEST_ID.fullmatch(request_id):
        raise WorkerError("invalid_request", "requestId must be 1–128 safe ASCII characters")
    command = value.get("command")
    if command == "cancel":
        if set(value) != {"version", "requestId", "command"}:
            raise WorkerError("invalid_request", "Cancel contains unsupported fields")
        return Command(request_id, command)
    if command != "match" or set(value) - {"version", "requestId", "command", "query", "sources"}:
        raise WorkerError("invalid_request", "Unsupported command or fields")
    query = value.get("query")
    if not isinstance(query, str) or not query.strip() or len(query) > 256 or any(unicodedata.category(c) in {"Cc", "Cs"} for c in query):
        raise WorkerError("invalid_request", "query must contain 1–256 printable characters")
    sources = value.get("sources", list(DEFAULT_SOURCES))
    if (not isinstance(sources, list) or not sources or len(sources) > len(DEFAULT_SOURCES)
        or any(not isinstance(source, str) or source not in DEFAULT_SOURCES for source in sources)
        or len(set(sources)) != len(sources)):
        raise WorkerError("invalid_sources", "sources must be a unique ordered list of migrated sources")
    return Command(request_id, command, query.strip(), tuple(sources))


def recover_request_id(raw: bytes) -> str:
    """Recover only validated IDs for schema errors; never echo arbitrary input."""
    if len(raw) > MAX_LINE_BYTES:
        return ""
    try:
        value = json.loads(raw, object_pairs_hook=_unique_object)
        candidate = value.get("requestId") if isinstance(value, dict) else None
        return candidate if isinstance(candidate, str) and REQUEST_ID.fullmatch(candidate) else ""
    except (ValueError, UnicodeError, RecursionError):
        return ""


@dataclass
class Running:
    task: asyncio.Task | None = None
    terminal: bool = False


class Worker:
    def __init__(self, matcher: Matcher, emit: Callable[[dict], None], *, request_timeout: float = 30.0,
                 max_active: int = MAX_ACTIVE_REQUESTS, max_requests: int = MAX_REQUESTS_PER_SESSION):
        self.matcher, self.emit = matcher, emit
        self.request_timeout, self.max_active, self.max_requests = request_timeout, max_active, max_requests
        self.active: dict[str, Running] = {}
        self.seen: set[str] = set()

    def event(self, request_id: str, event_type: str, **payload):
        self.emit({"version": 1, "requestId": request_id, "type": event_type, **payload})

    def protocol_error(self, request_id: str, error: WorkerError, *, terminal: bool = True):
        self.event(request_id, "error", error=error.payload(), terminal=terminal)

    def submit(self, command: Command):
        request_id = command.request_id
        if command.command == "cancel":
            state = self.active.get(request_id)
            if state is None or state.terminal:
                self.protocol_error(request_id, WorkerError("not_active", "Request is not active"), terminal=False)
                return
            # Emit the terminal acknowledgement here, even when cancellation occurs
            # before the coroutine gets its first turn. No post-terminal events.
            state.terminal = True
            self.event(request_id, "cancelled", terminal=True)
            state.task.cancel()
            return
        if request_id in self.seen:
            self.protocol_error(request_id, WorkerError("duplicate_request", "requestId cannot be reused in this session"), terminal=False)
            return
        if len(self.seen) >= self.max_requests:
            self.protocol_error(request_id, WorkerError("session_full", "Restart the worker with a fresh session"))
            return
        self.seen.add(request_id)
        if len(self.active) >= self.max_active:
            self.protocol_error(request_id, WorkerError("busy", "Too many active requests; retry with a new requestId", retryable=True))
            return
        state = Running()
        self.active[request_id] = state
        state.task = asyncio.create_task(self._run(command, state), name=f"match:{request_id}")
        state.task.add_done_callback(lambda _: self.active.pop(request_id, None))

    async def _run(self, command: Command, state: Running):
        request_id = command.request_id
        failures = 0

        def source_error(source: str, error: WorkerError):
            nonlocal failures
            if not state.terminal:
                failures += 1
                self.event(request_id, "error", source=source, error=error.payload(), terminal=False)

        try:
            async with asyncio.timeout(self.request_timeout):
                candidates = await self.matcher.match(command.query, command.sources, source_error)
            if state.terminal:
                return
            for candidate in candidates:
                self.event(request_id, "candidate", candidate=candidate.payload())
            self.event(request_id, "health", sources=self.matcher.scheduler.snapshot())
            state.terminal = True
            self.event(request_id, "completed", terminal=True, count=len(candidates),
                       status="matched" if candidates else ("sources_failed" if failures else "no_match"),
                       sourceFailures=failures)
        except asyncio.CancelledError:
            if not state.terminal:
                state.terminal = True
                self.event(request_id, "cancelled", terminal=True)
        except TimeoutError:
            if not state.terminal:
                state.terminal = True
                self.protocol_error(request_id, WorkerError("deadline", "Match request deadline exceeded", retryable=True))
        except Exception:  # noqa: BLE001 - process boundary must emit one safe terminal event without exception data
            if not state.terminal:
                state.terminal = True
                self.protocol_error(request_id, WorkerError("internal", "Worker failed safely"))

    async def drain(self):
        if self.active:
            await asyncio.gather(*(state.task for state in list(self.active.values())), return_exceptions=True)

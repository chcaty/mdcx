"""Bounded shared scheduling, per-attempt policy, retry, and source observability."""
from __future__ import annotations

import asyncio
import errno
import math
import random
import time
from collections.abc import Awaitable, Callable
from contextlib import asynccontextmanager
from dataclasses import asdict, dataclass
from datetime import UTC
from email.utils import parsedate_to_datetime
from urllib.parse import urlsplit

from .models import MAX_BODY_BYTES, Request, Response, Transport, WorkerError

TRANSIENT_STATUSES = {408, 429, 500, 502, 503, 504}


@dataclass
class Health:
    requests: int = 0
    successes: int = 0
    failures: int = 0
    retries: int = 0
    rateLimited: int = 0
    cancellations: int = 0
    inFlight: int = 0
    peakInFlight: int = 0
    matches: int = 0
    noMatches: int = 0
    parserFailures: int = 0
    latencyMs: int = 0
    lastError: str | None = None


@dataclass
class HostState:
    semaphore: asyncio.Semaphore
    gate: asyncio.Lock
    next_ready: float = 0.0
    cooldown: float = 0.0


def retry_after(headers: dict[str, str], now: float) -> float | None:
    value = next((v.strip() for k, v in headers.items() if k.lower() == "retry-after"), None)
    if value is None:
        return None
    try:
        if value.isdecimal():
            result = float(value)
        else:
            parsed = parsedate_to_datetime(value)
            if parsed.tzinfo is None:
                parsed = parsed.replace(tzinfo=UTC)
            result = max(0.0, parsed.timestamp() - now)
        return result if math.isfinite(result) else None
    except (ValueError, TypeError, OverflowError):
        return None


class Scheduler:
    def __init__(self, transport: Transport, *, global_limit: int = 4, per_host_limit: int = 2,
                 attempts: int = 3, timeout: float = 10.0, min_interval: float = 0.1,
                 max_retry_delay: float = 60.0, sleep: Callable[[float], Awaitable[None]] = asyncio.sleep,
                 monotonic: Callable[[], float] = time.monotonic, wall_time: Callable[[], float] = time.time,
                 jitter: Callable[[], float] = random.random):
        if global_limit < 1 or per_host_limit < 1 or attempts < 1 or timeout <= 0 or min_interval < 0:
            raise ValueError("Invalid scheduler limits")
        self.transport = transport
        self.global_slots = asyncio.Semaphore(global_limit)
        self.per_host_limit, self.attempts, self.timeout = per_host_limit, attempts, timeout
        self.min_interval, self.max_retry_delay = min_interval, max_retry_delay
        self.sleep, self.monotonic, self.wall_time, self.jitter = sleep, monotonic, wall_time, jitter
        self.hosts: dict[str, HostState] = {}
        self.health: dict[str, Health] = {}

    def counters(self, source: str) -> Health:
        return self.health.setdefault(source, Health())

    def snapshot(self) -> dict:
        return {source: asdict(value) for source, value in sorted(self.health.items())}

    @asynccontextmanager
    async def _admit(self, host: HostState):
        async with host.semaphore:
            while True:
                async with host.gate:
                    remaining = max(host.next_ready, host.cooldown) - self.monotonic()
                    if remaining <= 0:
                        await self.global_slots.acquire()
                        # Another in-flight response may have imposed a cooldown
                        # while this attempt waited for a global slot.
                        remaining = max(host.next_ready, host.cooldown) - self.monotonic()
                        if remaining <= 0:
                            host.next_ready = self.monotonic() + self.min_interval
                            break
                        self.global_slots.release()
                await self.sleep(remaining)
            try:
                yield
            finally:
                self.global_slots.release()

    async def fetch(self, request: Request) -> Response:
        hostname = urlsplit(request.url).hostname
        if not hostname:
            raise WorkerError("policy_blocked", "A valid source host is required")
        host = self.hosts.setdefault(hostname, HostState(asyncio.Semaphore(self.per_host_limit), asyncio.Lock()))
        health = self.counters(request.source)
        try:
            for attempt in range(self.attempts):
                response = None
                error = None
                try:
                    # Wait for this host before taking a global slot: one busy
                    # host cannot consume all global slots while merely queued.
                    async with self._admit(host):
                        health.requests += 1
                        health.inFlight += 1
                        health.peakInFlight = max(health.peakInFlight, health.inFlight)
                        started = self.monotonic()
                        try:
                            async with asyncio.timeout(self.timeout):
                                response = await self.transport.fetch(request)
                        finally:
                            health.inFlight -= 1
                            health.latencyMs += max(0, round((self.monotonic() - started) * 1000))
                    if len(response.body) > MAX_BODY_BYTES:
                        raise WorkerError("response_too_large", "Source body exceeds limit")
                    # Explicit challenge signals, including challenge pages with HTTP 200.
                    sample = response.body[:16384].lower()
                    if any(marker in sample for marker in (b"cf-chl-", b"g-recaptcha", b"hcaptcha", b"captcha challenge")):
                        raise WorkerError("challenge", "Source requires human verification")
                    if response.status == 200:
                        health.successes += 1
                        health.lastError = None
                        return response
                    if response.status == 404:
                        raise WorkerError("not_found", "Source has no matching record")
                    if response.status in (401, 403):
                        raise WorkerError("auth_required", "Source requires authorized access")
                    if 300 <= response.status < 400:
                        raise WorkerError("policy_blocked", "Redirects require a reviewed source route")
                    code = "rate_limited" if response.status == 429 else "http_error"
                    raise WorkerError(code, f"Source returned HTTP {response.status}", retryable=response.status in TRANSIENT_STATUSES)
                except TimeoutError:
                    error = WorkerError("timeout", "Source request timed out", retryable=True)
                except ConnectionError:
                    error = WorkerError("network", "Source connection failed", retryable=True)
                except OSError as exc:
                    transient = exc.errno in {errno.EAGAIN, errno.ETIMEDOUT, errno.ECONNRESET, errno.ECONNREFUSED, errno.ENETUNREACH, errno.EHOSTUNREACH}
                    error = WorkerError("network", "Source connection failed", retryable=transient)
                except WorkerError as exc:
                    error = exc
                health.failures += 1
                health.lastError = error.code
                if error.code == "rate_limited":
                    health.rateLimited += 1
                delay = retry_after(response.headers, self.wall_time()) if response else None
                # Respect a server cooldown even if this request is not retried.
                if delay is not None and error.retryable:
                    host.cooldown = max(host.cooldown, self.monotonic() + delay)
                if not error.retryable or attempt + 1 >= self.attempts:
                    raise error
                if delay is not None and delay > self.max_retry_delay:
                    # Never shorten Retry-After to our retry budget; surface it instead.
                    raise WorkerError(error.code, "Source cooldown exceeds this request's retry budget", retryable=True)
                health.retries += 1
                if delay is None:
                    delay = min(self.max_retry_delay, (0.5 * (2 ** attempt)) * (0.75 + self.jitter() / 2))
                # All permits released during backoff, and cancellation propagates.
                await self.sleep(delay)
            raise AssertionError("Unreachable retry state")
        except asyncio.CancelledError:
            health.cancellations += 1
            raise

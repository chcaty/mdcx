"""Explicit runtime composition; fixture mode never silently becomes network mode."""
from __future__ import annotations

from dataclasses import dataclass

from .adapters import DEFAULT_SOURCES
from .scheduler import Scheduler
from .service import Matcher
from .transports import FixtureTransport


@dataclass
class WorkerRuntime:
    matcher: object
    sources: tuple[str, ...]
    transport: object

    async def aclose(self):
        close = getattr(self.transport, "aclose", None)
        if close is not None:
            await close()


def build_runtime(settings=None, *, http_backend=None) -> WorkerRuntime:
    """HTTP injection is a Python test seam, never a JSONL/CLI capability.

    An explicit validated settings object selects the independent provider.
    Its network_enabled=False default still blocks before HTTP client creation.
    """
    if settings is None:
        transport = FixtureTransport()
        return WorkerRuntime(Matcher(Scheduler(transport)), DEFAULT_SOURCES, transport)
    from .http_transport import HttpTransport
    from .javbus_provider import JavBusProvider

    transport = HttpTransport(settings, transport=http_backend)
    scheduler = Scheduler(transport, **settings.scheduler_kwargs())
    return WorkerRuntime(JavBusProvider(scheduler, origin=settings.origin), ("javbus",), transport)

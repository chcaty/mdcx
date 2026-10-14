"""Transport seam: the shipped worker has NO live network transport.

A future HTTPS implementation must enforce the documented safety contract and
be explicitly injected through AccessControlledTransport. Nothing imports legacy
sessions, credentials, cookies, browser automation, or proxy configuration.
"""
from __future__ import annotations

import asyncio
import ipaddress
from collections.abc import Awaitable, Callable
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

from .models import MAX_BODY_BYTES, Request, Response, Transport, WorkerError, canonical


class FixtureTransport:
    def __init__(self, root: Path | None = None):
        root = root or Path(__file__).resolve().parent.parent / "fixtures"
        # Fixed files, bounded eagerly, no query-controlled paths or file reads.
        self.bodies = {}
        for source, name in {"xcity": "xcity-demo-001.html", "xcity_missing_date": "xcity-demo-006.html", "xcity_conflict": "xcity-conflicting-id.html", "theporndb_movies": "theporndb-movies.json"}.items():
            with (root / name).open("rb") as stream:
                body = stream.read(MAX_BODY_BYTES + 1)
            if len(body) > MAX_BODY_BYTES:
                raise WorkerError("response_too_large", "Fixture exceeds body limit")
            self.bodies[source] = body

    async def fetch(self, request: Request) -> Response:
        await asyncio.sleep(0)  # A real cancellation point, also in deterministic replay.
        url = urlsplit(request.url)
        expected_host = {"xcity": "xcity.example.invalid", "theporndb_movies": "theporndb.example.invalid"}.get(request.source)
        if url.hostname != expected_host or url.scheme != "https":
            raise WorkerError("policy_blocked", "Fixture route is not allowed")
        query = canonical(parse_qs(url.query).get("q", [""])[0])
        if request.source == "xcity" and query in {"demo006", "thequietobservatory"}:
            return Response(200, self.bodies["xcity_missing_date"], fixture=True)
        if request.source == "xcity" and query == "demo005":
            return Response(200, self.bodies["xcity_conflict"], fixture=True)
        if request.source == "xcity" and query not in {"demo001", "thelanternmap"}:
            return Response(404, fixture=True)
        if request.source == "theporndb_movies" and query not in {"demo001", "demo002", "demo003", "demo004", "demo005", "demo006", "thelanternmap", "harborofstars", "thelastbeacon", "thequietobservatory"}:
            return Response(200, b'{"data":[]}', fixture=True)
        return Response(200, self.bodies[request.source], {"Content-Type": "text/html" if request.source == "xcity" else "application/json"}, fixture=True)


class AccessControlledTransport:
    """Fail-closed guard for an independently reviewed future transport.

`authorize` must check current robots/terms/authorization for this exact URL.
There is deliberately no permissive default. The underlying implementation must
verify TLS, reject redirects, bound bodies before buffering, support cooperative
cancellation, and never load ambient credentials/cookies or bypass a challenge.
This guard is not a substitute for those transport-level responsibilities.
"""
    def __init__(self, inner: Transport, *, allowed_hosts: frozenset[str],
                 authorize: Callable[[Request], Awaitable[bool]]):
        self.inner, self.allowed_hosts, self.authorize = inner, allowed_hosts, authorize

    async def fetch(self, request: Request) -> Response:
        try:
            url = urlsplit(request.url)
            host = url.hostname or ""
            if url.scheme != "https" or host not in self.allowed_hosts or url.port not in (None, 443):
                raise ValueError("origin")
            if url.username or url.password or url.fragment or host in {"localhost", "localhost.localdomain"}:
                raise ValueError("credentials or local host")
            try:
                ipaddress.ip_address(host)
            except ValueError:
                pass
            else:
                raise ValueError("literal address")
        except ValueError as exc:
            raise WorkerError("policy_blocked", "Request origin is not allowed") from exc
        if not await self.authorize(request):
            raise WorkerError("policy_blocked", "Source access is not authorized by policy")
        return await self.inner.fetch(request)

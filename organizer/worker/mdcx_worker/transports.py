"""Only deterministic mocked HTTP replay is shipped; no socket implementation."""
from __future__ import annotations

import asyncio
import ipaddress
from collections.abc import Awaitable, Callable
from pathlib import Path
from urllib.parse import urlsplit

from .contracts import validate_provider_request
from .models import MAX_BODY_BYTES, Request, Response, Transport, WorkerError, canonical

# All paths are code-reviewed constants, never obtained from JSONL, a URL or query.
FIXTURE_FILES = (
    "xcity-search-001.html", "xcity-search-002.html", "xcity-search-005.html", "xcity-search-006.html",
    "xcity-search-empty.html", "xcity-demo-001.html", "xcity-demo-002.html", "xcity-demo-006.html",
    "xcity-conflicting-id.html", "theporndb-movies.json",
    "theporndb-search-lantern.json", "theporndb-search-harbor.json", "theporndb-search-beacon.json",
    "theporndb-search-mountain.json", "theporndb-search-observatory.json",
    "theporndb-detail-the-lantern-map.json", "theporndb-detail-harbor-of-stars.json",
    "theporndb-detail-the-last-beacon-2022.json", "theporndb-detail-the-last-beacon-2025.json",
    "theporndb-detail-the-mountain-letters.json", "theporndb-detail-the-quiet-observatory.json",
)
XCITY_SEARCH = {
    "demo001": "xcity-search-001.html", "thelanternmap": "xcity-search-001.html",
    "demo002": "xcity-search-002.html", "harborofstars": "xcity-search-002.html",
    "demo005": "xcity-search-005.html", "demo006": "xcity-search-006.html",
    "thequietobservatory": "xcity-search-006.html",
}
XCITY_DETAIL = {"1001": "xcity-demo-001.html", "1002": "xcity-demo-002.html",
                "1005": "xcity-conflicting-id.html", "1006": "xcity-demo-006.html"}
TPDB_SEARCH = {"thelanternmap": "theporndb-search-lantern.json", "harborofstars": "theporndb-search-harbor.json",
               "thelastbeacon": "theporndb-search-beacon.json", "themountainletters": "theporndb-search-mountain.json",
               "thequietobservatory": "theporndb-search-observatory.json"}
TPDB_DETAIL = {
    "the-lantern-map": "theporndb-detail-the-lantern-map.json",
    "harbor-of-stars": "theporndb-detail-harbor-of-stars.json",
    "the-last-beacon-2022": "theporndb-detail-the-last-beacon-2022.json",
    "the-last-beacon-2025": "theporndb-detail-the-last-beacon-2025.json",
    "the-mountain-letters": "theporndb-detail-the-mountain-letters.json",
    "the-quiet-observatory": "theporndb-detail-the-quiet-observatory.json",
}
# These are authored negative examples, not a fallback for arbitrary queries.
# The two TPDB-only fictional titles retain their explicit xcity no-result step.
EMPTY_SEARCH_QUERIES = {
    "xcity": frozenset({"absent999", "nosuchfictionalfilm", "thelastbeacon", "themountainletters"}),
    "theporndb_movies": frozenset({"nosuchfictionalfilm"}),
}


class FixtureTransport:
    def __init__(self):
        root = Path(__file__).resolve().parent.parent / "fixtures"
        self.bodies = {}
        self.calls: list[Request] = []
        for name in FIXTURE_FILES:
            with (root / name).open("rb") as stream:
                body = stream.read(MAX_BODY_BYTES + 1)
            if len(body) > MAX_BODY_BYTES:
                raise WorkerError("response_too_large", "Fixture exceeds body limit")
            self.bodies[name] = body

    async def fetch(self, request: Request) -> Response:
        await asyncio.sleep(0)
        kind, key = validate_provider_request(request)
        self.calls.append(request)
        if kind == "search" and canonical(key) in EMPTY_SEARCH_QUERIES[request.source]:
            body = self.bodies["xcity-search-empty.html"] if request.source == "xcity" else b'{"data":[]}'
            return Response(200, body, fixture=True)
        if request.source == "xcity":
            name = (XCITY_SEARCH.get(canonical(key)) if kind == "search"
                    else XCITY_DETAIL.get(key))
        else:
            name = TPDB_SEARCH.get(canonical(key)) if kind == "search" else TPDB_DETAIL.get(key)
        if name is None:
            raise WorkerError("live_unavailable", "Demo mode only contains authored offline examples; configure a metadata source for real queries")
        return Response(200, self.bodies[name],
                        {"Content-Type": "text/html" if request.source == "xcity" else "application/json"}, fixture=True)


class ReplayTransport:
    """Strict ordered request/response script; unexpected calls fail closed.

    Exact source, method, URL, headers and credential reference must match.
    Responses remain authored test data; this class has no HTTP fallback.
    """
    def __init__(self, expected: list[tuple[Request, Response]]):
        self.expected = list(expected)
        self.calls: list[Request] = []
        self.position = 0

    async def fetch(self, request: Request) -> Response:
        await asyncio.sleep(0)
        validate_provider_request(request)
        if self.position >= len(self.expected) or request != self.expected[self.position][0]:
            raise WorkerError("replay_mismatch", "Request does not match the next offline replay expectation")
        self.calls.append(request)
        response = self.expected[self.position][1]
        self.position += 1
        return response

    def assert_complete(self):
        if self.position != len(self.expected):
            raise AssertionError("Offline replay has unconsumed expected requests")


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

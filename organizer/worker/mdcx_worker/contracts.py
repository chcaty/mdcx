"""Reviewed historical request subset, for strict offline replay only.

These routes are not proof of current live schemas or authorization to access them.
No request executor in this package opens a network connection.
"""
from __future__ import annotations

import re
import unicodedata
from urllib.parse import parse_qsl, quote, urlsplit

from .models import ProviderIdentity, Request, WorkerError

XCITY_ORIGIN = "https://xcity.jp"
TPDB_ORIGIN = "https://api.theporndb.net"
TPDB_CREDENTIAL_REF = "fixture:theporndb_movies:nonsecret"
# Inert literal, never a real token, injected only into offline request objects.
TPDB_TEST_AUTH = "Bearer MDCX_OFFLINE_TEST_MARKER_NOT_A_CREDENTIAL"
TPDB_HEADERS = (
    ("Authorization", TPDB_TEST_AUTH),
    ("Content-Type", "application/json"),
    ("Accept", "application/json"),
    ("User-Agent", "MDCxOrganizer-OfflineContract/2"),
)


def valid_slug(value: object) -> bool:
    return isinstance(value, str) and bool(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}", value)) and ".." not in value


def detail_identity(source: str, url: str) -> ProviderIdentity:
    # Comparing the complete URL also rejects credentials, fragments, ports,
    # encoded separators, dot segments, duplicate query keys, and other hosts.
    if source == "xcity":
        match = re.fullmatch(re.escape(XCITY_ORIGIN) + r"/avod/detail/\?id=([1-9][0-9]{0,11})", url)
        if match:
            return ProviderIdentity(source, match[1])
    elif source == "theporndb_movies" and url.startswith(TPDB_ORIGIN + "/movies/"):
        slug = url.removeprefix(TPDB_ORIGIN + "/movies/")
        if valid_slug(slug):
            return ProviderIdentity(source, slug)
    raise WorkerError("policy_blocked", "Detail route is not in the reviewed offline contract")


def validate_provider_request(request: Request) -> tuple[str, str]:
    """Return (search, decoded query) or (detail, opaque provider identity)."""
    if request.method != "GET":
        raise WorkerError("policy_blocked", "Only reviewed read-only requests are allowed")
    expected_headers = TPDB_HEADERS if request.source == "theporndb_movies" else ()
    expected_ref = TPDB_CREDENTIAL_REF if request.source == "theporndb_movies" else None
    if request.headers != expected_headers or request.credential_ref != expected_ref:
        raise WorkerError("policy_blocked", "Request headers or credential reference violate the offline contract")
    prefix = {"xcity": XCITY_ORIGIN + "/result_published/?q=",
              "theporndb_movies": TPDB_ORIGIN + "/movies?q="}.get(request.source)
    if prefix and request.url.startswith(prefix):
        try:
            parts = urlsplit(request.url)
            pairs = parse_qsl(parts.query, keep_blank_values=True, strict_parsing=True, encoding="utf-8", errors="strict")
            query = pairs[0][1]
            suffix = "&per_page=100" if request.source == "theporndb_movies" else ""
            if (not query or len(query) > 256 or any(unicodedata.category(c) == "Cc" for c in query)
                or request.url != prefix + quote(query, safe="") + suffix):
                raise ValueError("noncanonical query")
            return "search", query
        except (ValueError, IndexError, UnicodeError) as exc:
            raise WorkerError("policy_blocked", "Search route is not in the reviewed offline contract") from exc
    return "detail", detail_identity(request.source, request.url).value

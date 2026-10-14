"""Immutable, explicit startup settings for the first independent HTTP source.

No environment, legacy configuration, credentials, or source-supplied URLs are
loaded. The only source origin is a code-reviewed constant. Merely loading this
module or a configuration file never enables or performs network access.
"""
from __future__ import annotations

import ipaddress
import json
import math
import re
from dataclasses import dataclass, field, fields
from os import PathLike
from pathlib import Path
from urllib.parse import urlsplit

from .models import MAX_BODY_BYTES, ProviderIdentity, Request, WorkerError

JAVBUS_ORIGIN = "https://www.javbus.com"
JAVBUS_CATALOG_PATTERN = r"[A-Z]{2,12}-[0-9]{1,12}[A-Z]?"
JAVBUS_DETAIL_PATTERN = r"[A-Za-z]{2,12}[-_]?[0-9]{1,12}[A-Za-z]?"
MAX_CONFIG_BYTES = 16_384


def _invalid_config() -> WorkerError:
    return WorkerError("invalid_config", "Metadata configuration is invalid")


def validate_javbus_origin(origin: str) -> str:
    """Return the single allowed exact HTTPS origin, without aliases or ports."""
    if not isinstance(origin, str) or origin != JAVBUS_ORIGIN:
        raise WorkerError("policy_blocked", "Source origin is not allowed")
    return origin


def javbus_detail_identity(url: str, *, origin: str = JAVBUS_ORIGIN) -> ProviderIdentity:
    """Identity is the exact source route token, not a normalized catalog ID."""
    validate_javbus_origin(origin)
    if isinstance(url, str):
        match = re.fullmatch(re.escape(origin) + "/(" + JAVBUS_DETAIL_PATTERN + ")", url)
        if match:
            return ProviderIdentity("javbus", match[1])
    raise WorkerError("policy_blocked", "Detail route is not allowed")


def validate_javbus_request(request: Request, *, origin: str = JAVBUS_ORIGIN) -> tuple[str, str]:
    """Allow only bounded catalog detail routes and the exact historical search.

    Complete URL matching deliberately rejects credentials, custom ports,
    queries, fragments, encoded separators, dot segments, and other origins.
    Headers and authentication cannot be supplied by the caller.
    """
    validate_javbus_origin(origin)
    if (request.source != "javbus" or request.method != "GET"
        or request.headers != () or request.credential_ref is not None):
        raise WorkerError("policy_blocked", "Request violates the source access policy")
    if not isinstance(request.url, str):
        raise WorkerError("policy_blocked", "Source route is not allowed")
    match = re.fullmatch(re.escape(origin) + "/search/(" + JAVBUS_CATALOG_PATTERN + ")&type=&parent=ce", request.url)
    if match:
        return "search", match[1]
    return "detail", javbus_detail_identity(request.url, origin=origin).value


def _validate_proxy(proxy: str | None) -> None:
    if proxy is None:
        return
    # This is an explicit routing setting, never read from *_PROXY variables.
    # Local proxies are allowed only when explicitly specified by the operator.
    if not isinstance(proxy, str) or len(proxy) > 512 or any(ord(c) <= 32 or ord(c) >= 127 for c in proxy):
        raise _invalid_config()
    try:
        parsed = urlsplit(proxy)
        host = parsed.hostname
        if (parsed.scheme not in {"http", "https"} or not host or parsed.username is not None
            or parsed.password is not None or parsed.path not in {"", "/"} or parsed.query or parsed.fragment
            or "?" in proxy or "#" in proxy or parsed.port is None or not 1 <= parsed.port <= 65535):
            raise ValueError
        try:
            ipaddress.ip_address(host)
        except ValueError:
            if (len(host) > 253 or not re.fullmatch(r"[A-Za-z0-9.-]+", host)
                or any(not re.fullmatch(r"[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?", label)
                       for label in host.split("."))):
                raise ValueError from None
    except ValueError:
        raise _invalid_config() from None


def _bounded_number(value: object, lower: float, upper: float, *, integer: bool = False) -> bool:
    if type(value) not in ({int} if integer else {int, float}):
        return False
    return lower <= value <= upper and math.isfinite(value)


@dataclass(frozen=True, slots=True)
class SourceSettings:
    """No arbitrary origin, bearer token, cookie, TLS-off, or redirect setting.

    ``request_timeout_seconds`` is the scheduler's per-attempt deadline, not
    the JSONL worker's independent overall matching deadline. HTTPX stage
    timeouts further bound connect, read/write, and pool waits. Retry/rate
    policy is enforced by composing this transport with the existing Scheduler.
    """

    source: str = "javbus"
    origin: str = JAVBUS_ORIGIN
    network_enabled: bool = False
    proxy: str | None = field(default=None, repr=False)
    connect_timeout_seconds: float = 5.0
    read_timeout_seconds: float = 10.0
    pool_timeout_seconds: float = 5.0
    request_timeout_seconds: float = 15.0
    max_body_bytes: int = MAX_BODY_BYTES
    global_limit: int = 2
    per_host_limit: int = 1
    attempts: int = 2
    min_interval_seconds: float = 1.0
    max_retry_delay_seconds: float = 30.0

    def __post_init__(self) -> None:
        if self.source != "javbus" or type(self.network_enabled) is not bool or self.origin != JAVBUS_ORIGIN:
            raise _invalid_config()
        for value in (self.connect_timeout_seconds, self.read_timeout_seconds, self.pool_timeout_seconds,
                      self.request_timeout_seconds):
            if not _bounded_number(value, 0.1, 60):
                raise _invalid_config()
        if (not _bounded_number(self.max_body_bytes, 1, MAX_BODY_BYTES, integer=True)
            or not _bounded_number(self.global_limit, 1, 4, integer=True)
            or not _bounded_number(self.per_host_limit, 1, 2, integer=True)
            or self.per_host_limit > self.global_limit
            or not _bounded_number(self.attempts, 1, 3, integer=True)
            or not _bounded_number(self.min_interval_seconds, 0.1, 60)
            or not _bounded_number(self.max_retry_delay_seconds, 0, 60)):
            raise _invalid_config()
        _validate_proxy(self.proxy)

    def scheduler_kwargs(self) -> dict[str, int | float]:
        return {
            "global_limit": self.global_limit,
            "per_host_limit": self.per_host_limit,
            "attempts": self.attempts,
            "timeout": self.request_timeout_seconds,
            "min_interval": self.min_interval_seconds,
            "max_retry_delay": self.max_retry_delay_seconds,
        }


def _unique_object(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result = {}
    for key, value in pairs:
        if key in result:
            raise _invalid_config()
        result[key] = value
    return result


def _invalid_constant(_: str) -> None:
    raise _invalid_config()


def load_source_settings(path: str | PathLike[str]) -> SourceSettings:
    """Load one explicitly selected, size-bounded UTF-8 JSON object.

    Unknown keys (including credentials/headers), duplicate keys, nonfinite
    numbers, and type coercion are rejected. Error messages never echo a path,
    configuration value, credential, or parser exception.
    """
    try:
        with Path(path).open("rb") as stream:
            content = stream.read(MAX_CONFIG_BYTES + 1)
        if len(content) > MAX_CONFIG_BYTES:
            raise _invalid_config()
        values = json.loads(content.decode("utf-8"), object_pairs_hook=_unique_object,
                            parse_constant=_invalid_constant)
        if type(values) is not dict or not set(values).issubset({item.name for item in fields(SourceSettings)}):
            raise _invalid_config()
        return SourceSettings(**values)
    except (OSError, ValueError, TypeError, UnicodeError, RecursionError, OverflowError):
        raise _invalid_config() from None

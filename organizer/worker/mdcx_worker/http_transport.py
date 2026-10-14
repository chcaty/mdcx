"""Optional, cancellable HTTP transport for an explicitly enabled source.

The executable's default fixture path does not import HTTPX. This transport
never discovers origins, loads ambient credentials, follows redirects, solves
challenges, or weakens TLS. Compose it with Scheduler for admission and retries.
Source permission/terms and production DNS/egress policy must be reviewed before
real use; a fixed hostname allowlist is not a DNS-rebinding firewall.
"""
from __future__ import annotations

import importlib
import logging
import ssl
from contextvars import ContextVar
from http.cookiejar import CookieJar, DefaultCookiePolicy
from typing import TYPE_CHECKING

from .models import Request, Response, WorkerError
from .source_settings import SourceSettings, validate_javbus_request

if TYPE_CHECKING:
    import httpx

USER_AGENT = "MDCxOrganizer-MetadataWorker/1.0"
HTTP_HEADERS = (
    ("User-Agent", USER_AGENT),
    ("Accept", "text/html,application/xhtml+xml"),
    # Reject compressed responses rather than buffering or inflating them.
    ("Accept-Encoding", "identity"),
)
# HTTPX otherwise emits the full requested URL at INFO, and httpcore may emit
# response headers at DEBUG. Suppression is context-local to this transport;
# unrelated requests in the host process keep their normal logging behavior.
_PRIVATE_HTTP_OPERATION: ContextVar[bool] = ContextVar("mdcx_private_http_operation", default=False)


class _PrivateOperationFilter(logging.Filter):
    def filter(self, record: logging.LogRecord) -> bool:
        return not _PRIVATE_HTTP_OPERATION.get()


_PRIVATE_FILTER = _PrivateOperationFilter()


def _protect_library_logs() -> None:
    # These are the loggers used by the pinned HTTPX/httpcore HTTP/1 path. Also
    # cover already loaded child loggers; no global logger levels are changed.
    names = {"httpx", "httpcore", "httpcore.connection", "httpcore.http11", "httpcore.http2",
             "httpcore.proxy", "httpcore.socks"}
    names.update(name for name in logging.Logger.manager.loggerDict
                 if name.startswith(("httpx.", "httpcore.")))
    for name in names:
        logging.getLogger(name).addFilter(_PRIVATE_FILTER)


class _RejectCookies(DefaultCookiePolicy):
    def set_ok(self, cookie, request) -> bool:
        return False

    def return_ok(self, cookie, request) -> bool:
        return False


def _tls_failure(error: BaseException) -> bool:
    seen = set()
    while error is not None and id(error) not in seen:
        if isinstance(error, ssl.SSLError):
            return True
        seen.add(id(error))
        error = error.__cause__ or error.__context__
    return False


def _response_headers(headers) -> dict[str, str]:
    # Do not retain Set-Cookie, Location, authentication challenges, server
    # details, or arbitrary reflected headers in the worker response contract.
    result = {}
    for name in ("content-type", "retry-after"):
        values = headers.get_list(name)
        if len(values) == 1 and len(values[0]) <= 256 and all(32 <= ord(c) < 127 for c in values[0]):
            result[name] = values[0]
    return result


async def _reject_redirects(response) -> None:
    # HTTPX builds a next_request even when follow_redirects=False. Reject in
    # a response hook before it parses an untrusted Location, so malformed
    # redirects cannot become retryable protocol errors.
    if 300 <= response.status_code < 400:
        raise WorkerError("policy_blocked", "Redirects require a reviewed source route")


class HttpTransport:
    """A lazy owned AsyncClient with an in-process transport seam for tests.

    ``transport`` is trusted code-only dependency injection, never JSONL or
    JSON configuration input. A proxy cannot be combined with an injected
    transport: HTTPX proxy mounts could otherwise bypass a mock and use sockets.
    Disabling network denies even injected requests, so tests must opt in using
    SourceSettings(network_enabled=True) together with MockTransport.
    """

    def __init__(self, settings: SourceSettings | None = None, *, transport: httpx.AsyncBaseTransport | None = None):
        self.settings = settings if settings is not None else SourceSettings()
        self._backend = transport
        self._client = None
        self._httpx = None
        self._closed = False
        if transport is not None and self.settings.proxy is not None:
            raise WorkerError("invalid_config", "A test HTTP transport cannot be combined with a proxy")

    def _ensure_client(self):
        if self._client is None:
            try:
                httpx = importlib.import_module("httpx")
            except ImportError:
                raise WorkerError("dependency_unavailable", "The optional HTTP dependency is unavailable") from None
            if httpx.__version__ != "0.28.1":
                raise WorkerError("dependency_unavailable", "The reviewed HTTP dependency version is unavailable")
            self._httpx = httpx
            _protect_library_logs()
            self._client = httpx.AsyncClient(
                verify=True,
                trust_env=False,
                follow_redirects=False,
                http1=True,
                http2=False,
                auth=None,
                event_hooks={"response": [_reject_redirects]},
                cookies=CookieJar(policy=_RejectCookies()),
                headers=HTTP_HEADERS,
                proxy=self.settings.proxy,
                transport=self._backend,
                timeout=httpx.Timeout(
                    connect=self.settings.connect_timeout_seconds,
                    read=self.settings.read_timeout_seconds,
                    write=self.settings.read_timeout_seconds,
                    pool=self.settings.pool_timeout_seconds,
                ),
                limits=httpx.Limits(
                    max_connections=self.settings.global_limit,
                    max_keepalive_connections=self.settings.global_limit,
                    keepalive_expiry=5.0,
                ),
            )
        return self._client

    async def fetch(self, request: Request) -> Response:
        if not self.settings.network_enabled:
            raise WorkerError("live_unavailable", "Metadata network access is disabled")
        if self._closed:
            raise WorkerError("policy_blocked", "The HTTP transport is closed")
        validate_javbus_request(request, origin=self.settings.origin)
        token = _PRIVATE_HTTP_OPERATION.set(True)
        try:
            client = self._ensure_client()
            httpx = self._httpx
            try:
                # A fresh Request, sent as-is, never merges a client cookie jar,
                # user-provided headers, proxy environment, or netrc credentials.
                outgoing = httpx.Request("GET", request.url, headers=HTTP_HEADERS)
                upstream = await client.send(outgoing, stream=True, auth=None, follow_redirects=False)
                try:
                    headers = _response_headers(upstream.headers)
                    if 300 <= upstream.status_code < 400:
                        # Scheduler classifies this as nonretryable policy_blocked.
                        # Do not read a redirect body or expose its Location.
                        return Response(upstream.status_code, headers=headers, fixture=self._backend is not None)
                    encodings = upstream.headers.get_list("content-encoding")
                    if encodings and (len(encodings) != 1 or encodings[0].strip().lower() != "identity"):
                        raise WorkerError("policy_blocked", "Compressed source responses are not supported")
                    lengths = upstream.headers.get_list("content-length")
                    expected_size = None
                    if lengths:
                        if (len(lengths) != 1 or not lengths[0].isascii() or not lengths[0].isdecimal()
                            or len(lengths[0]) > 16):
                            raise WorkerError("policy_blocked", "Source response framing is invalid")
                        expected_size = int(lengths[0])
                        if expected_size > self.settings.max_body_bytes:
                            raise WorkerError("response_too_large", "Source body exceeds limit")
                    if upstream.is_stream_consumed:
                        # MockTransport(content=...) constructs already-resident
                        # test data. The real transport must always stream.
                        if self._backend is None:
                            raise WorkerError("policy_blocked", "Source response was not streamed")
                        body = upstream.content
                        if len(body) > self.settings.max_body_bytes:
                            raise WorkerError("response_too_large", "Source body exceeds limit")
                    else:
                        body_buffer = bytearray()
                        async for chunk in upstream.aiter_raw():
                            # Check BEFORE extending the retained buffer. Raw
                            # iteration never invokes HTTPX content decompression.
                            if len(chunk) > self.settings.max_body_bytes - len(body_buffer):
                                raise WorkerError("response_too_large", "Source body exceeds limit")
                            body_buffer.extend(chunk)
                        body = bytes(body_buffer)
                    if expected_size is not None and len(body) != expected_size:
                        raise WorkerError("network", "Source response was incomplete", retryable=True)
                    # A challenge after the scheduler's initial sample must
                    # still prevent retries and false 404 search fallbacks.
                    sample = body.lower()
                    if any(marker in sample for marker in (
                        b"cf-chl-", b"g-recaptcha", b"hcaptcha", b"captcha challenge", b"challenge-platform",
                        b"<title>just a moment", b"verify you are human",
                    )):
                        raise WorkerError("challenge", "Source requires human verification")
                    if b"lostpasswd" in sample:
                        raise WorkerError("auth_required", "Source requires authorized access")
                    return Response(upstream.status_code, body, headers, fixture=self._backend is not None)
                finally:
                    await upstream.aclose()
            except httpx.TimeoutException:
                raise WorkerError("timeout", "Source request timed out", retryable=True) from None
            except (httpx.InvalidURL, httpx.UnsupportedProtocol, httpx.LocalProtocolError, httpx.TooManyRedirects):
                raise WorkerError("policy_blocked", "Source request was blocked by HTTP policy") from None
            except httpx.RequestError as error:
                if _tls_failure(error):
                    raise WorkerError("policy_blocked", "Source TLS verification failed") from None
                retryable = isinstance(error, httpx.NetworkError | httpx.RemoteProtocolError)
                raise WorkerError("network", "Source connection failed", retryable=retryable) from None
        finally:
            _PRIVATE_HTTP_OPERATION.reset(token)

    async def aclose(self) -> None:
        if self._closed:
            return
        self._closed = True
        if self._client is not None:
            token = _PRIVATE_HTTP_OPERATION.set(True)
            try:
                await self._client.aclose()
            finally:
                _PRIVATE_HTTP_OPERATION.reset(token)

    async def __aenter__(self) -> HttpTransport:
        return self

    async def __aexit__(self, exc_type, exc_value, traceback) -> None:
        await self.aclose()

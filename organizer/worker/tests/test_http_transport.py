import asyncio
import gzip
import importlib.util
import io
import logging
import ssl
import sys
import unittest
from dataclasses import replace
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from mdcx_worker.http_transport import HTTP_HEADERS, USER_AGENT, HttpTransport
from mdcx_worker.models import Request, WorkerError
from mdcx_worker.scheduler import Scheduler
from mdcx_worker.source_settings import JAVBUS_ORIGIN, SourceSettings

HAS_HTTPX = importlib.util.find_spec("httpx") is not None
if HAS_HTTPX:
    import httpx

    class StreamingBody(httpx.AsyncByteStream):
        def __init__(self, *chunks):
            self.chunks = chunks
            self.yielded = 0
            self.closed = False

        async def __aiter__(self):
            for chunk in self.chunks:
                self.yielded += 1
                yield chunk
                await asyncio.sleep(0)

        async def aclose(self):
            self.closed = True


REQUEST = Request("javbus", JAVBUS_ORIGIN + "/DEMO-001")


class DisabledTransportTests(unittest.IsolatedAsyncioTestCase):
    async def test_default_disabled_before_dependency_import_or_dns(self):
        with patch("socket.getaddrinfo", side_effect=AssertionError("DNS is forbidden")), \
                patch("mdcx_worker.http_transport.importlib.import_module") as load:
            transport = HttpTransport()
            with self.assertRaises(WorkerError) as error:
                await transport.fetch(REQUEST)
            self.assertEqual(error.exception.code, "live_unavailable")
            self.assertFalse(error.exception.retryable)
            self.assertIsNone(transport._client)
            load.assert_not_called()
            await transport.aclose()
            await transport.aclose()

    async def test_missing_optional_dependency_is_safe_and_does_not_install(self):
        transport = HttpTransport(SourceSettings(network_enabled=True))
        with patch("socket.getaddrinfo", side_effect=AssertionError("DNS is forbidden")), \
                patch("mdcx_worker.http_transport.importlib.import_module", side_effect=ImportError("private path")):
            with self.assertRaises(WorkerError) as error:
                await transport.fetch(REQUEST)
        self.assertEqual(error.exception.code, "dependency_unavailable")
        self.assertNotIn("private", str(error.exception))
        await transport.aclose()


@unittest.skipUnless(HAS_HTTPX, "Optional pinned HTTPX dependency is not installed")
class HttpTransportTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.backends = []
        for name in ("socket.getaddrinfo", "socket.create_connection", "socket.socket.connect", "socket.socket.connect_ex"):
            guard = patch(name, side_effect=AssertionError("No real sockets or DNS are permitted in HTTP tests"))
            guard.start()
            self.addCleanup(guard.stop)

    def make_transport(self, handler, **settings):
        transport = HttpTransport(SourceSettings(network_enabled=True, **settings), transport=httpx.MockTransport(handler))
        self.backends.append(transport)
        return transport

    async def asyncTearDown(self):
        for transport in self.backends:
            await transport.aclose()

    async def test_mock_fetch_owned_headers_raw_stream_and_fixture_provenance(self):
        observed = []
        body = StreamingBody(b"<html>", b"authored</html>")

        async def handler(request):
            observed.append(request)
            return httpx.Response(200, headers={"Content-Type": "text/html"}, stream=body)

        transport = self.make_transport(handler)
        response = await transport.fetch(REQUEST)
        self.assertEqual(response.status, 200)
        self.assertEqual(response.body, b"<html>authored</html>")
        self.assertTrue(response.fixture)
        self.assertEqual(response.headers, {"content-type": "text/html"})
        self.assertTrue(body.closed)
        self.assertEqual(len(observed), 1)
        outgoing = observed[0]
        self.assertEqual(str(outgoing.url), REQUEST.url)
        self.assertEqual(outgoing.method, "GET")
        self.assertEqual(dict(outgoing.headers), {"host": "www.javbus.com", **{k.lower(): v for k, v in HTTP_HEADERS}})
        self.assertEqual(outgoing.headers["user-agent"], USER_AGENT)
        self.assertNotIn("Mozilla", outgoing.headers["user-agent"])
        self.assertNotIn("cookie", outgoing.headers)
        self.assertNotIn("authorization", outgoing.headers)
        self.assertNotIn("proxy-authorization", outgoing.headers)

    async def test_prebuffered_mock_response_is_supported_and_bounded(self):
        transport = self.make_transport(lambda request: httpx.Response(200, content=b"1234"), max_body_bytes=4)
        self.assertEqual((await transport.fetch(REQUEST)).body, b"1234")
        too_large = self.make_transport(lambda request: httpx.Response(200, content=b"12345"), max_body_bytes=4)
        with self.assertRaises(WorkerError) as error:
            await too_large.fetch(REQUEST)
        self.assertEqual(error.exception.code, "response_too_large")

    async def test_network_disabled_denies_even_injected_backend(self):
        calls = []
        transport = HttpTransport(transport=httpx.MockTransport(lambda request: calls.append(request)))
        with self.assertRaises(WorkerError) as error:
            await transport.fetch(REQUEST)
        self.assertEqual(error.exception.code, "live_unavailable")
        self.assertEqual(calls, [])
        await transport.aclose()

    async def test_tls_environment_redirect_auth_and_timeout_configuration(self):
        seen = []
        real_client = httpx.AsyncClient
        with patch.dict("os.environ", {
            "HTTP_PROXY": "http://secret:password@127.0.0.1:9999",
            "HTTPS_PROXY": "http://secret:password@127.0.0.1:9999",
            "SSL_CERT_FILE": "/no/private/certificate.pem", "NETRC": "/no/private/netrc",
        }), patch.object(httpx, "AsyncClient", wraps=real_client) as create:
            transport = self.make_transport(lambda request: seen.append(request) or httpx.Response(200, content=b"ok"),
                                            connect_timeout_seconds=1, read_timeout_seconds=2, pool_timeout_seconds=3,
                                            global_limit=3)
            await transport.fetch(REQUEST)
            kwargs = create.call_args.kwargs
        self.assertIs(kwargs["verify"], True)
        self.assertIs(kwargs["trust_env"], False)
        self.assertIs(kwargs["follow_redirects"], False)
        self.assertIs(kwargs["auth"], None)
        self.assertIs(kwargs["http2"], False)
        self.assertIs(kwargs["proxy"], None)
        self.assertEqual(kwargs["timeout"].as_dict(), {"connect": 1, "read": 2, "write": 2, "pool": 3})
        self.assertEqual(kwargs["limits"].max_connections, 3)
        self.assertEqual(seen[0].extensions["timeout"], {"connect": 1, "read": 2, "write": 2, "pool": 3})
        self.assertNotIn("authorization", seen[0].headers)

    async def test_explicit_proxy_cannot_escape_mock_through_httpx_mount(self):
        with self.assertRaises(WorkerError) as error:
            HttpTransport(SourceSettings(network_enabled=True, proxy="http://127.0.0.1:8080"),
                          transport=httpx.MockTransport(lambda request: httpx.Response(200)))
        self.assertEqual(error.exception.code, "invalid_config")

    async def test_set_cookie_is_not_retained_or_sent_on_later_requests(self):
        seen = []

        def handler(request):
            seen.append(request)
            return httpx.Response(200, headers={"Set-Cookie": "private=secret; Domain=.javbus.com; Path=/; Secure"},
                                  content=b"ok")

        transport = self.make_transport(handler)
        await transport.fetch(REQUEST)
        await transport.fetch(replace(REQUEST, url=JAVBUS_ORIGIN + "/DEMO-002"))
        self.assertEqual(len(transport._client.cookies), 0)
        self.assertTrue(all("cookie" not in request.headers for request in seen))

    async def test_unreviewed_routes_headers_and_credentials_fail_before_client_creation(self):
        transport = self.make_transport(lambda request: self.fail("An invalid request reached HTTPX"))
        for change in ({"url": "https://127.0.0.1/DEMO-001"}, {"url": JAVBUS_ORIGIN + "/private?token=secret"},
                       {"headers": (("Authorization", "Bearer secret"),)}, {"credential_ref": "secret"},
                       {"method": "POST"}, {"source": "xcity"}):
            with self.subTest(change=change):
                with self.assertRaises(WorkerError) as error:
                    await transport.fetch(replace(REQUEST, **change))
                self.assertEqual(error.exception.code, "policy_blocked")
                self.assertNotIn("secret", str(error.exception))
                self.assertIsNone(transport._client)

    async def test_redirect_never_followed_or_buffered_and_scheduler_blocks(self):
        seen = []
        bodies = []

        def handler(request):
            seen.append(request)
            body = StreamingBody(b"private body that should never be read")
            bodies.append(body)
            return httpx.Response(302, headers={"Location": "http://127.0.0.1/private?token=secret"}, stream=body)

        transport = self.make_transport(handler)
        scheduler = Scheduler(transport, attempts=3, min_interval=0)
        with self.assertRaises(WorkerError) as error:
            await scheduler.fetch(REQUEST)
        self.assertEqual(error.exception.code, "policy_blocked")
        self.assertFalse(error.exception.retryable)
        self.assertEqual(len(seen), 1)
        self.assertEqual(bodies[0].yielded, 0)
        self.assertTrue(bodies[0].closed)
        self.assertNotIn("secret", str(error.exception))

    async def test_malformed_redirect_location_is_blocked_before_httpx_parses_it(self):
        calls = []
        body = StreamingBody(b"do not read")
        transport = self.make_transport(lambda request: calls.append(request) or httpx.Response(
            302, headers={"Location": "https://[private-invalid-host"}, stream=body))
        with self.assertRaises(WorkerError) as error:
            await Scheduler(transport, attempts=3, min_interval=0).fetch(REQUEST)
        self.assertEqual(error.exception.code, "policy_blocked")
        self.assertFalse(error.exception.retryable)
        self.assertEqual(len(calls), 1)
        self.assertEqual(body.yielded, 0)
        self.assertTrue(body.closed)

    async def test_declared_oversize_rejected_without_reading_stream(self):
        stream = StreamingBody(b"must not be consumed")
        transport = self.make_transport(lambda request: httpx.Response(200, headers={"Content-Length": "9"}, stream=stream),
                                        max_body_bytes=8)
        with self.assertRaises(WorkerError) as error:
            await transport.fetch(REQUEST)
        self.assertEqual(error.exception.code, "response_too_large")
        self.assertEqual(stream.yielded, 0)
        self.assertTrue(stream.closed)

    async def test_stream_bound_before_buffer_append_and_no_further_reads(self):
        stream = StreamingBody(b"1234", b"5678", b"9", b"must not be consumed")
        transport = self.make_transport(lambda request: httpx.Response(200, stream=stream), max_body_bytes=8)
        with self.assertRaises(WorkerError) as error:
            await transport.fetch(REQUEST)
        self.assertEqual(error.exception.code, "response_too_large")
        self.assertEqual(stream.yielded, 3)
        self.assertTrue(stream.closed)

    async def test_exact_stream_limit_is_accepted(self):
        stream = StreamingBody(b"1234", b"5678")
        transport = self.make_transport(lambda request: httpx.Response(200, stream=stream), max_body_bytes=8)
        self.assertEqual((await transport.fetch(REQUEST)).body, b"12345678")

    async def test_compression_bomb_rejected_before_read_or_decompression(self):
        bomb = gzip.compress(b"X" * 1_000_000)
        stream = StreamingBody(bomb)
        transport = self.make_transport(lambda request: httpx.Response(200, headers={"Content-Encoding": "gzip"},
                                                                      stream=stream), max_body_bytes=1024)
        with patch.object(httpx.Response, "_get_content_decoder", side_effect=AssertionError("No decoding is allowed")):
            with self.assertRaises(WorkerError) as error:
                await transport.fetch(REQUEST)
        self.assertEqual(error.exception.code, "policy_blocked")
        self.assertEqual(stream.yielded, 0)
        self.assertTrue(stream.closed)

    async def test_invalid_response_framing_is_safe(self):
        for value in ("-1", "many-secret", "1,1", "9" * 100, "12.0", "+5"):
            with self.subTest(value=value):
                stream = StreamingBody(b"private")
                transport = self.make_transport(lambda request, value=value, stream=stream: httpx.Response(200, headers={"Content-Length": value},
                                                                                                       stream=stream))
                with self.assertRaises(WorkerError) as error:
                    await transport.fetch(REQUEST)
                self.assertEqual(error.exception.code, "policy_blocked")
                self.assertEqual(stream.yielded, 0)
                self.assertNotIn("secret", str(error.exception))
        stream = StreamingBody(b"x")
        transport = self.make_transport(lambda request: httpx.Response(200, headers=[("Content-Length", "1"),
                                                                                    ("Content-Length", "1")], stream=stream))
        with self.assertRaises(WorkerError) as error:
            await transport.fetch(REQUEST)
        self.assertEqual(error.exception.code, "policy_blocked")

    async def test_incomplete_declared_body_is_retryable_network_error(self):
        transport = self.make_transport(lambda request: httpx.Response(200, headers={"Content-Length": "5"},
                                                                      stream=StreamingBody(b"x")))
        with self.assertRaises(WorkerError) as error:
            await transport.fetch(REQUEST)
        self.assertEqual(error.exception.code, "network")
        self.assertTrue(error.exception.retryable)

    async def test_response_headers_are_restricted(self):
        transport = self.make_transport(lambda request: httpx.Response(429, headers={
            "Retry-After": "2", "Content-Type": "text/html", "Set-Cookie": "secret=token",
            "Location": "https://secret.invalid", "Authorization": "Bearer secret", "X-Private": "secret",
        }, content=b""))
        self.assertEqual((await transport.fetch(REQUEST)).headers, {"content-type": "text/html", "retry-after": "2"})

    async def test_timeouts_network_errors_and_tls_redaction_taxonomy(self):
        cases = [
            (httpx.ConnectTimeout, "timeout", True), (httpx.ReadTimeout, "timeout", True),
            (httpx.WriteTimeout, "timeout", True), (httpx.PoolTimeout, "timeout", True),
            (httpx.ConnectError, "network", True), (httpx.ReadError, "network", True),
            (httpx.WriteError, "network", True), (httpx.RemoteProtocolError, "network", True),
            (httpx.LocalProtocolError, "policy_blocked", False), (httpx.UnsupportedProtocol, "policy_blocked", False),
            (httpx.ProxyError, "network", False), (httpx.TooManyRedirects, "policy_blocked", False),
        ]
        for error_type, code, retryable in cases:
            async def handler(request, error_type=error_type):
                raise error_type("private token and URL https://secret.invalid")
            with self.subTest(error_type=error_type):
                transport = self.make_transport(handler)
                with self.assertRaises(WorkerError) as error:
                    await transport.fetch(REQUEST)
                self.assertEqual(error.exception.code, code)
                self.assertEqual(error.exception.retryable, retryable)
                self.assertNotIn("secret", str(error.exception))
                self.assertNotIn("token", str(error.exception))

        async def bad_tls(request):
            try:
                raise ssl.SSLCertVerificationError("private certificate hostname")
            except ssl.SSLCertVerificationError as error:
                raise httpx.ConnectError("private endpoint") from error
        transport = self.make_transport(bad_tls)
        with self.assertRaises(WorkerError) as error:
            await transport.fetch(REQUEST)
        self.assertEqual(error.exception.code, "policy_blocked")
        self.assertFalse(error.exception.retryable)
        self.assertNotIn("private", str(error.exception))

    async def test_scheduler_handles_status_challenges_and_retry_after(self):
        for status, body, code in ((401, b"", "auth_required"), (403, b"", "auth_required"),
                                   (404, b"", "not_found"), (400, b"", "http_error"),
                                   (200, b'<div class="g-recaptcha">private</div>', "challenge")):
            with self.subTest(status=status, code=code):
                calls = []
                transport = self.make_transport(lambda request, calls=calls, status=status, body=body: calls.append(request) or httpx.Response(status, content=body))
                with self.assertRaises(WorkerError) as error:
                    await Scheduler(transport, attempts=3, min_interval=0).fetch(REQUEST)
                self.assertEqual(error.exception.code, code)
                self.assertEqual(len(calls), 1)

        calls, delays = [], []
        async def retry_sleep(delay):
            delays.append(delay)
            await asyncio.sleep(0)
        def limited(request):
            calls.append(request)
            return httpx.Response(429, headers={"Retry-After": "0"}) if len(calls) == 1 else httpx.Response(200, content=b"ok")
        transport = self.make_transport(limited)
        scheduler = Scheduler(transport, attempts=2, min_interval=0, sleep=retry_sleep)
        self.assertEqual((await scheduler.fetch(REQUEST)).body, b"ok")
        self.assertEqual(delays, [0])
        self.assertEqual(scheduler.counters("javbus").retries, 1)

    async def test_late_challenge_or_login_in_404_is_not_a_genuine_not_found(self):
        for marker, expected in ((b"challenge-platform", "challenge"), (b"lostpasswd", "auth_required")):
            with self.subTest(marker=marker):
                calls = []
                transport = self.make_transport(lambda request, calls=calls, marker=marker: calls.append(request) or httpx.Response(
                    404, stream=StreamingBody(b" " * 20000, marker)))
                with self.assertRaises(WorkerError) as error:
                    await Scheduler(transport, attempts=3, min_interval=0).fetch(REQUEST)
                self.assertEqual(error.exception.code, expected)
                self.assertFalse(error.exception.retryable)
                self.assertEqual(len(calls), 1)

    async def test_scheduler_timeout_cancels_stream_and_releases_permits(self):
        class BlockedStream(httpx.AsyncByteStream):
            closed = False
            async def __aiter__(self):
                yield b"x"
                await asyncio.Event().wait()
            async def aclose(self):
                self.closed = True
        stream = BlockedStream()
        transport = self.make_transport(lambda request: httpx.Response(200, stream=stream))
        scheduler = Scheduler(transport, attempts=1, min_interval=0, timeout=0.01)
        with self.assertRaises(WorkerError) as error:
            await scheduler.fetch(REQUEST)
        self.assertEqual(error.exception.code, "timeout")
        self.assertTrue(stream.closed)
        self.assertEqual(scheduler.counters("javbus").inFlight, 0)

    async def test_cancellation_during_connection_is_not_mapped_or_retried(self):
        started = asyncio.Event()
        calls = []
        async def handler(request):
            calls.append(request)
            started.set()
            await asyncio.Event().wait()
        transport = self.make_transport(handler)
        scheduler = Scheduler(transport, attempts=3, min_interval=0)
        task = asyncio.create_task(scheduler.fetch(REQUEST))
        await started.wait()
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        self.assertEqual(len(calls), 1)
        self.assertEqual(scheduler.counters("javbus").cancellations, 1)
        self.assertEqual(scheduler.counters("javbus").inFlight, 0)

    async def test_cancellation_closes_stream_then_same_transport_can_be_reused(self):
        started = asyncio.Event()
        class BlockedStream(httpx.AsyncByteStream):
            closed = False
            async def __aiter__(self):
                yield b"first"
                started.set()
                await asyncio.Event().wait()
            async def aclose(self):
                self.closed = True
        blocked = BlockedStream()
        replies = [httpx.Response(200, stream=blocked), httpx.Response(200, content=b"second")]
        transport = self.make_transport(lambda request: replies.pop(0))
        task = asyncio.create_task(transport.fetch(REQUEST))
        await started.wait()
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        self.assertTrue(blocked.closed)
        self.assertEqual((await transport.fetch(REQUEST)).body, b"second")

    async def test_context_scoped_library_logging_redacts_url_and_headers(self):
        capture = io.StringIO()
        handler = logging.StreamHandler(capture)
        loggers = [logging.getLogger("httpx"), logging.getLogger("httpcore.http11")]
        previous = [(logger.level, logger.propagate) for logger in loggers]
        for logger in loggers:
            logger.addHandler(handler)
            logger.setLevel(logging.DEBUG)
            logger.propagate = False
        try:
            def mock(request):
                logging.getLogger("httpcore.http11").debug("private token from response headers")
                return httpx.Response(200, content=b"private response")
            transport = self.make_transport(mock)
            await transport.fetch(REQUEST)
            logging.getLogger("httpx").info("unrelated operation is still logged")
            self.assertEqual(capture.getvalue().strip(), "unrelated operation is still logged")
        finally:
            for logger, (level, propagate) in zip(loggers, previous, strict=True):
                logger.removeHandler(handler)
                logger.setLevel(level)
                logger.propagate = propagate

    async def test_close_is_idempotent_and_disallows_new_fetch(self):
        transport = self.make_transport(lambda request: httpx.Response(200, content=b"ok"))
        async with transport:
            await transport.fetch(REQUEST)
        await transport.aclose()
        self.assertTrue(transport._client.is_closed)
        with self.assertRaises(WorkerError) as error:
            await transport.fetch(REQUEST)
        self.assertEqual(error.exception.code, "policy_blocked")


if __name__ == "__main__":
    unittest.main()

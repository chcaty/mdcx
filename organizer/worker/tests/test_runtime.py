"""Composition checks use injected HTTP only; no provider is contacted."""
from __future__ import annotations

import asyncio
import json
import socket
import sys
import tempfile
import unittest
from contextlib import contextmanager
from pathlib import Path
from unittest.mock import patch

import httpx

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from mdcx_worker.models import WorkerError  # noqa: E402 - direct discovery adds the worker root above
from mdcx_worker.protocol import Worker, parse_line  # noqa: E402
from mdcx_worker.runtime import build_runtime  # noqa: E402
from mdcx_worker.source_settings import SourceSettings  # noqa: E402

DETAIL = (ROOT / "fixtures/javbus/detail-demo-001.html").read_bytes()


@contextmanager
def no_network():
    def forbidden(*args, **kwargs):
        raise AssertionError("Unexpected external network operation")
    with (patch.object(socket.socket, "__new__", side_effect=forbidden) as sockets,
          patch("socket.create_connection", side_effect=forbidden) as connections,
          patch("socket.getaddrinfo", side_effect=forbidden) as dns):
        yield
        for guard in (sockets, connections, dns):
            guard.assert_not_called()


def frame(query, request_id="runtime-1", **kwargs):
    return json.dumps({"version": 1, "command": "match", "requestId": request_id,
                       "query": query, **kwargs}).encode()


class RuntimeTests(unittest.IsolatedAsyncioTestCase):
    async def exercise(self, runtime, query):
        events = []
        worker = Worker(runtime.matcher, events.append)
        worker.submit(parse_line(frame(query), allowed_sources=runtime.sources))
        await worker.drain()
        return events

    async def test_default_composition_stays_fixture_only(self):
        with no_network():
            runtime = build_runtime()
            try:
                self.assertEqual(runtime.sources, ("xcity", "theporndb_movies"))
                events = await self.exercise(runtime, "BF-658")
            finally:
                await runtime.aclose()
        self.assertEqual(events[-1]["status"], "sources_failed")
        self.assertIn("live_unavailable", [e["error"]["code"] for e in events if e["type"] == "error"])

    async def test_explicit_provider_config_is_disabled_by_default(self):
        calls = []
        backend = httpx.MockTransport(lambda request: calls.append(request))
        with no_network():
            runtime = build_runtime(SourceSettings(), http_backend=backend)
            try:
                events = await self.exercise(runtime, "BF-658")
            finally:
                await runtime.aclose()
        self.assertEqual(calls, [])
        self.assertEqual(events[-1]["status"], "sources_failed")
        self.assertEqual(events[0]["source"], "javbus")
        self.assertEqual(events[0]["error"]["code"], "live_unavailable")

    async def test_configured_composition_parses_neutral_http_fixture_to_candidate(self):
        calls = []
        def respond(request):
            calls.append(request)
            return httpx.Response(200, content=DETAIL, headers={"Content-Type": "text/html"})
        with no_network():
            runtime = build_runtime(SourceSettings(network_enabled=True), http_backend=httpx.MockTransport(respond))
            try:
                events = await self.exercise(runtime, "DEMO-001")
            finally:
                await runtime.aclose()
        self.assertEqual([str(r.url) for r in calls], ["https://www.javbus.com/DEMO-001"])
        candidate = next(e["candidate"] for e in events if e["type"] == "candidate")
        self.assertEqual(candidate["source"], "javbus")
        self.assertEqual(candidate["catalogIdentifier"], {"namespace": "javbus:catalog", "value": "DEMO-001"})
        self.assertEqual(candidate["title"], "The Lantern Map")
        self.assertEqual(candidate["year"], 2024)
        self.assertTrue(candidate["requiresReview"])
        self.assertEqual(candidate["provenance"]["fixture"], "true")
        self.assertEqual(candidate["provenance"]["responseOrigin"], "synthetic_uncaptured")
        self.assertEqual(events[-1]["status"], "matched")

    async def test_bf658_transmission_never_creates_an_invented_online_match(self):
        calls = []
        def respond(request):
            calls.append(request)
            # Deliberately neutral error page: no real metadata is invented.
            return httpx.Response(200, content=b"<html><title>Neutral test error</title></html>")
        with no_network():
            runtime = build_runtime(SourceSettings(network_enabled=True), http_backend=httpx.MockTransport(respond))
            try:
                events = await self.exercise(runtime, "BF-658")
            finally:
                await runtime.aclose()
        self.assertEqual([str(r.url) for r in calls], ["https://www.javbus.com/BF-658"])
        self.assertEqual(events[-1]["status"], "sources_failed")
        self.assertFalse(any(e["type"] == "candidate" for e in events))
        self.assertIn("schema_drift", [e["error"]["code"] for e in events if e["type"] == "error"])

    async def test_configured_source_registry_cannot_silently_select_a_demo_provider(self):
        with self.assertRaises(WorkerError) as error:
            parse_line(frame("DEMO-001", sources=["xcity"]), allowed_sources=("javbus",))
        self.assertEqual(error.exception.code, "invalid_sources")

    async def test_cancellation_propagates_through_injected_http_and_closes_runtime(self):
        entered = asyncio.Event()
        cancelled = asyncio.Event()
        async def respond(request):
            entered.set()
            try:
                await asyncio.sleep(30)
            except asyncio.CancelledError:
                cancelled.set()
                raise
        with no_network():
            runtime = build_runtime(SourceSettings(network_enabled=True), http_backend=httpx.MockTransport(respond))
            events = []
            worker = Worker(runtime.matcher, events.append)
            try:
                worker.submit(parse_line(frame("BF-658"), allowed_sources=runtime.sources))
                await asyncio.wait_for(entered.wait(), 2)
                worker.submit(parse_line(b'{"version":1,"requestId":"runtime-1","command":"cancel"}', allowed_sources=runtime.sources))
                await asyncio.wait_for(worker.drain(), 2)
                self.assertTrue(cancelled.is_set())
                self.assertEqual([e["type"] for e in events], ["cancelled"])
            finally:
                await runtime.aclose()

    async def test_real_entrypoint_with_disabled_config_uses_selected_provider_without_network(self):
        # Import the same guard that already protects actual entrypoint tests.
        from test_cli import NETWORK_GUARDED_ENTRYPOINT
        with tempfile.TemporaryDirectory(prefix="metadata-config-cli-") as folder:
            config = Path(folder) / "source.json"
            config.write_text('{"source":"javbus"}', encoding="utf-8")
            process = await asyncio.create_subprocess_exec(
                sys.executable, "-B", "-c", NETWORK_GUARDED_ENTRYPOINT, str(ROOT / "main.py"),
                "--metadata-config", str(config), cwd=folder,
                stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
            )
            stdout, stderr = await asyncio.wait_for(process.communicate(frame("BF-658") + b"\n"), 5)
        self.assertEqual(process.returncode, 0, stderr.decode())
        events = [json.loads(line) for line in stdout.splitlines()]
        self.assertEqual(events[0]["source"], "javbus")
        self.assertEqual(events[0]["error"]["code"], "live_unavailable")
        self.assertEqual(events[-1]["status"], "sources_failed")
        self.assertIn(b"configured metadata provider", stderr)
        self.assertIn(b"zero external socket, connection, DNS or HTTP calls", stderr)

    async def test_invalid_startup_configuration_emits_safe_structured_failure(self):
        with tempfile.TemporaryDirectory(prefix="metadata-invalid-config-") as folder:
            config = Path(folder) / "source.json"
            config.write_text('{"password":"synthetic-value-must-not-be-echoed"}', encoding="utf-8")
            process = await asyncio.create_subprocess_exec(
                sys.executable, "-B", str(ROOT / "main.py"), "--metadata-config", str(config),
                cwd=folder, stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE,
            )
            stdout, stderr = await asyncio.wait_for(process.communicate(), 5)
        self.assertEqual(process.returncode, 2)
        event = json.loads(stdout)
        self.assertEqual((event["requestId"], event["type"], event["error"]["code"]),
                         ("", "startup_error", "invalid_config"))
        self.assertTrue(event["terminal"])
        self.assertFalse(event["error"]["retryable"])
        self.assertNotIn(b"synthetic-value", stdout + stderr)
        self.assertNotIn(b"Traceback", stderr)


if __name__ == "__main__":
    unittest.main()

import asyncio
import errno
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from mdcx_worker.models import MAX_BODY_BYTES, Request, Response, WorkerError
from mdcx_worker.scheduler import Scheduler, retry_after
from mdcx_worker.transports import AccessControlledTransport


class SequenceTransport:
    def __init__(self, *values):
        self.values = list(values)
        self.calls = []

    async def fetch(self, request):
        self.calls.append(request)
        value = self.values.pop(0)
        if isinstance(value, Exception):
            raise value
        return value


class FakeClock:
    def __init__(self):
        self.value = 1000.0
        self.sleeps = []

    async def sleep(self, seconds):
        self.sleeps.append(seconds)
        self.value += seconds
        await asyncio.sleep(0)

    def now(self):
        return self.value


class RetryTests(unittest.IsolatedAsyncioTestCase):
    def scheduler(self, transport, **kwargs):
        self.clock = FakeClock()
        return Scheduler(transport, min_interval=0, sleep=self.clock.sleep,
                         monotonic=self.clock.now, wall_time=self.clock.now, jitter=lambda: 0.5, **kwargs)

    async def test_retry_after_seconds_and_per_attempt_counts(self):
        transport = SequenceTransport(Response(429, headers={"rEtRy-AfTeR": "5"}), Response(200))
        scheduler = self.scheduler(transport)
        await scheduler.fetch(Request("source", "https://example.invalid/a"))
        self.assertEqual(self.clock.sleeps, [5])
        health = scheduler.snapshot()["source"]
        self.assertEqual((health["requests"], health["retries"], health["rateLimited"], health["successes"]), (2, 1, 1, 1))
        self.assertEqual(health["inFlight"], 0)

    async def test_retry_after_http_date(self):
        transport = SequenceTransport(Response(503, headers={"Retry-After": "Thu, 01 Jan 1970 00:16:45 GMT"}), Response(200))
        scheduler = self.scheduler(transport)
        await scheduler.fetch(Request("source", "https://example.invalid/a"))
        self.assertEqual(self.clock.sleeps, [5])

    async def test_long_cooldown_is_not_shortened(self):
        transport = SequenceTransport(Response(429, headers={"Retry-After": "120"}), Response(200))
        scheduler = self.scheduler(transport)
        with self.assertRaises(WorkerError) as caught:
            await scheduler.fetch(Request("source", "https://example.invalid/a"))
        self.assertEqual(caught.exception.code, "rate_limited")
        self.assertEqual(len(transport.calls), 1)
        self.assertEqual(self.clock.sleeps, [])
        await scheduler.fetch(Request("source", "https://example.invalid/b"))
        self.assertEqual(self.clock.sleeps, [120])

    async def test_transient_connection_error_retries(self):
        transport = SequenceTransport(ConnectionError("private endpoint data"), Response(200))
        scheduler = self.scheduler(transport)
        await scheduler.fetch(Request("source", "https://example.invalid/a"))
        self.assertEqual(len(transport.calls), 2)
        self.assertEqual(self.clock.sleeps, [0.5])

    async def test_nontransient_operating_system_error_not_retried(self):
        transport = SequenceTransport(PermissionError(errno.EACCES, "secret filename"))
        scheduler = self.scheduler(transport)
        with self.assertRaises(WorkerError) as caught:
            await scheduler.fetch(Request("source", "https://example.invalid/a"))
        self.assertFalse(caught.exception.retryable)
        self.assertNotIn("secret", caught.exception.message)
        self.assertEqual(len(transport.calls), 1)

    async def test_status_taxonomy_and_no_permanent_retries(self):
        for status, code in ((400, "http_error"), (401, "auth_required"), (403, "auth_required"), (404, "not_found"), (302, "policy_blocked")):
            with self.subTest(status=status):
                transport = SequenceTransport(Response(status))
                scheduler = self.scheduler(transport)
                with self.assertRaises(WorkerError) as caught:
                    await scheduler.fetch(Request("source", "https://example.invalid/a"))
                self.assertEqual(caught.exception.code, code)
                self.assertFalse(caught.exception.retryable)
                self.assertEqual(len(transport.calls), 1)

    async def test_503_exhaustion_is_bounded(self):
        transport = SequenceTransport(Response(503), Response(503), Response(503))
        scheduler = self.scheduler(transport)
        with self.assertRaises(WorkerError):
            await scheduler.fetch(Request("source", "https://example.invalid/a"))
        self.assertEqual(len(transport.calls), 3)
        self.assertEqual(self.clock.sleeps, [0.5, 1.0])

    async def test_challenge_not_retried_even_with_200(self):
        transport = SequenceTransport(Response(200, b'<div class="g-recaptcha">private</div>'))
        scheduler = self.scheduler(transport)
        with self.assertRaises(WorkerError) as caught:
            await scheduler.fetch(Request("source", "https://example.invalid/a"))
        self.assertEqual(caught.exception.code, "challenge")
        self.assertEqual(len(transport.calls), 1)

    async def test_oversized_response_rejected(self):
        scheduler = self.scheduler(SequenceTransport(Response(200, b"x" * (MAX_BODY_BYTES + 1))))
        with self.assertRaises(WorkerError) as caught:
            await scheduler.fetch(Request("source", "https://example.invalid/a"))
        self.assertEqual(caught.exception.code, "response_too_large")

    async def test_timeout_retries_only_bounded_attempts(self):
        class SlowTransport:
            async def fetch(self, request):
                await asyncio.sleep(10)
        scheduler = self.scheduler(SlowTransport(), timeout=0.005, attempts=2)
        with self.assertRaises(WorkerError) as caught:
            await scheduler.fetch(Request("source", "https://example.invalid/a"))
        self.assertEqual(caught.exception.code, "timeout")
        self.assertEqual(scheduler.counters("source").requests, 2)
        self.assertEqual(scheduler.counters("source").inFlight, 0)

    async def test_cancellation_during_backoff_releases_permits(self):
        entered = asyncio.Event()
        async def sleep(_):
            entered.set()
            await asyncio.Event().wait()
        scheduler = Scheduler(SequenceTransport(Response(429), Response(200)), min_interval=0, sleep=sleep)
        task = asyncio.create_task(scheduler.fetch(Request("source", "https://example.invalid/a")))
        await entered.wait()
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        self.assertEqual(scheduler.counters("source").inFlight, 0)
        self.assertEqual(scheduler.counters("source").cancellations, 1)
        # The same host remains usable after cancellation.
        await scheduler.fetch(Request("source", "https://example.invalid/b"))

    async def test_new_cooldown_is_rechecked_after_global_queue(self):
        entered, release = asyncio.Event(), asyncio.Event()
        clock = FakeClock()
        starts = []
        class CooldownTransport:
            async def fetch(self, request):
                starts.append(clock.now())
                if len(starts) == 1:
                    entered.set()
                    await release.wait()
                    return Response(429, headers={"Retry-After": "2"})
                return Response(200)
        scheduler = Scheduler(CooldownTransport(), global_limit=1, per_host_limit=2,
                              attempts=1, min_interval=0, sleep=clock.sleep,
                              monotonic=clock.now, wall_time=clock.now)
        first = asyncio.create_task(scheduler.fetch(Request("source", "https://a.invalid/one")))
        await entered.wait()
        second = asyncio.create_task(scheduler.fetch(Request("source", "https://a.invalid/two")))
        await asyncio.sleep(0)  # Let the second attempt queue on the global permit.
        release.set()
        with self.assertRaises(WorkerError):
            await first
        await second
        self.assertEqual(starts, [1000.0, 1002.0])
        self.assertEqual(clock.sleeps, [2.0])

    async def test_cancel_queued_request_does_not_leak_slots(self):
        started, release = asyncio.Event(), asyncio.Event()
        class HeldTransport:
            async def fetch(self, request):
                started.set()
                await release.wait()
                return Response(200)
        scheduler = Scheduler(HeldTransport(), global_limit=1, per_host_limit=1, min_interval=0)
        first = asyncio.create_task(scheduler.fetch(Request("first", "https://a.invalid/one")))
        await started.wait()
        host_queued = asyncio.create_task(scheduler.fetch(Request("first", "https://a.invalid/two")))
        global_queued = asyncio.create_task(scheduler.fetch(Request("second", "https://b.invalid/three")))
        await asyncio.sleep(0)
        host_queued.cancel()
        global_queued.cancel()
        for task in (host_queued, global_queued):
            with self.assertRaises(asyncio.CancelledError):
                await task
        release.set()
        await first
        await scheduler.fetch(Request("second", "https://b.invalid/four"))
        self.assertEqual(scheduler.counters("first").inFlight, 0)
        self.assertEqual(scheduler.counters("second").inFlight, 0)
        self.assertEqual(scheduler.counters("first").cancellations, 1)
        self.assertEqual(scheduler.counters("second").cancellations, 1)

    async def test_global_and_per_host_concurrency(self):
        class CountingTransport:
            def __init__(self):
                self.active = self.peak = 0
                self.hosts, self.peaks = {}, {}
            async def fetch(self, request):
                from urllib.parse import urlsplit
                host = urlsplit(request.url).hostname
                self.active += 1
                self.peak = max(self.peak, self.active)
                self.hosts[host] = self.hosts.get(host, 0) + 1
                self.peaks[host] = max(self.peaks.get(host, 0), self.hosts[host])
                try:
                    await asyncio.sleep(0.005)
                    return Response(200)
                finally:
                    self.active -= 1
                    self.hosts[host] -= 1
        transport = CountingTransport()
        scheduler = Scheduler(transport, global_limit=3, per_host_limit=2, min_interval=0)
        await asyncio.gather(*(scheduler.fetch(Request(f"s{i % 3}", f"https://h{i % 3}.invalid/item")) for i in range(30)))
        self.assertEqual(transport.peak, 3)
        self.assertTrue(all(value <= 2 for value in transport.peaks.values()))
        self.assertEqual(transport.active, 0)
        self.assertEqual(sum(h.requests for h in scheduler.health.values()), 30)

    async def test_rate_gate_applies_to_every_attempt(self):
        transport = SequenceTransport(Response(503), Response(200))
        scheduler = self.scheduler(transport)
        scheduler.min_interval = 2
        await scheduler.fetch(Request("source", "https://example.invalid/a"))
        self.assertEqual(self.clock.sleeps, [0.5, 1.5])


class HeaderTests(unittest.TestCase):
    def test_retry_after_invalid_negative_past(self):
        for value in ("abc", "-1", "1.5", "NaN", "Infinity"):
            self.assertIsNone(retry_after({"Retry-After": value}, 1000))
        self.assertEqual(retry_after({"Retry-After": "Thu, 01 Jan 1970 00:00:00 GMT"}, 1000), 0)
        self.assertEqual(retry_after({"Retry-After": "0"}, 1000), 0)


class PolicyTests(unittest.IsolatedAsyncioTestCase):
    async def test_policy_denial_never_reaches_transport(self):
        transport = SequenceTransport(Response(200))
        async def denied(_):
            return False
        guard = AccessControlledTransport(transport, allowed_hosts=frozenset({"example.invalid"}), authorize=denied)
        with self.assertRaises(WorkerError):
            await guard.fetch(Request("source", "https://example.invalid/a"))
        self.assertEqual(transport.calls, [])

    async def test_disallowed_origins_never_reach_transport(self):
        transport = SequenceTransport(Response(200))
        async def allowed(_):
            return True
        guard = AccessControlledTransport(transport, allowed_hosts=frozenset({"example.invalid", "127.0.0.1", "localhost"}), authorize=allowed)
        for url in ("http://example.invalid/a", "https://user:secret@example.invalid/a", "https://other.invalid/a", "https://127.0.0.1/a", "https://localhost/a", "https://example.invalid:444/a"):
            with self.subTest(url=url), self.assertRaises(WorkerError):
                await guard.fetch(Request("source", url))
        self.assertEqual(transport.calls, [])

    async def test_explicit_allowed_request(self):
        transport = SequenceTransport(Response(200))
        async def allowed(_):
            return True
        guard = AccessControlledTransport(transport, allowed_hosts=frozenset({"example.invalid"}), authorize=allowed)
        await guard.fetch(Request("source", "https://example.invalid/a"))
        self.assertEqual(len(transport.calls), 1)


if __name__ == "__main__":
    unittest.main()

"""Strict JSONL input and cancellation-safe worker lifecycle regression tests."""
from __future__ import annotations

import asyncio
import json
import sys
import unittest
from pathlib import Path

WORKER_ROOT = Path(__file__).resolve().parents[1]
if str(WORKER_ROOT) not in sys.path:
    sys.path.insert(0, str(WORKER_ROOT))

from mdcx_worker.adapters import DEFAULT_SOURCES  # noqa: E402 - direct test discovery adds worker root above
from mdcx_worker.models import Candidate, WorkerError  # noqa: E402 - direct test discovery adds worker root above
from mdcx_worker.protocol import (  # noqa: E402 - direct test discovery adds worker root above
    MAX_LINE_BYTES,
    Command,
    Worker,
    parse_line,
    recover_request_id,
)


def request_bytes(**updates) -> bytes:
    value = {"version": 1, "requestId": "req-1", "command": "match", "query": "DEMO-001"}
    value.update(updates)
    return json.dumps(value).encode("utf-8")


class ParseLineTests(unittest.TestCase):
    def assert_error(self, raw: bytes, code: str = "invalid_request") -> None:
        with self.assertRaises(WorkerError) as raised:
            parse_line(raw)
        self.assertEqual(raised.exception.code, code)

    def test_valid_match_uses_default_sources_and_trims_query(self):
        command = parse_line(request_bytes(query="  DEMO-001  "))
        self.assertEqual(command, Command("req-1", "match", "DEMO-001", DEFAULT_SOURCES))

    def test_explicit_source_order_is_preserved(self):
        command = parse_line(request_bytes(sources=["theporndb_movies", "xcity"]))
        self.assertEqual(command.sources, ("theporndb_movies", "xcity"))
        self.assertEqual(parse_line(request_bytes(sources=["xcity"])).sources, ("xcity",))

    def test_cancel_identifies_target_without_separate_query(self):
        command = parse_line(b'{"version":1,"requestId":"target.1:part_2","command":"cancel"}')
        self.assertEqual(command.request_id, "target.1:part_2")
        self.assertEqual(command.command, "cancel")
        self.assertEqual(command.query, "")

    def test_malformed_non_object_and_non_utf8_frames(self):
        for raw in (b"", b"{", b"[]", b"null", b"true", b"1", b'"text"', b"\xff", b"{}{}"):
            with self.subTest(raw=raw):
                self.assert_error(raw)

    def test_duplicate_json_keys_are_rejected_even_when_values_agree(self):
        for raw in (
            b'{"version":1,"version":1,"requestId":"req-1","command":"match","query":"DEMO-001"}',
            b'{"version":1,"requestId":"req-1","requestId":"req-2","command":"match","query":"DEMO-001"}',
            b'{"version":1,"requestId":"req-1","command":"match","query":"DEMO-001","extra":{"x":1,"x":2}}',
        ):
            with self.subTest(raw=raw):
                self.assert_error(raw)
                self.assertEqual(recover_request_id(raw), "")

    def test_nonfinite_json_numbers_are_rejected(self):
        for constant in (b"NaN", b"Infinity", b"-Infinity"):
            with self.subTest(constant=constant):
                self.assert_error(b'{"version":1,"requestId":"req-1","command":"match","query":' + constant + b'}')

    def test_version_requires_integer_one_not_boolean_or_float(self):
        for version in (True, False, 0, 2, "1", 1.0, None):
            with self.subTest(version=version):
                self.assert_error(request_bytes(version=version), "unsupported_version")
        self.assert_error(b'{"requestId":"req-1","command":"match","query":"DEMO-001"}', "unsupported_version")

    def test_request_id_must_be_safe_bounded_ascii(self):
        for request_id in ("", "a" * 129, "has space", "line\nbreak", "slash/id", "日本語", 1, None):
            with self.subTest(request_id=request_id):
                raw = request_bytes(requestId=request_id)
                self.assert_error(raw)
                self.assertEqual(recover_request_id(raw), "")
        self.assertEqual(parse_line(request_bytes(requestId="a" * 128)).request_id, "a" * 128)

    def test_missing_blank_invalid_or_overlong_query(self):
        self.assert_error(b'{"version":1,"requestId":"req-1","command":"match"}')
        for query in (None, 123, [], "", "   ", "q" * 257):
            with self.subTest(query=query):
                self.assert_error(request_bytes(query=query))
        self.assertEqual(parse_line(request_bytes(query="q" * 256)).query, "q" * 256)

    def test_query_control_characters_are_rejected(self):
        for control in ("\0", "\t", "\n", "\r", "\x1b", "\x7f", "\x85"):
            with self.subTest(control=repr(control)):
                self.assert_error(request_bytes(query="DEMO" + control + "001"))
        self.assertEqual(parse_line(request_bytes(query="灯台の地図")).query, "灯台の地図")

    def test_unknown_duplicate_empty_and_wrongly_typed_sources(self):
        for sources in ([], ["unknown"], ["xcity", "xcity"], ["xcity", "theporndb_movies", "xcity"], "xcity", None, [1], [{"source": "xcity"}]):
            with self.subTest(sources=sources):
                self.assert_error(request_bytes(sources=sources), "invalid_sources")

    def test_unknown_commands_and_fields_are_rejected(self):
        for command in ("unknown", "MATCH", "", None, 1):
            with self.subTest(command=command):
                self.assert_error(request_bytes(command=command))
        self.assert_error(request_bytes(extra="unused"))
        self.assert_error(request_bytes(targetRequestId="req-2"))
        self.assert_error(request_bytes(command="cancel"))
        self.assert_error(b'{"version":1,"requestId":"req-1","command":"cancel","sources":["xcity"]}')

    def test_credentials_transport_urls_and_fixture_paths_are_never_jsonl_inputs(self):
        for field in ("credential", "credentials", "token", "apiToken", "authorization", "headers", "url", "fixturePath", "fixtureRoot"):
            with self.subTest(field=field):
                self.assert_error(request_bytes(**{field: "inert-test-value"}))

    def test_lone_surrogates_are_rejected_before_url_encoding(self):
        for query in ("\ud800", "Title\udfff"):
            with self.subTest(query=repr(query)):
                self.assert_error(request_bytes(query=query))

    def test_line_size_boundary_and_oversized_frame(self):
        raw = request_bytes()
        at_limit = raw + b" " * (MAX_LINE_BYTES - len(raw))
        self.assertEqual(parse_line(at_limit).request_id, "req-1")
        self.assert_error(at_limit + b" ", "request_too_large")
        self.assertEqual(recover_request_id(at_limit + b" "), "")

    def test_recover_request_id_only_echoes_valid_unambiguous_ids(self):
        self.assertEqual(recover_request_id(request_bytes(query=None)), "req-1")
        for raw in (b"{", b"[]", b"null", b"\xff", b'{"requestId":"unsafe id"}', b'{"requestId":"a","requestId":"b"}'):
            with self.subTest(raw=raw):
                self.assertEqual(recover_request_id(raw), "")


class FakeScheduler:
    def __init__(self):
        self.snapshots = 0

    def snapshot(self):
        self.snapshots += 1
        return {"xcity": {"requests": 1, "inFlight": 0}}


class GateMatcher:
    """Event-controlled fake, optionally continuing after its caller cancels."""
    def __init__(self, *, candidates=(), errors=(), ignore_cancel=False, fail=None):
        self.scheduler = FakeScheduler()
        self.candidates = list(candidates)
        self.errors = list(errors)
        self.ignore_cancel = ignore_cancel
        self.fail = fail
        self.entered = asyncio.Event()
        self.release = asyncio.Event()
        self.cancelled = asyncio.Event()
        self.calls = []

    async def match(self, query, sources, on_error):
        self.calls.append((query, sources))
        self.entered.set()
        try:
            await self.release.wait()
        except asyncio.CancelledError:
            self.cancelled.set()
            if not self.ignore_cancel:
                raise
            await self.release.wait()
        for source, error in self.errors:
            on_error(source, error)
        if self.fail is not None:
            raise self.fail
        return self.candidates


def demo_candidate():
    return Candidate("DEMO-001", "The Lantern Map", 2024, "xcity", "https://xcity.example.invalid/detail/DEMO-001", 0.99, {"fixture": "true"})


class WorkerLifecycleTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.workers = []

    async def asyncTearDown(self):
        for worker in self.workers:
            for state in list(worker.active.values()):
                if state.task is not None and not state.task.done():
                    state.task.cancel()
            worker.matcher.release.set()
            await asyncio.wait_for(worker.drain(), 1)

    def make_worker(self, matcher=None, **options):
        events = []
        worker = Worker(matcher or GateMatcher(), events.append, **options)
        self.workers.append(worker)
        return worker, worker.matcher, events

    async def drain(self, worker):
        await asyncio.wait_for(worker.drain(), 1)
        await asyncio.sleep(0)  # Let task-done callbacks remove active entries.
        self.assertEqual(worker.active, {})

    def assert_single_terminal(self, events, request_id, event_type):
        terminal = [event for event in events if event["requestId"] == request_id and event.get("terminal") is True]
        self.assertEqual(len(terminal), 1, events)
        self.assertEqual(terminal[0]["type"], event_type)
        return terminal[0]

    async def test_immediate_cancel_before_first_turn_emits_exactly_one_cancelled(self):
        worker, matcher, events = self.make_worker()
        worker.submit(Command("one", "match", "DEMO-001"))
        worker.submit(Command("one", "cancel"))
        self.assertEqual([event["type"] for event in events], ["cancelled"])
        await self.drain(worker)
        self.assertEqual(matcher.calls, [])
        self.assertEqual(len(events), 1)
        self.assert_single_terminal(events, "one", "cancelled")

    async def test_in_flight_cancel_blocks_late_candidates_and_source_errors(self):
        matcher = GateMatcher(candidates=[demo_candidate()], errors=[("xcity", WorkerError("network", "Late failure"))], ignore_cancel=True)
        worker, matcher, events = self.make_worker(matcher)
        worker.submit(Command("one", "match", "DEMO-001"))
        await asyncio.wait_for(matcher.entered.wait(), 1)
        worker.submit(Command("one", "cancel"))
        await asyncio.wait_for(matcher.cancelled.wait(), 1)
        matcher.release.set()
        await self.drain(worker)
        self.assertEqual([event["type"] for event in events], ["cancelled"])
        self.assertEqual(matcher.scheduler.snapshots, 0)
        self.assert_single_terminal(events, "one", "cancelled")

    async def test_repeated_cancel_is_nonterminal_not_active(self):
        worker, _matcher, events = self.make_worker()
        worker.submit(Command("one", "match", "DEMO-001"))
        worker.submit(Command("one", "cancel"))
        worker.submit(Command("one", "cancel"))
        await self.drain(worker)
        self.assert_single_terminal(events, "one", "cancelled")
        self.assertEqual(events[-1]["error"]["code"], "not_active")
        self.assertIs(events[-1]["terminal"], False)

    async def test_duplicate_active_id_does_not_cancel_original(self):
        worker, matcher, events = self.make_worker(GateMatcher(candidates=[demo_candidate()]))
        worker.submit(Command("one", "match", "DEMO-001"))
        await asyncio.wait_for(matcher.entered.wait(), 1)
        worker.submit(Command("one", "match", "different"))
        self.assertEqual(events[-1]["error"]["code"], "duplicate_request")
        self.assertIs(events[-1]["terminal"], False)
        matcher.release.set()
        await self.drain(worker)
        self.assertEqual(matcher.calls, [("DEMO-001", DEFAULT_SOURCES)])
        terminal = self.assert_single_terminal(events, "one", "completed")
        self.assertEqual(terminal["status"], "matched")
        self.assertEqual(terminal["count"], 1)

    async def test_completed_and_cancelled_ids_cannot_be_reused(self):
        for cancelled in (False, True):
            with self.subTest(cancelled=cancelled):
                worker, matcher, events = self.make_worker()
                worker.submit(Command("one", "match", "DEMO-001"))
                if cancelled:
                    worker.submit(Command("one", "cancel"))
                else:
                    matcher.release.set()
                await self.drain(worker)
                before = len(events)
                worker.submit(Command("one", "match", "DEMO-001"))
                self.assertEqual(len(events), before + 1)
                self.assertEqual(events[-1]["error"]["code"], "duplicate_request")
                self.assertIs(events[-1]["terminal"], False)
                self.assert_single_terminal(events, "one", "cancelled" if cancelled else "completed")

    async def test_stale_or_unknown_cancel_does_not_consume_session_capacity(self):
        worker, matcher, events = self.make_worker(max_requests=1)
        worker.submit(Command("missing", "cancel"))
        self.assertEqual(events[-1]["error"]["code"], "not_active")
        self.assertIs(events[-1]["terminal"], False)
        self.assertEqual(worker.seen, set())
        worker.submit(Command("one", "match", "DEMO-001"))
        matcher.release.set()
        await self.drain(worker)
        worker.submit(Command("one", "cancel"))
        self.assertEqual(events[-1]["error"]["code"], "not_active")
        self.assertIs(events[-1]["terminal"], False)
        self.assert_single_terminal(events, "one", "completed")

    async def test_max_active_is_bounded_and_busy_ids_are_consumed(self):
        worker, matcher, events = self.make_worker(max_active=2)
        for request_id in ("one", "two", "three", "four"):
            worker.submit(Command(request_id, "match", "DEMO-001"))
            self.assertLessEqual(len(worker.active), 2)
        self.assertEqual(set(worker.active), {"one", "two"})
        for request_id in ("three", "four"):
            terminal = self.assert_single_terminal(events, request_id, "error")
            self.assertEqual(terminal["error"]["code"], "busy")
            self.assertIs(terminal["error"]["retryable"], True)
        matcher.release.set()
        await self.drain(worker)
        worker.submit(Command("three", "match", "DEMO-001"))
        self.assertEqual(events[-1]["error"]["code"], "duplicate_request")
        self.assertIs(events[-1]["terminal"], False)
        worker.submit(Command("fresh", "match", "DEMO-001"))
        await self.drain(worker)
        self.assert_single_terminal(events, "fresh", "completed")

    async def test_session_cap_is_bounded_and_duplicate_classification_is_stable(self):
        worker, matcher, events = self.make_worker(max_requests=2)
        matcher.release.set()
        for request_id in ("one", "two"):
            worker.submit(Command(request_id, "match", "DEMO-001"))
            await self.drain(worker)
        for request_id in ("three", "four", "five"):
            worker.submit(Command(request_id, "match", "DEMO-001"))
            self.assertEqual(events[-1]["error"]["code"], "session_full")
            self.assertIs(events[-1]["terminal"], True)
            self.assertEqual(len(worker.seen), 2)
            self.assertEqual(worker.active, {})
        worker.submit(Command("one", "match", "DEMO-001"))
        self.assertEqual(events[-1]["error"]["code"], "duplicate_request")
        self.assertIs(events[-1]["terminal"], False)
        self.assertEqual(len(matcher.calls), 2)

    async def test_request_timeout_has_one_retryable_terminal_error(self):
        worker, matcher, events = self.make_worker(request_timeout=0.02)
        worker.submit(Command("slow", "match", "DEMO-001"))
        await self.drain(worker)
        terminal = self.assert_single_terminal(events, "slow", "error")
        self.assertEqual(terminal["error"]["code"], "deadline")
        self.assertIs(terminal["error"]["retryable"], True)
        self.assertTrue(matcher.cancelled.is_set())
        self.assertEqual(len(events), 1)

    async def test_matched_event_order_and_requested_source_order(self):
        worker, matcher, events = self.make_worker(GateMatcher(candidates=[demo_candidate()]))
        sources = ("theporndb_movies", "xcity")
        worker.submit(Command("one", "match", "DEMO-001", sources))
        matcher.release.set()
        await self.drain(worker)
        self.assertEqual(matcher.calls, [("DEMO-001", sources)])
        self.assertEqual([event["type"] for event in events], ["candidate", "health", "completed"])
        self.assertTrue(all(event["version"] == 1 and event["requestId"] == "one" for event in events))
        self.assertEqual(events[0]["candidate"]["id"], "DEMO-001")
        self.assertEqual(events[1]["sources"], {"xcity": {"requests": 1, "inFlight": 0}})
        self.assertEqual(events[-1]["sourceFailures"], 0)
        self.assert_single_terminal(events, "one", "completed")

    async def test_empty_match_and_source_failure_have_distinct_status(self):
        for errors, status in (([], "no_match"), ([("xcity", WorkerError("parse", "Invalid metadata"))], "sources_failed")):
            with self.subTest(status=status):
                worker, matcher, events = self.make_worker(GateMatcher(errors=errors))
                worker.submit(Command("one", "match", "unknown"))
                matcher.release.set()
                await self.drain(worker)
                terminal = self.assert_single_terminal(events, "one", "completed")
                self.assertEqual(terminal["status"], status)
                self.assertEqual(terminal["count"], 0)
                self.assertEqual(terminal["sourceFailures"], len(errors))
                source_errors = [event for event in events if event["type"] == "error"]
                self.assertEqual(len(source_errors), len(errors))
                for event in source_errors:
                    self.assertIs(event["terminal"], False)
                    self.assertEqual(event["source"], "xcity")

    async def test_unexpected_exception_is_safe_terminal_error(self):
        worker, matcher, events = self.make_worker(GateMatcher(fail=RuntimeError("secret-token-not-for-output")))
        worker.submit(Command("one", "match", "DEMO-001"))
        matcher.release.set()
        await self.drain(worker)
        terminal = self.assert_single_terminal(events, "one", "error")
        self.assertEqual(terminal["error"]["code"], "internal")
        self.assertNotIn("secret-token", json.dumps(events))
        self.assertEqual(len(events), 1)

    async def test_drain_waits_for_all_submitted_requests(self):
        worker, matcher, events = self.make_worker()
        worker.submit(Command("one", "match", "DEMO-001"))
        worker.submit(Command("two", "match", "unknown"))
        waiter = asyncio.create_task(worker.drain())
        await asyncio.wait_for(matcher.entered.wait(), 1)
        await asyncio.sleep(0)
        self.assertFalse(waiter.done())
        matcher.release.set()
        await asyncio.wait_for(waiter, 1)
        await self.drain(worker)
        self.assert_single_terminal(events, "one", "completed")
        self.assert_single_terminal(events, "two", "completed")


if __name__ == "__main__":
    unittest.main()

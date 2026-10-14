"""Exercise the real fixture-only executable through its JSONL streams."""
from __future__ import annotations

import asyncio
import json
import sys
import tempfile
import unittest
from pathlib import Path

WORKER_ROOT = Path(__file__).resolve().parents[1]
if str(WORKER_ROOT) not in sys.path:
    sys.path.insert(0, str(WORKER_ROOT))

from mdcx_worker.protocol import MAX_LINE_BYTES  # noqa: E402 - direct test discovery adds worker root above

MAIN = WORKER_ROOT / "main.py"
# Execute the unchanged __main__ entrypoint, installing guards only after the
# event loop creates its internal wakeup socket (also portable to Proactor).
NETWORK_GUARDED_ENTRYPOINT = """
import asyncio
import runpy
import socket
import sys
from pathlib import Path
from unittest.mock import patch

original_run = asyncio.run

async def guarded(coro):
    def forbidden(*args, **kwargs):
        raise AssertionError("Unexpected external socket or network request")
    with (patch.object(socket.socket, "__new__", side_effect=forbidden) as sockets,
          patch("socket.create_connection", side_effect=forbidden) as connections,
          patch("socket.getaddrinfo", side_effect=forbidden) as dns,
          patch("urllib.request.urlopen", side_effect=forbidden) as http):
        await coro
        for guard in (sockets, connections, dns, http):
            guard.assert_not_called()
    print("offline network guard: zero external socket, connection, DNS or HTTP calls", file=sys.stderr)

def guarded_run(coro, **kwargs):
    return original_run(guarded(coro), **kwargs)

asyncio.run = guarded_run
entrypoint = sys.argv[1]
sys.path.insert(0, str(Path(entrypoint).parent))
sys.argv = [entrypoint, *sys.argv[2:]]
runpy.run_path(entrypoint, run_name="__main__")
"""


def match_frame(request_id="known", query="DEMO-001", **options):
    return json.dumps({"version": 1, "requestId": request_id, "command": "match", "query": query, **options}).encode("utf-8") + b"\n"


class CliTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.processes = []
        self.unrelated_cwd = tempfile.TemporaryDirectory(prefix="mdcx-worker-cli-")

    async def asyncTearDown(self):
        for process in self.processes:
            if process.returncode is None:
                process.kill()
                await process.wait()
        self.unrelated_cwd.cleanup()

    async def start_worker(self, *, deny_network=False):
        arguments = ["-c", NETWORK_GUARDED_ENTRYPOINT, str(MAIN)] if deny_network else [str(MAIN)]
        process = await asyncio.create_subprocess_exec(
            sys.executable, *arguments, cwd=self.unrelated_cwd.name,
            stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
        )
        self.processes.append(process)
        return process

    def decode_events(self, stdout):
        if stdout:
            self.assertTrue(stdout.endswith(b"\n"), "Every stdout event must end with a newline")
        events = []
        for line in stdout.splitlines():
            self.assertTrue(line, "stdout must not contain blank diagnostic lines")
            event = json.loads(line)
            self.assertIsInstance(event, dict)
            self.assertEqual(event["version"], 1)
            self.assertIsInstance(event["requestId"], str)
            self.assertIn(event["type"], {"candidate", "health", "completed", "cancelled", "error"})
            events.append(event)
        return events

    async def run_worker(self, data, *, deny_network=False):
        process = await self.start_worker(deny_network=deny_network)
        stdout, stderr = await asyncio.wait_for(process.communicate(data), 5)
        self.assertEqual(process.returncode, 0, stderr.decode("utf-8", errors="replace"))
        self.assertIn(b"fixture transport", stderr)
        self.assertIn(b"live network disabled", stderr)
        self.assertNotIn(b"Traceback", stderr)
        self.assertNotIn(b"metadata worker", stdout)
        if deny_network:
            self.assertIn(b"offline network guard: zero external socket, connection, DNS or HTTP calls", stderr)
        return self.decode_events(stdout), stderr

    def assert_terminal(self, events, request_id, event_type="completed"):
        terminals = [event for event in events if event["requestId"] == request_id and event.get("terminal") is True]
        self.assertEqual(len(terminals), 1, events)
        self.assertEqual(terminals[0]["type"], event_type)
        return terminals[0]

    async def test_empty_stdin_prints_startup_diagnostics_only_to_stderr(self):
        events, _ = await self.run_worker(b"")
        self.assertEqual(events, [])

    async def test_known_fixture_from_unrelated_cwd_has_string_provenance(self):
        events, _ = await self.run_worker(match_frame())
        self.assertEqual([event["type"] for event in events], ["candidate", "health", "completed"])
        candidate = events[0]["candidate"]
        self.assertEqual(candidate["id"], "DEMO-001")
        self.assertEqual(candidate["source"], "xcity")
        self.assertEqual(candidate["title"], "The Lantern Map")
        self.assertIsInstance(candidate["confidence"], (int, float))
        self.assertGreaterEqual(candidate["confidence"], 0)
        self.assertLessEqual(candidate["confidence"], 1)
        provenance = candidate["provenance"]
        self.assertTrue(provenance)
        self.assertTrue(all(isinstance(key, str) and isinstance(value, str) for key, value in provenance.items()))
        self.assertEqual(provenance["fixture"], "true")
        self.assertEqual(provenance["matchMethod"], "exact_identifier")
        self.assertRegex(provenance["bodySha256"], r"^[a-f0-9]{64}$")
        terminal = self.assert_terminal(events, "known")
        self.assertEqual(terminal["status"], "matched")
        self.assertEqual(terminal["count"], 1)
        self.assertEqual(terminal["sourceFailures"], 0)

    async def test_ordered_sources_can_select_second_migrated_adapter_first(self):
        events, _ = await self.run_worker(match_frame(query="The Lantern Map", sources=["theporndb_movies", "xcity"]))
        candidates = [event["candidate"] for event in events if event["type"] == "candidate"]
        self.assertEqual(len(candidates), 1)
        self.assertEqual(candidates[0]["id"], "theporndb_movies:the-lantern-map")
        self.assertEqual(candidates[0]["sourceId"], "the-lantern-map")
        self.assertIsNone(candidates[0]["catalogIdentifier"])
        self.assertEqual(candidates[0]["source"], "theporndb_movies")
        health = next(event for event in events if event["type"] == "health")
        self.assertEqual(set(health["sources"]), {"theporndb_movies"})
        self.assert_terminal(events, "known")

    async def test_declared_negative_examples_complete_no_match_without_candidate(self):
        # These two queries are explicitly authored negative fixtures, not
        # evidence about any real provider. TPDB cannot search catalog IDs.
        for query, options in (("ABSENT-999", {"sources": ["xcity"]}), ("No Such Fictional Film", {})):
            with self.subTest(query=query):
                events, _ = await self.run_worker(match_frame("negative", query, **options), deny_network=True)
                self.assertFalse(any(event["type"] in {"candidate", "error"} for event in events))
                terminal = self.assert_terminal(events, "negative")
                self.assertEqual(terminal["status"], "no_match")
                self.assertEqual(terminal["count"], 0)
                self.assertEqual(terminal["sourceFailures"], 0)

    async def test_unlisted_queries_fail_honestly_without_external_sockets(self):
        for query, options, expected in (
            ("BF-658", {}, [("xcity", "live_unavailable"), ("theporndb_movies", "unsupported_query")]),
            ("Unlisted Real Movie Title", {}, [("xcity", "live_unavailable"), ("theporndb_movies", "live_unavailable")]),
            ("BF-658", {"sources": ["xcity"]}, [("xcity", "live_unavailable")]),
            ("Unlisted Real Movie Title", {"sources": ["theporndb_movies"]}, [("theporndb_movies", "live_unavailable")]),
            ("BF-658", {"sources": ["theporndb_movies"]}, [("theporndb_movies", "unsupported_query")]),
        ):
            with self.subTest(query=query, options=options):
                events, _ = await self.run_worker(match_frame("unlisted", query, **options), deny_network=True)
                self.assertFalse(any(event["type"] == "candidate" for event in events))
                errors = [event for event in events if event["type"] == "error"]
                self.assertEqual([(event["source"], event["error"]["code"]) for event in errors], expected)
                for event in errors:
                    self.assertIs(event["terminal"], False)
                    self.assertIs(event["error"]["retryable"], False)
                    self.assertNotIn(query, event["error"]["message"])
                    if event["error"]["code"] == "live_unavailable":
                        self.assertIn("Demo mode only contains authored offline examples", event["error"]["message"])
                terminal = self.assert_terminal(events, "unlisted")
                self.assertEqual(terminal["status"], "sources_failed")
                self.assertEqual(terminal["count"], 0)
                self.assertEqual(terminal["sourceFailures"], len(expected))
                health = next(event["sources"] for event in events if event["type"] == "health")
                for source, code in expected:
                    self.assertEqual(health[source]["requests"], 1 if code == "live_unavailable" else 0)
                    self.assertEqual(health[source]["successes"], 0)
                    self.assertEqual(health[source]["retries"], 0)
                    self.assertEqual(health[source]["noMatches"], 0)
                    self.assertEqual(health[source]["lastError"], code)

    async def test_eof_drains_multiple_outstanding_requests(self):
        data = match_frame("first") + match_frame("second", "DEMO-002") + match_frame("third", "No Such Fictional Film")
        events, _ = await self.run_worker(data)
        self.assertEqual({event["requestId"] for event in events}, {"first", "second", "third"})
        self.assertEqual(self.assert_terminal(events, "first")["status"], "matched")
        self.assertEqual(self.assert_terminal(events, "second")["status"], "matched")
        self.assertEqual(self.assert_terminal(events, "third")["status"], "no_match")
        for request_id in ("first", "second", "third"):
            per_request = [event for event in events if event["requestId"] == request_id]
            self.assertEqual(per_request[-1]["type"], "completed")

    async def test_results_are_flushed_before_stdin_eof_and_stream_continues(self):
        process = await self.start_worker()
        process.stdin.write(match_frame("first"))
        await process.stdin.drain()
        first = []
        while not first or first[-1].get("terminal") is not True:
            raw = await asyncio.wait_for(process.stdout.readline(), 3)
            self.assertTrue(raw, "worker exited before responding to an open input stream")
            first.extend(self.decode_events(raw))
        self.assert_terminal(first, "first")
        self.assertIsNone(process.returncode)
        process.stdin.write(match_frame("second", "No Such Fictional Film"))
        await process.stdin.drain()
        stdout, stderr = await asyncio.wait_for(process.communicate(), 5)
        self.assertEqual(process.returncode, 0, stderr.decode())
        second = self.decode_events(stdout)
        self.assertEqual(self.assert_terminal(second, "second")["status"], "no_match")
        self.assertTrue(all(event["requestId"] == "second" for event in second))

    async def test_last_frame_without_newline_is_processed_before_exit(self):
        events, _ = await self.run_worker(match_frame().rstrip(b"\n"))
        self.assertEqual(self.assert_terminal(events, "known")["status"], "matched")

    async def test_malformed_frame_is_safe_and_does_not_poison_next_request(self):
        events, stderr = await self.run_worker(b"{this is not json}\n" + match_frame())
        invalid = self.assert_terminal(events, "", "error")
        self.assertEqual(invalid["error"]["code"], "invalid_request")
        self.assertEqual(self.assert_terminal(events, "known")["status"], "matched")
        self.assertNotIn(b"this is not json", stderr)

    async def test_invalid_utf8_and_schema_errors_remain_structured(self):
        bad_schema = json.dumps({"version": True, "requestId": "bad-version", "command": "match", "query": "DEMO-001"}).encode() + b"\n"
        events, _ = await self.run_worker(b"\xff\n" + bad_schema + match_frame())
        self.assertEqual(self.assert_terminal(events, "", "error")["error"]["code"], "invalid_request")
        self.assertEqual(self.assert_terminal(events, "bad-version", "error")["error"]["code"], "unsupported_version")
        self.assert_terminal(events, "known")

    async def test_oversized_frame_resynchronizes_after_discarding_entire_line(self):
        oversized = b'{"version":1,"requestId":"do-not-echo","command":"match","query":"' + b"x" * (MAX_LINE_BYTES * 3) + b'"}\n'
        events, _ = await self.run_worker(oversized + match_frame())
        errors = [event for event in events if event["type"] == "error"]
        self.assertEqual(len(errors), 1)
        self.assertEqual(errors[0]["requestId"], "")
        self.assertEqual(errors[0]["error"]["code"], "request_too_large")
        self.assertIs(errors[0]["terminal"], True)
        self.assert_terminal(events, "known")

    async def test_oversized_frame_without_newline_exits_cleanly(self):
        events, _ = await self.run_worker(b"x" * (MAX_LINE_BYTES * 2))
        self.assertEqual(len(events), 1)
        self.assertEqual(self.assert_terminal(events, "", "error")["error"]["code"], "request_too_large")

    async def test_duplicate_id_returns_nonterminal_error_without_second_match(self):
        events, _ = await self.run_worker(match_frame("same") + match_frame("same"))
        terminal = self.assert_terminal(events, "same")
        self.assertEqual(terminal["status"], "matched")
        duplicates = [event for event in events if event["type"] == "error"]
        self.assertEqual(len(duplicates), 1)
        self.assertEqual(duplicates[0]["error"]["code"], "duplicate_request")
        self.assertIs(duplicates[0]["terminal"], False)
        self.assertEqual(len([event for event in events if event["type"] == "candidate"]), 1)

    async def test_malformed_frame_for_active_id_cannot_end_original_match(self):
        # Earlier same-host work keeps the target behind the real rate gate
        # while its malformed second frame is read and classified.
        data = b"".join(match_frame(f"warmup-{index}") for index in range(3))
        data += match_frame("active") + match_frame("active", False)
        events, _ = await self.run_worker(data)
        active = [event for event in events if event["requestId"] == "active"]
        errors = [event for event in active if event["type"] == "error"]
        self.assertEqual(len(errors), 1)
        self.assertEqual(errors[0]["error"]["code"], "invalid_request")
        self.assertIs(errors[0]["terminal"], False)
        self.assertEqual([event["type"] for event in active], ["error", "candidate", "health", "completed"])
        self.assertEqual(self.assert_terminal(active, "active")["status"], "matched")

    async def test_malformed_frame_for_completed_id_cannot_add_terminal_outcome(self):
        process = await self.start_worker()
        process.stdin.write(match_frame("completed"))
        await process.stdin.drain()
        events = []
        while not events or events[-1].get("terminal") is not True:
            raw = await asyncio.wait_for(process.stdout.readline(), 3)
            self.assertTrue(raw, "worker exited before completing the original request")
            events.extend(self.decode_events(raw))
        self.assert_terminal(events, "completed")

        process.stdin.write(match_frame("completed", False) + match_frame("fresh"))
        await process.stdin.drain()
        stdout, stderr = await asyncio.wait_for(process.communicate(), 5)
        self.assertEqual(process.returncode, 0, stderr.decode())
        events.extend(self.decode_events(stdout))
        completed = [event for event in events if event["requestId"] == "completed"]
        self.assertEqual([event["type"] for event in completed], ["candidate", "health", "completed", "error"])
        self.assertEqual(completed[-1]["error"]["code"], "invalid_request")
        self.assertIs(completed[-1]["terminal"], False)
        self.assert_terminal(events, "completed")
        self.assertEqual(self.assert_terminal(events, "fresh")["status"], "matched")

    async def test_unknown_cancel_is_nonterminal_and_worker_stays_usable(self):
        cancel = b'{"version":1,"requestId":"missing","command":"cancel"}\n'
        events, _ = await self.run_worker(cancel + match_frame())
        stale = [event for event in events if event["requestId"] == "missing"]
        self.assertEqual(len(stale), 1)
        self.assertEqual(stale[0]["error"]["code"], "not_active")
        self.assertIs(stale[0]["terminal"], False)
        self.assert_terminal(events, "known")


if __name__ == "__main__":
    unittest.main()

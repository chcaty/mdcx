"""Local synthetic contract tests; no source requests, media, or credentials."""
import asyncio
import hashlib
import socket
import sys
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from mdcx_worker.javbus_provider import JavBusProvider
from mdcx_worker.models import CatalogIdentifier, DetailTarget, ProviderIdentity, Request, Response, WorkerError, comparable_catalog
from mdcx_worker.scheduler import Scheduler
from mdcx_worker.source_settings import JAVBUS_ORIGIN, validate_javbus_request

FIXTURES = Path(__file__).resolve().parents[1] / "fixtures" / "javbus"
DIRECT = Request("javbus", JAVBUS_ORIGIN + "/DEMO-001")
SEARCH = Request("javbus", JAVBUS_ORIGIN + "/search/DEMO-001&type=&parent=ce")
ALIAS = Request("javbus", JAVBUS_ORIGIN + "/demo001")
TARGET = DetailTarget(ProviderIdentity("javbus", "DEMO-001"), DIRECT.url)


def fixture(name):
    return (FIXTURES / name).read_bytes()


def response(name):
    return Response(200, fixture(name), {"Content-Type": "text/html; charset=utf-8"}, fixture=True)


class ScriptTransport:
    """Independent exact-order script; no fixture gap becomes an invented 404."""
    def __init__(self, expected):
        self.expected, self.calls = list(expected), []

    async def fetch(self, request):
        validate_javbus_request(request)
        self.calls.append(request)
        if not self.expected or self.expected[0][0] != request:
            raise AssertionError("Unexpected scripted request")
        _, result = self.expected.pop(0)
        if isinstance(result, Exception):
            raise result
        return result

    def assert_complete(self):
        if self.expected:
            raise AssertionError("Unconsumed scripted request")


def provider(expected=()):
    return JavBusProvider(Scheduler(ScriptTransport(expected), min_interval=0, attempts=1))


class DetailParserTests(unittest.TestCase):
    def setUp(self):
        self.provider = provider()

    def test_legacy_sibling_identifier_nested_h3_and_single_digit_date(self):
        raw = self.provider.parse_detail(fixture("detail-demo-001.html"), TARGET, "demo_001")
        self.assertEqual(raw.catalog_identifier, CatalogIdentifier("javbus:catalog", "DEMO-001"))
        self.assertEqual(raw.provider_identity, ProviderIdentity("javbus", "DEMO-001"))
        self.assertEqual((raw.title, raw.release), ("The Lantern Map", "2024-03-09"))
        self.assertEqual(raw.fields["title"], "h3[1]")
        self.assertEqual(raw.fields["year.documentValue"], "2024-3-9")
        self.assertEqual(raw.fields["sourceId.validation"], "response_route_only")
        self.assertFalse(comparable_catalog(raw.catalog_identifier, CatalogIdentifier("xcity:hinban", "DEMO-001")))

    def test_parent_text_identifier_title_fallback_and_slash_date(self):
        target = DetailTarget(ProviderIdentity("javbus", "DEMO-002"), JAVBUS_ORIGIN + "/DEMO-002")
        raw = self.provider.parse_detail(fixture("detail-demo-002.html"), target, "DEMO-002")
        self.assertEqual((raw.title, raw.release), ("A Quiet Observatory", "2023-11-02"))
        self.assertEqual(raw.fields["title"], "title[1]")

    def test_missing_identifier_never_uses_query_route_title_or_canonical(self):
        for body in (fixture("detail-missing-id.html"), fixture("detail-missing-id.html").replace(
                b"</head>", b'<link rel="canonical" href="/DEMO-001"></head>')):
            with self.subTest(body=body), self.assertRaises(WorkerError) as caught:
                self.provider.parse_detail(body, TARGET, "DEMO-001")
            self.assertEqual(caught.exception.code, "schema_drift")

    def test_wrong_identifier_cannot_become_title_match(self):
        with self.assertRaises(WorkerError) as caught:
            self.provider.parse_detail(fixture("detail-wrong-id.html"), TARGET, "DEMO-001")
        self.assertEqual(caught.exception.code, "identity_mismatch")

    def test_duplicate_labels_rejected_even_if_same_identifier(self):
        for body in (fixture("detail-ambiguous-id.html"), fixture("detail-ambiguous-id.html").replace(b"DEMO-099", b"DEMO-001")):
            with self.subTest(body=body), self.assertRaises(WorkerError) as caught:
                self.provider.parse_detail(body, TARGET, "DEMO-001")
            self.assertEqual(caught.exception.code, "ambiguous_identity")

    def test_multiple_values_under_one_catalog_label_are_ambiguous(self):
        body = fixture("detail-demo-001.html").replace(b"<span>DEMO-001</span>", b"<span>DEMO-001</span><span>DEMO-099</span>")
        with self.assertRaises(WorkerError) as caught:
            self.provider.parse_detail(body, TARGET, "DEMO-001")
        self.assertEqual(caught.exception.code, "ambiguous_identity")

    def test_template_identifiers_cannot_supply_missing_catalog_evidence(self):
        body = fixture("detail-missing-id.html").replace(b"</body>", b"<template><p><span>ID:</span><span>DEMO-001</span></p></template></body>")
        with self.assertRaises(WorkerError) as caught:
            self.provider.parse_detail(body, TARGET, "DEMO-001")
        self.assertEqual(caught.exception.code, "schema_drift")

    def test_catalog_format_preserves_zeroes_and_edition_suffixes(self):
        for value in (b"DEMO-0001", b"DEMO-001A", b"DEMO/001", b"DEMO-001 DEMO-002"):
            body = fixture("detail-demo-001.html").replace(b"<span>DEMO-001</span>", b"<span>" + value + b"</span>")
            with self.subTest(value=value), self.assertRaises(WorkerError):
                self.provider.parse_detail(body, TARGET, "DEMO-001")

    def test_source_route_identity_is_exact_and_separate(self):
        target = DetailTarget(ProviderIdentity("javbus", "demo001"), ALIAS.url)
        raw = self.provider.parse_detail(fixture("detail-demo-001.html"), target, "DEMO-001")
        self.assertEqual(raw.provider_identity.value, "demo001")
        self.assertEqual(raw.catalog_identifier.value, "DEMO-001")
        with self.assertRaises(WorkerError) as caught:
            self.provider.parse_detail(fixture("detail-demo-001.html"), DetailTarget(TARGET.provider_identity, ALIAS.url), "DEMO-001")
        self.assertEqual(caught.exception.code, "identity_mismatch")

    def test_canonical_link_only_checks_provider_route(self):
        for href, expected in (("/DEMO-001", None), ("/DEMO-099", "identity_mismatch"),
                               ("https://other.example/DEMO-001", "policy_blocked"), ("/demo001", "identity_mismatch")):
            body = fixture("detail-demo-001.html").replace(b"</head>", f'<link rel="canonical" href="{href}"></head>'.encode())
            if expected:
                with self.subTest(href=href), self.assertRaises(WorkerError) as caught:
                    self.provider.parse_detail(body, TARGET, "DEMO-001")
                self.assertEqual(caught.exception.code, expected)
            else:
                raw = self.provider.parse_detail(body, TARGET, "DEMO-001")
                self.assertEqual(raw.fields["sourceId.validation"], "response_route_and_canonical_link")

    def test_non_utf8_malformed_and_excessive_nesting_fail_closed(self):
        for body in (b"\xff", b"<h3>DEMO-001 Title", b'<p><span>ID:</p><span>DEMO-001</span>',
                     b"<div>" * 140, b'<span class="header" class="other">ID:</span>'):
            with self.subTest(body=body[:80]), self.assertRaises(WorkerError) as caught:
                self.provider.parse_detail(body, TARGET, "DEMO-001")
            self.assertEqual(caught.exception.code, "parse")

    def test_scripts_are_never_title_text(self):
        body = fixture("detail-demo-001.html").replace(b"The <em>Lantern</em> Map", b'The <script>wrong</script><em>Lantern</em> Map')
        self.assertEqual(self.provider.parse_detail(body, TARGET, "DEMO-001").title, "The Lantern Map")

    def test_challenge_auth_and_200_error_documents_are_errors(self):
        examples = ((b"<html>verify you are human</html>", "challenge"),
                    (b"<html>" + b" " * 17000 + b"challenge-platform</html>", "challenge"),
                    (b'<a href="/lostpasswd">Login required</a>', "auth_required"),
                    (b"<title>503 Service Unavailable</title>", "http_error"))
        for body, expected in examples:
            with self.subTest(expected=expected), self.assertRaises(WorkerError) as caught:
                self.provider.parse_detail(body, TARGET, "DEMO-001")
            self.assertEqual(caught.exception.code, expected)

    def test_search_requires_detail_followup_not_link_text_as_metadata(self):
        targets = self.provider.parse_search(fixture("search-demo-001.html"), "DEMO-001")
        self.assertEqual(len(targets), 1)
        self.assertEqual(targets[0].provider_identity, ProviderIdentity("javbus", "demo001"))
        self.assertEqual(targets[0].url, ALIAS.url)

    def test_ambiguous_search_routes_fail_without_guessing(self):
        with self.assertRaises(WorkerError) as caught:
            self.provider.parse_search(fixture("search-ambiguous.html"), "DEMO-001")
        self.assertEqual(caught.exception.code, "ambiguous_identity")

    def test_duplicate_search_link_preserves_evidence_and_deduplicates(self):
        body = fixture("search-demo-001.html").replace(b'<div id="waterfall">', b'<div id="waterfall"><a class="movie-box" href="/demo001">same</a>')
        targets = self.provider.parse_search(body, "DEMO-001")
        self.assertEqual((len(targets), targets[0].duplicate_count), (1, 2))

    def test_unsafe_search_routes_rejected_before_requests(self):
        for href in ("//other.example/DEMO-001", "https://other.example/DEMO-001", "/DEMO-001?token=secret",
                     "/DEMO-001#part", "/a/../DEMO-001", "/%44EMO-001", "/DEMO-001/", "javascript:bad", "DEMO-001"):
            body = f'<div id="waterfall"><a class="movie-box" href="{href}">Result</a></div>'.encode()
            with self.subTest(href=href), self.assertRaises(WorkerError) as caught:
                self.provider.parse_search(body, "DEMO-001")
            self.assertEqual(caught.exception.code, "policy_blocked")

    def test_empty_search_has_recognized_structure(self):
        self.assertEqual(self.provider.parse_search(fixture("search-empty.html"), "DEMO-001"), [])
        with self.assertRaises(WorkerError) as caught:
            self.provider.parse_search(b"<html>new schema or error</html>", "DEMO-001")
        self.assertEqual(caught.exception.code, "schema_drift")


class MatchingTests(unittest.IsolatedAsyncioTestCase):
    async def test_direct_catalog_match_requires_human_review_and_provenance(self):
        subject = provider([(DIRECT, response("detail-demo-001.html"))])
        errors = []
        result = await subject.match("ＤＥＭＯ－００１", ("javbus",), lambda source, error: errors.append((source, error.code)))
        self.assertEqual(errors, [])
        self.assertEqual(len(result), 1)
        item = result[0]
        self.assertEqual((item.id, item.sourceId, item.title, item.year), ("DEMO-001", "DEMO-001", "The Lantern Map", 2024))
        self.assertEqual(item.catalogIdentifier, CatalogIdentifier("javbus:catalog", "DEMO-001"))
        self.assertEqual(item.source, "javbus")
        self.assertTrue(item.payload()["requiresReview"])
        self.assertIn("catalog_metadata_requires_review", item.payload()["reviewReasons"])
        self.assertEqual(item.provenance["bodySha256"], hashlib.sha256(fixture("detail-demo-001.html")).hexdigest())
        self.assertEqual(item.provenance["responseOrigin"], "synthetic_uncaptured")
        self.assertEqual(item.provenance["catalogIdentifier.crossProviderIdentity"], "unavailable_no_crosswalk")
        self.assertEqual(item.provenance["field.catalogIdentifier.identityGate"], "exact_detail_catalog_to_query")
        self.assertEqual(item.provenance["lookup.method"], "direct_catalog_detail")
        self.assertTrue(all(isinstance(value, str) for value in item.provenance.values()))
        subject.scheduler.transport.assert_complete()

    async def test_only_genuine_404_triggers_exact_legacy_search_route(self):
        subject = provider([(DIRECT, Response(404)), (SEARCH, response("search-demo-001.html")), (ALIAS, response("detail-demo-001.html"))])
        result = await subject.match("demo001")
        self.assertEqual((len(result), result[0].sourceId), (1, "demo001"))
        self.assertEqual(result[0].provenance["lookup.method"], "search_after_direct_404")
        self.assertEqual(result[0].provenance["search.bodySha256"], hashlib.sha256(fixture("search-demo-001.html")).hexdigest())
        self.assertEqual([call.url for call in subject.scheduler.transport.calls], [DIRECT.url, SEARCH.url, ALIAS.url])
        subject.scheduler.transport.assert_complete()

    async def test_source_errors_never_become_genuine_absence_or_search_fallback(self):
        for error_response, expected in ((Response(403), "auth_required"), (Response(429), "rate_limited"),
                                         (Response(503), "http_error"), (Response(302), "policy_blocked"),
                                         (Response(200, b"<html>cf-chl-platform</html>"), "challenge"),
                                         (Response(200, b"<html>404 Not Found</html>"), "schema_drift"),
                                         (WorkerError("network", "Synthetic source failure"), "network")):
            subject, errors = provider([(DIRECT, error_response)]), []
            self.assertEqual(await subject.match("DEMO-001", on_error=lambda source, error: errors.append((source, error.code))), [])
            self.assertEqual(errors, [("javbus", expected)])
            self.assertEqual(subject.scheduler.counters("javbus").noMatches, 0)
            self.assertEqual(len(subject.scheduler.transport.calls), 1)

    async def test_wrong_missing_and_ambiguous_detail_ids_are_reported(self):
        for name, expected in (("detail-wrong-id.html", "identity_mismatch"), ("detail-missing-id.html", "schema_drift"),
                               ("detail-ambiguous-id.html", "ambiguous_identity")):
            subject, errors = provider([(DIRECT, response(name))]), []
            self.assertEqual(await subject.match("DEMO-001", on_error=lambda source, error: errors.append(error.code)), [])
            self.assertEqual(errors, [expected])
            self.assertEqual(subject.scheduler.counters("javbus").parserFailures, 1)
            self.assertEqual(subject.scheduler.counters("javbus").noMatches, 0)
            self.assertEqual(len(subject.scheduler.transport.calls), 1)

    async def test_search_ambiguity_error_has_no_detail_request(self):
        subject, errors = provider([(DIRECT, Response(404)), (SEARCH, response("search-ambiguous.html"))]), []
        self.assertEqual(await subject.match("DEMO-001", on_error=lambda source, error: errors.append(error.code)), [])
        self.assertEqual(errors, ["ambiguous_identity"])
        self.assertEqual(subject.scheduler.counters("javbus").noMatches, 0)
        subject.scheduler.transport.assert_complete()

    async def test_explicit_empty_search_and_final_detail_404_are_no_matches(self):
        for scripted in (
            [(DIRECT, Response(404)), (SEARCH, response("search-empty.html"))],
            [(DIRECT, Response(404)), (SEARCH, response("search-demo-001.html")), (ALIAS, Response(404))],
        ):
            subject, errors = provider(scripted), []
            self.assertEqual(await subject.match("DEMO-001", on_error=lambda source, error: errors.append(error.code)), [])
            self.assertEqual(errors, [])
            self.assertEqual(subject.scheduler.counters("javbus").noMatches, 1)
            subject.scheduler.transport.assert_complete()

    async def test_missing_search_endpoint_is_source_error_not_no_match(self):
        subject, errors = provider([(DIRECT, Response(404)), (SEARCH, Response(404))]), []
        self.assertEqual(await subject.match("DEMO-001", on_error=lambda source, error: errors.append(error.code)), [])
        self.assertEqual(errors, ["http_error"])
        self.assertEqual(subject.scheduler.counters("javbus").noMatches, 0)

    async def test_invalid_optional_date_does_not_invent_year(self):
        body = fixture("detail-demo-001.html").replace(b"2024-3-9", b"2024-02-31")
        subject = provider([(DIRECT, Response(200, body, fixture=True))])
        item = (await subject.match("DEMO-001"))[0]
        self.assertIsNone(item.year)
        self.assertIn("invalid_release_date", item.payload()["reviewReasons"])

    async def test_unsupported_sources_and_queries_do_not_request(self):
        for query, sources, expected in (("DEMO-001", ("xcity",), "invalid_sources"),
                                         ("DEMO-001", (), "invalid_sources"),
                                         ("DEMO-001", None, "invalid_sources"),
                                         ("DEMO-001\n", ("javbus",), "unsupported_query"),
                                         ("The Lantern Map", ("javbus",), "unsupported_query"),
                                         ("DEMO-1234567890123", ("javbus",), "unsupported_query")):
            subject, errors = provider(), []
            self.assertEqual(await subject.match(query, sources, lambda source, error: errors.append(error.code)), [])
            self.assertEqual(errors, [expected])
            self.assertEqual(subject.scheduler.transport.calls, [])
            self.assertEqual(subject.scheduler.counters("javbus").noMatches, 0)

    async def test_bf_658_is_only_a_request_transmission_regression(self):
        # Synthetic failure supplies no BF-658 title/date/catalog response and
        # says nothing about the real source's existence, access, or availability.
        request = Request("javbus", JAVBUS_ORIGIN + "/BF-658")
        subject, errors = provider([(request, Response(503))]), []
        self.assertEqual(await subject.match("BF-658", on_error=lambda source, error: errors.append(error.code)), [])
        self.assertEqual(subject.scheduler.transport.calls, [request])
        self.assertEqual(errors, ["http_error"])

    async def test_no_legacy_imports_network_sockets_or_artwork_requests(self):
        subject = provider([(DIRECT, response("detail-demo-001.html"))])
        def blocked(*args, **kwargs):
            raise AssertionError("No network or legacy package may be used")
        loop = asyncio.get_running_loop()
        with (patch.object(socket.socket, "__new__", side_effect=blocked) as socket_call,
              patch("socket.create_connection", side_effect=blocked) as connect_call,
              patch("urllib.request.urlopen", side_effect=blocked) as urlopen_call):
            result = await subject.match("DEMO-001")
            checkpoint = loop.create_future()
            loop.call_soon(checkpoint.set_result, None)
            await checkpoint
            socket_call.assert_not_called()
            connect_call.assert_not_called()
            urlopen_call.assert_not_called()
        self.assertEqual(len(result), 1)
        self.assertEqual(subject.scheduler.transport.calls, [DIRECT])
        self.assertFalse(any(name == "mdcx" or name.startswith("mdcx.") or name.startswith("PyQt") for name in sys.modules))

    async def test_cancellation_propagates_without_error_or_false_no_match(self):
        entered = asyncio.Event()
        class WaitingTransport:
            async def fetch(self, request):
                entered.set()
                await asyncio.Event().wait()
        subject, errors = JavBusProvider(Scheduler(WaitingTransport(), min_interval=0)), []
        task = asyncio.create_task(subject.match("DEMO-001", on_error=lambda source, error: errors.append(error.code)))
        await entered.wait()
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        self.assertEqual(errors, [])
        self.assertEqual(subject.scheduler.counters("javbus").noMatches, 0)
        self.assertEqual(subject.scheduler.counters("javbus").inFlight, 0)


if __name__ == "__main__":
    unittest.main()

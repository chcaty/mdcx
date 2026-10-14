import asyncio
import socket
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from mdcx_worker.adapters import ThePornDbMoviesAdapter, XcityAdapter
from mdcx_worker.models import (
    CatalogIdentifier,
    DetailTarget,
    ProviderIdentity,
    RawMovie,
    Response,
    WorkerError,
    canonical_id,
    comparable_catalog,
    normalize,
)
from mdcx_worker.scheduler import Scheduler
from mdcx_worker.service import Matcher
from mdcx_worker.transports import FixtureTransport

ROOT = Path(__file__).resolve().parents[1] / "fixtures"
XTARGET = DetailTarget(ProviderIdentity("xcity", "1001"), "https://xcity.jp/avod/detail/?id=1001")
TTARGET = DetailTarget(ProviderIdentity("theporndb_movies", "harbor-of-stars"), "https://api.theporndb.net/movies/harbor-of-stars")


def raw(identifier="DEMO-001", title="Good Title", release="", url="https://example.invalid/movie", *, namespace="test:catalog", source="test"):
    return RawMovie(ProviderIdentity(source, "opaque-1"), CatalogIdentifier(namespace, identifier), title, release, url, {})


class ParserTests(unittest.TestCase):
    def test_real_html_detail_selectors_and_nested_text(self):
        body = (ROOT / "xcity-demo-001.html").read_bytes()
        result = normalize(XcityAdapter().parse_detail(body, XTARGET), source="xcity", query="demo001", body=body, fixture=True)
        self.assertEqual((result.id, result.sourceId, result.title, result.year), ("DEMO-001", "1001", "The Lantern Map", 2024))
        self.assertEqual(result.payload()["catalogIdentifier"], {"namespace": "xcity:hinban", "value": "DEMO-001"})
        self.assertEqual(result.provenance["identitySchema"], "2")
        self.assertEqual(result.provenance["confidenceScale"], "heuristic_v1_not_probability")
        self.assertTrue(all(isinstance(value, str) for value in result.provenance.values()))
        self.assertEqual(result.provenance["field.title"], "span#program_detail_title")
        self.assertEqual(result.provenance["fixture"], "true")
        self.assertEqual(result.provenance["responseOrigin"], "synthetic_uncaptured")
        self.assertEqual(result.provenance["field.sourceId.validation"], "response_route_only")

    def test_schema_drift_distinct_from_no_match(self):
        for adapter, body in ((XcityAdapter(), b"<html>changed</html>"), (ThePornDbMoviesAdapter(), b'{"results":[]}')):
            with self.subTest(adapter=adapter.source), self.assertRaises(WorkerError) as caught:
                adapter.parse_search(body)
            self.assertEqual(caught.exception.code, "schema_drift")
        self.assertEqual(ThePornDbMoviesAdapter().parse_search(b'{"data":[]}'), [])
        self.assertEqual(XcityAdapter().parse_search((ROOT / "xcity-search-empty.html").read_bytes()), [])

    def test_malformed_json_and_utf8(self):
        for adapter, body in ((XcityAdapter(), b"\xff"), (ThePornDbMoviesAdapter(), b"{")):
            with self.subTest(adapter=adapter.source), self.assertRaises(WorkerError) as caught:
                adapter.parse_search(body)
            self.assertEqual(caught.exception.code, "parse")

    def test_html_complexity_bound(self):
        with self.assertRaises(WorkerError):
            XcityAdapter().parse_detail(b"<div>" * 300, XTARGET)

    def test_title_script_content_ignored(self):
        result = XcityAdapter().parse_detail(b'<span id="hinban">DEMO-001</span><span id="program_detail_title">Hello<script>wrong</script> Map</span>', XTARGET)
        self.assertEqual(result.title, "Hello Map")

    def test_empty_or_wrong_typed_fields_rejected(self):
        with self.assertRaises(WorkerError):
            ThePornDbMoviesAdapter().parse_detail(b'{"data":{"slug":"harbor-of-stars","title":7}}', TTARGET)
        with self.assertRaises(WorkerError) as caught:
            normalize(raw(title=" "), source="test", query="DEMO-001", body=b"", fixture=True)
        self.assertEqual(caught.exception.code, "validation")

    def test_invalid_calendar_date_has_evidence_not_invented_year(self):
        result = normalize(raw(release="2024-02-31"), source="test", query="DEMO-001", body=b"", fixture=True)
        self.assertIsNone(result.year)
        self.assertIn("invalid_release_date", result.provenance["warnings"])
        self.assertEqual(result.provenance["requiresReview"], "true")

    def test_oversized_raw_whitespace_and_release_are_rejected(self):
        for title, release in ((" " * 2000 + "Title", ""), ("Good Title", "x" * 2000)):
            with self.subTest(title_length=len(title)), self.assertRaises(WorkerError):
                normalize(raw(title=title, release=release), source="test", query="DEMO-001", body=b"", fixture=True)

    def test_unsafe_metadata_url_rejected(self):
        for url in ("javascript:alert(1)", "https://user:secret@example.invalid/", "https://example.invalid/?token=abc"):
            with self.subTest(url=url), self.assertRaises(WorkerError):
                normalize(raw(url=url), source="test", query="DEMO-001", body=b"", fixture=True)

    def test_identifier_mismatch_never_becomes_title_hit(self):
        self.assertIsNone(normalize(raw("DEMO-099", "DEMO-001"), source="test", query="DEMO-001", body=b"", fixture=True))

    def test_catalog_normalization_preserves_editions_zeroes_namespace(self):
        self.assertEqual(canonical_id("demo_001A"), "DEMO-001A")
        for other in ("DEMO-001A", "DEMO-0001", "DEMO/001"):
            self.assertNotEqual(canonical_id(other), canonical_id("DEMO-001"))
        self.assertFalse(comparable_catalog(CatalogIdentifier("one", "DEMO-001"), CatalogIdentifier("two", "DEMO-001")))
        self.assertFalse(comparable_catalog(None, None))
        self.assertTrue(comparable_catalog(CatalogIdentifier("one", "ＤＥＭＯ－００１"), CatalogIdentifier("one", "demo_001")))

    def test_fullwidth_identifier_cannot_bypass_identity_gate(self):
        self.assertIsNone(normalize(raw("DEMO-099", "ＤＥＭＯ－００１"), source="test", query="ＤＥＭＯ－００１", body=b"", fixture=True))
        result = normalize(raw(), source="test", query="ＤＥＭＯ－００１", body=b"", fixture=True)
        self.assertEqual(result.provenance["matchMethod"], "exact_identifier")
        self.assertFalse(result.payload()["requiresReview"])
        self.assertEqual(result.payload()["reviewReasons"], [])

    def test_fuzzy_title_is_reviewable_and_bounded(self):
        result = normalize(raw(title="The Lantern Map"), source="test", query="The Lantarn Map", body=b"", fixture=True)
        self.assertLessEqual(result.confidence, 0.84)
        self.assertTrue(result.payload()["requiresReview"])

    def test_tpdb_never_synthesizes_catalog_number_from_slug(self):
        adapter = ThePornDbMoviesAdapter()
        target = DetailTarget(ProviderIdentity(adapter.source, "DEMO-001"), "https://api.theporndb.net/movies/DEMO-001")
        movie = adapter.parse_detail(b'{"data":{"slug":"DEMO-001","title":"DEMO-001"}}', target)
        self.assertIsNone(movie.catalog_identifier)
        self.assertIsNone(normalize(movie, source=adapter.source, query="DEMO-001", body=b"", fixture=True))


class QualityTests(unittest.IsolatedAsyncioTestCase):
    def matcher(self):
        return Matcher(Scheduler(FixtureTransport(), min_interval=0))

    async def test_fixture_mode_never_opens_a_socket(self):
        from unittest.mock import patch
        loop = asyncio.get_running_loop()
        loop_errors = []
        previous_handler = loop.get_exception_handler()
        loop.set_exception_handler(lambda _loop, context: loop_errors.append(context))
        try:
            # Preserve the class for Windows Proactor isinstance checks on its
            # existing wakeup socket, but reject and count every new socket.
            with (patch.object(socket.socket, "__new__", side_effect=AssertionError("Unexpected network socket")) as socket_call,
                  patch("socket.create_connection", side_effect=AssertionError("Unexpected network connection")) as connect_call,
                  patch("urllib.request.urlopen", side_effect=AssertionError("Unexpected live HTTP")) as urlopen_call):
                result = await self.matcher().match("DEMO-006")
                json_result = await self.matcher().match("Harbor of Stars", ("theporndb_movies",))
                # Run pending loop callbacks while both guards are still active.
                checkpoint = loop.create_future()
                loop.call_soon(checkpoint.set_result, None)
                await checkpoint
                socket_call.assert_not_called()
                connect_call.assert_not_called()
                urlopen_call.assert_not_called()
        finally:
            loop.set_exception_handler(previous_handler)
        self.assertEqual(loop_errors, [], f"Unexpected event-loop exceptions: {loop_errors!r}")
        self.assertIsNone(result[0].year)
        self.assertEqual(json_result[0].year, 2023)

    async def test_priority_is_lazy_no_lower_source_for_complete_match(self):
        matcher = self.matcher()
        result = await matcher.match("DEMO-001")
        self.assertEqual(result[0].source, "xcity")
        self.assertNotIn("theporndb_movies", matcher.scheduler.health)
        self.assertEqual(len(matcher.scheduler.transport.calls), 2)

    async def test_explicit_priority_order_wins_for_supported_title(self):
        matcher = self.matcher()
        result = await matcher.match("The Lantern Map", ("theporndb_movies", "xcity"))
        self.assertEqual(result[0].source, "theporndb_movies")
        self.assertIsNone(result[0].catalogIdentifier)
        self.assertNotIn("xcity", matcher.scheduler.health)

    async def test_second_demo_has_legitimate_xcity_catalog_evidence(self):
        result = await self.matcher().match("DEMO-002")
        self.assertEqual((result[0].id, result[0].sourceId, result[0].source), ("DEMO-002", "1002", "xcity"))

    async def test_missing_year_not_filled_by_unproven_tpdb_identity(self):
        matcher = self.matcher()
        result = (await matcher.match("DEMO-006"))[0]
        self.assertEqual((result.title, result.year, result.source), ("The Quiet Observatory", None, "xcity"))
        self.assertEqual(result.provenance["field.year.source"], "xcity")
        self.assertIn("no_comparable_catalog_identity", result.provenance["rejectedFallback"])
        self.assertTrue(all(call.source == "xcity" for call in matcher.scheduler.transport.calls))

    async def test_conflicting_detail_identifier_errors_without_wrong_id_fallback(self):
        errors = []
        result = await self.matcher().match("DEMO-005", on_error=lambda s, e: errors.append((s, e.code)))
        self.assertEqual(errors, [("xcity", "identity_mismatch")])
        self.assertEqual(result, [])

    async def test_title_only_ambiguity_keeps_both_for_review(self):
        result = await self.matcher().match("The Last Beacon")
        self.assertEqual([candidate.sourceId for candidate in result], ["the-last-beacon-2022", "the-last-beacon-2025"])
        for candidate in result:
            self.assertTrue(candidate.payload()["requiresReview"])
            self.assertIn("ambiguous_candidates", candidate.payload()["reviewReasons"])
            self.assertIn("missing_catalog_identifier", candidate.payload()["reviewReasons"])

    async def test_no_match_is_empty_and_deterministic(self):
        self.assertEqual(await self.matcher().match("No Such Fictional Film"), [])

    async def test_title_provenance_uses_detail_object_and_search_evidence(self):
        result = (await self.matcher().match("Harbor of Stars", ("theporndb_movies",)))[0]
        payload = result.payload()
        self.assertIsNone(payload["catalogIdentifier"])
        self.assertEqual(payload["id"], "theporndb_movies:harbor-of-stars")
        self.assertEqual(result.provenance["field.title"], "data.title")
        self.assertEqual(result.provenance["providerIdentity.value"], "harbor-of-stars")
        self.assertIn("&per_page=100", result.provenance["search.url"])
        self.assertNotEqual(result.provenance["bodySha256"], result.provenance["search.bodySha256"])

    async def test_only_explicit_comparable_catalog_evidence_allows_enrichment(self):
        # These are deliberately fake providers with explicit common namespace;
        # this is not evidence that real TPDB records have a catalog crosswalk.
        from mdcx_worker.models import Request

        class ExplicitCatalogAdapter:
            def __init__(self, source, namespace, title, release):
                self.source, self.namespace, self.title, self.release = source, namespace, title, release
            def supports_query(self, query):
                return True
            def search_request(self, query):
                return Request(self.source, f"https://{self.source}.example.invalid/search")
            def parse_search(self, body):
                return [DetailTarget(ProviderIdentity(self.source, "opaque-1"), f"https://{self.source}.example.invalid/detail")]
            def detail_request(self, target):
                return Request(self.source, target.url)
            def parse_detail(self, body, target):
                return RawMovie(target.provider_identity, CatalogIdentifier(self.namespace, "DEMO-006"),
                                self.title, self.release, target.url, {"title": "test.title", "year": "test.date"})

        class TestTransport:
            async def fetch(self, request):
                return Response(200, request.source.encode(), fixture=True)

        for namespace, expected_year in (("test:declared-catalog", 2021), ("unrelated:catalog", None)):
            with self.subTest(namespace=namespace):
                adapters = {
                    "primary": ExplicitCatalogAdapter("primary", "test:declared-catalog", "Good Primary Title", ""),
                    "secondary": ExplicitCatalogAdapter("secondary", namespace, "Different Secondary Title", "2021-01-02"),
                }
                result = (await Matcher(Scheduler(TestTransport(), min_interval=0), adapters).match("DEMO-006", tuple(adapters)))[0]
                self.assertEqual(result.title, "Good Primary Title")
                self.assertEqual(result.year, expected_year)
                self.assertEqual(result.provenance["field.title.source"], "primary")
                if expected_year:
                    self.assertEqual(result.provenance["field.year.source"], "secondary")
                    self.assertEqual(result.provenance["field.year.identityGate"], "explicit_catalog_namespace_and_value")
                    self.assertIn("conflicting_title", result.payload()["reviewReasons"])
                else:
                    self.assertIn("fallback_identity_conflict", result.payload()["reviewReasons"])


if __name__ == "__main__":
    unittest.main()

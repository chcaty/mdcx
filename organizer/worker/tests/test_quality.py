import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from mdcx_worker.adapters import ThePornDbMoviesAdapter, XcityAdapter
from mdcx_worker.models import RawMovie, Response, WorkerError, canonical_id, normalize
from mdcx_worker.scheduler import Scheduler
from mdcx_worker.service import Matcher
from mdcx_worker.transports import FixtureTransport


class ParserTests(unittest.TestCase):
    def test_real_html_selectors_and_nested_text(self):
        body = (Path(__file__).resolve().parents[1] / "fixtures/xcity-demo-001.html").read_bytes()
        raw = XcityAdapter().parse(body)[0]
        candidate = normalize(raw, source="xcity", query="demo001", body=body, fixture=True)
        self.assertEqual((candidate.id, candidate.title, candidate.year), ("DEMO-001", "The Lantern Map", 2024))
        self.assertEqual(candidate.provenance["confidenceScale"], "heuristic_v1_not_probability")
        self.assertTrue(all(isinstance(value, str) for value in candidate.provenance.values()))
        self.assertEqual(candidate.provenance["field.title"], "span#program_detail_title")
        self.assertEqual(candidate.provenance["fixture"], "true")

    def test_schema_drift_distinct_from_no_match(self):
        for adapter, body in ((XcityAdapter(), b"<html>changed</html>"), (ThePornDbMoviesAdapter(), b'{"results":[]}')):
            with self.subTest(adapter=adapter.source), self.assertRaises(WorkerError) as caught:
                adapter.parse(body)
            self.assertEqual(caught.exception.code, "schema_drift")
        self.assertEqual(ThePornDbMoviesAdapter().parse(b'{"data":[]}'), [])

    def test_malformed_json_and_utf8(self):
        for adapter, body in ((XcityAdapter(), b"\xff"), (ThePornDbMoviesAdapter(), b"{")):
            with self.subTest(adapter=adapter.source), self.assertRaises(WorkerError) as caught:
                adapter.parse(body)
            self.assertEqual(caught.exception.code, "parse")

    def test_html_complexity_bound(self):
        with self.assertRaises(WorkerError):
            XcityAdapter().parse(b"<div>" * 300)

    def test_title_script_content_ignored(self):
        raw = XcityAdapter().parse(b'<span id="hinban">DEMO-001</span><span id="program_detail_title">Hello<script>wrong</script> Map</span>')[0]
        self.assertEqual(raw.title, "Hello Map")

    def test_empty_or_wrong_typed_fields_rejected(self):
        with self.assertRaises(WorkerError):
            ThePornDbMoviesAdapter().parse(b'{"data":[{"slug":"x","title":7}]}')
        raw = RawMovie("DEMO-001", " ", "", "https://example.invalid/movie", {})
        with self.assertRaises(WorkerError) as caught:
            normalize(raw, source="x", query="DEMO-001", body=b"", fixture=True)
        self.assertEqual(caught.exception.code, "validation")

    def test_invalid_calendar_date_has_evidence_not_invented_year(self):
        raw = RawMovie("DEMO-001", "Good Title", "2024-02-31", "https://example.invalid/movie", {})
        result = normalize(raw, source="x", query="DEMO-001", body=b"", fixture=True)
        self.assertIsNone(result.year)
        self.assertIn("invalid_release_date", result.provenance["warnings"])
        self.assertEqual(result.provenance["requiresReview"], "true")

    def test_oversized_raw_whitespace_and_release_are_rejected(self):
        for title, release in ((" " * 2000 + "Title", ""), ("Good Title", "x" * 2000)):
            raw = RawMovie("DEMO-001", title, release, "https://example.invalid/movie", {})
            with self.subTest(title_length=len(title), release_length=len(release)), self.assertRaises(WorkerError):
                normalize(raw, source="x", query="DEMO-001", body=b"", fixture=True)

    def test_unsafe_metadata_url_rejected(self):
        for url in ("javascript:alert(1)", "https://user:secret@example.invalid/", "https://example.invalid/?token=abc"):
            raw = RawMovie("DEMO-001", "Good Title", "", url, {})
            with self.subTest(url=url), self.assertRaises(WorkerError):
                normalize(raw, source="x", query="DEMO-001", body=b"", fixture=True)

    def test_identifier_mismatch_never_becomes_title_hit(self):
        raw = RawMovie("DEMO-099", "DEMO-001", "", "https://example.invalid/movie", {})
        self.assertIsNone(normalize(raw, source="x", query="DEMO-001", body=b"", fixture=True))

    def test_identifier_normalization_preserves_editions_and_zeroes(self):
        self.assertEqual(canonical_id("demo_001A"), "DEMO-001A")
        self.assertNotEqual(canonical_id("DEMO-001A"), canonical_id("DEMO-001"))
        self.assertNotEqual(canonical_id("DEMO-0001"), canonical_id("DEMO-001"))
        self.assertNotEqual(canonical_id("DEMO/001"), canonical_id("DEMO-001"))

    def test_fullwidth_identifier_cannot_bypass_identity_gate(self):
        raw = RawMovie("DEMO-099", "ＤＥＭＯ－００１", "", "https://example.invalid/movie", {})
        self.assertIsNone(normalize(raw, source="x", query="ＤＥＭＯ－００１", body=b"", fixture=True))
        raw = RawMovie("DEMO-001", "Good Title", "", "https://example.invalid/movie", {})
        result = normalize(raw, source="x", query="ＤＥＭＯ－００１", body=b"", fixture=True)
        self.assertEqual(result.provenance["matchMethod"], "exact_identifier")
        self.assertFalse(result.payload()["requiresReview"])
        self.assertEqual(result.payload()["reviewReasons"], [])

    def test_fuzzy_title_is_reviewable_and_bounded(self):
        raw = RawMovie("DEMO-001", "The Lantern Map", "", "https://example.invalid/movie", {})
        result = normalize(raw, source="x", query="The Lantarn Map", body=b"", fixture=True)
        self.assertLessEqual(result.confidence, 0.84)
        self.assertEqual(result.provenance["requiresReview"], "true")


class QualityTests(unittest.IsolatedAsyncioTestCase):
    async def test_fixture_mode_never_opens_a_socket(self):
        from unittest.mock import patch
        with patch("socket.socket", side_effect=AssertionError("Unexpected network socket")):
            result = await self.matcher().match("DEMO-006")
        self.assertEqual(result[0].year, 2021)

    def matcher(self):
        return Matcher(Scheduler(FixtureTransport(), min_interval=0))

    async def test_priority_is_lazy_no_lower_source_for_complete_match(self):
        matcher = self.matcher()
        result = await matcher.match("DEMO-001")
        self.assertEqual(result[0].source, "xcity")
        self.assertNotIn("theporndb_movies", matcher.scheduler.health)

    async def test_explicit_priority_order_wins(self):
        matcher = self.matcher()
        result = await matcher.match("DEMO-001", ("theporndb_movies", "xcity"))
        self.assertEqual(result[0].source, "theporndb_movies")
        self.assertNotIn("xcity", matcher.scheduler.health)

    async def test_fallback_after_no_match(self):
        matcher = self.matcher()
        result = await matcher.match("DEMO-002")
        self.assertEqual((result[0].id, result[0].source), ("DEMO-002", "theporndb_movies"))
        self.assertEqual(matcher.scheduler.counters("xcity").noMatches, 1)

    async def test_missing_year_filled_only_with_exact_identity(self):
        result = (await self.matcher().match("DEMO-006"))[0]
        self.assertEqual((result.title, result.year, result.source), ("The Quiet Observatory", 2021, "xcity"))
        self.assertEqual(result.provenance["field.title.source"], "xcity")
        self.assertEqual(result.provenance["field.year.source"], "theporndb_movies")
        self.assertEqual(result.provenance["field.year.identityGate"], "exact_canonical_identifier")
        self.assertIn("2021", result.provenance["field.year.rawValue"])

    async def test_conflicting_detail_identifier_errors_and_falls_back(self):
        errors = []
        result = await self.matcher().match("DEMO-005", on_error=lambda s, e: errors.append((s, e.code)))
        self.assertEqual(errors, [("xcity", "identity_mismatch")])
        self.assertEqual((result[0].id, result[0].title), ("DEMO-005", "The Mountain Letters"))

    async def test_title_only_ambiguity_keeps_both_for_review(self):
        result = await self.matcher().match("The Last Beacon")
        self.assertEqual([candidate.id for candidate in result], ["DEMO-003", "DEMO-004"])
        self.assertTrue(all(candidate.provenance["requiresReview"] == "true" for candidate in result))
        self.assertTrue(all(candidate.provenance["reviewReasons"] == "ambiguous_candidates" for candidate in result))

    async def test_no_match_is_empty_and_deterministic(self):
        self.assertEqual(await self.matcher().match("No Such Fictional Film"), [])

    async def test_valid_title_survives_conflicting_lower_priority_title(self):
        class ConflictTransport(FixtureTransport):
            async def fetch(self, request):
                if request.source == "theporndb_movies":
                    return Response(200, b'{"data":[{"slug":"DEMO-006","title":"Wrong Lower Priority Title","date":"2021-01-02"}]}', fixture=True)
                return await super().fetch(request)
        matcher = Matcher(Scheduler(ConflictTransport(), min_interval=0))
        result = (await matcher.match("DEMO-006"))[0]
        self.assertEqual(result.title, "The Quiet Observatory")
        self.assertEqual(result.year, 2021)
        self.assertEqual(result.provenance["requiresReview"], "true")
        self.assertEqual(result.provenance["reviewReasons"], "conflicting_title")

    async def test_wrong_identity_cannot_fill_missing_year(self):
        class WrongIdTransport(FixtureTransport):
            async def fetch(self, request):
                if request.source == "theporndb_movies":
                    return Response(200, b'{"data":[{"slug":"DEMO-099","title":"The Quiet Observatory","date":"2021-01-02"}]}', fixture=True)
                return await super().fetch(request)
        result = (await Matcher(Scheduler(WrongIdTransport(), min_interval=0)).match("DEMO-006"))[0]
        self.assertIsNone(result.year)
        self.assertIn("mismatch", result.provenance["rejectedFallback"])

    async def test_empty_bad_fallback_cannot_destroy_valid_prior_fields(self):
        class EmptyTransport(FixtureTransport):
            async def fetch(self, request):
                if request.source == "theporndb_movies":
                    return Response(200, b'{"data":[{"slug":"DEMO-006","title":"","date":"9999-01-02"}]}', fixture=True)
                return await super().fetch(request)
        errors = []
        result = (await Matcher(Scheduler(EmptyTransport(), min_interval=0)).match("DEMO-006", on_error=lambda s,e: errors.append(e.code)))[0]
        self.assertEqual(result.title, "The Quiet Observatory")
        self.assertIsNone(result.year)
        self.assertEqual(errors, ["validation"])

    async def test_exact_identifier_excludes_prefix_false_positive(self):
        class SearchTransport(FixtureTransport):
            async def fetch(self, request):
                return Response(200, b'{"data":[{"slug":"DEMO-0014","title":"First Wrong Hit","date":"2020-01-01"},{"slug":"DEMO-001","title":"Exact Second Hit","date":"2021-01-01"}]}', fixture=True)
        result = await Matcher(Scheduler(SearchTransport(), min_interval=0)).match("DEMO-001", ("theporndb_movies",))
        self.assertEqual(len(result), 1)
        self.assertEqual(result[0].title, "Exact Second Hit")

    async def test_duplicate_exact_ids_require_review(self):
        class DuplicateTransport(FixtureTransport):
            async def fetch(self, request):
                return Response(200, b'{"data":[{"slug":"DEMO-001","title":"Edition One","date":"2020-01-01"},{"slug":"DEMO-001","title":"Edition Two","date":"2021-01-01"}]}', fixture=True)
        result = await Matcher(Scheduler(DuplicateTransport(), min_interval=0)).match("DEMO-001", ("theporndb_movies",))
        self.assertEqual(len(result), 2)
        self.assertTrue(all(candidate.provenance["requiresReview"] == "true" for candidate in result))


if __name__ == "__main__":
    unittest.main()

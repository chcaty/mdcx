"""Offline historical provider contracts, independent of fixture route builders.

Bodies here are authored, fictional test documents. They are not site captures,
current-schema evidence, credentials, or authorization to access either provider.
Expected request URLs/headers are literal so adapter and fixture bugs cannot agree
with one another unnoticed. Legacy evidence: xcity.py main/getWebNumber and
ThePornDB movies main/read_data (search data[] versus detail data{}).
"""
from __future__ import annotations

import asyncio
import json
import socket
import sys
import unittest
from dataclasses import replace
from html import escape
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from mdcx_worker import service
from mdcx_worker.adapters import ThePornDbMoviesAdapter, XcityAdapter
from mdcx_worker.contracts import validate_provider_request
from mdcx_worker.models import (
    CatalogIdentifier,
    DetailTarget,
    ProviderIdentity,
    Request,
    Response,
    WorkerError,
    canonical_id,
    comparable_catalog,
    normalize,
)
from mdcx_worker.protocol import Command, Worker
from mdcx_worker.scheduler import Scheduler
from mdcx_worker.service import Matcher
from mdcx_worker.transports import FixtureTransport, ReplayTransport

XCITY_SEARCH = Request("xcity", "https://xcity.jp/result_published/?q=DEMO001")
XCITY_DETAIL = Request("xcity", "https://xcity.jp/avod/detail/?id=1001")
# Literal offline headers are independent of the adapter. Historical bearer and
# JSON header shapes use an inert marker and an honest offline-tool User-Agent.
# Replacing the inert marker with a real token must fail offline.
TPDB_HEADERS = (
    ("Authorization", "Bearer MDCX_OFFLINE_TEST_MARKER_NOT_A_CREDENTIAL"),
    ("Content-Type", "application/json"),
    ("Accept", "application/json"),
    ("User-Agent", "MDCxOrganizer-OfflineContract/2"),
)
TPDB_SEARCH = Request(
    "theporndb_movies", "https://api.theporndb.net/movies?q=Harbor%20of%20Stars&per_page=100",
    headers=TPDB_HEADERS, credential_ref="fixture:theporndb_movies:nonsecret",
)
TPDB_DETAIL = replace(TPDB_SEARCH, url="https://api.theporndb.net/movies/harbor-of-stars")
XCITY_TARGET = DetailTarget(ProviderIdentity("xcity", "1001"), XCITY_DETAIL.url)
TPDB_TARGET = DetailTarget(ProviderIdentity("theporndb_movies", "harbor-of-stars"), TPDB_DETAIL.url)


def xcity_search(*urls: str) -> bytes:
    links = "".join(f'<tr><td><a href="{escape(url, quote=True)}">Fictional result</a></td></tr>' for url in urls)
    return f'<html><table class="resultList">{links}</table></html>'.encode()


def xcity_detail(identifier="DEMO-001", title="The Lantern Map", release="2024/03/09") -> bytes:
    return (
        f'<html><span id="hinban">{escape(identifier)}</span>'
        f'<span id="program_detail_title">{escape(title)}</span>'
        f'<ul><li><span class="koumoku">発売日</span> {escape(release)}</li></ul></html>'
    ).encode()


def json_body(data) -> bytes:
    return json.dumps({"data": data}, ensure_ascii=False).encode()


def tpdb_movie(slug="harbor-of-stars", title="Harbor of Stars", date="2023-06-17", **extra):
    return {"slug": slug, "title": title, "date": date, **extra}


def matcher(transport):
    return Matcher(Scheduler(transport, min_interval=0, attempts=1))


class LiteralRequestContractTests(unittest.TestCase):
    def test_request_defaults_are_read_only_and_credential_free(self):
        self.assertEqual(XCITY_SEARCH.method, "GET")
        self.assertEqual(XCITY_SEARCH.headers, ())
        self.assertIsNone(XCITY_SEARCH.credential_ref)

    def test_xcity_search_and_detail_requests_match_historical_routes(self):
        adapter = XcityAdapter()
        self.assertEqual(adapter.search_request("DEMO-001"), XCITY_SEARCH)
        self.assertEqual(adapter.detail_request(XCITY_TARGET), XCITY_DETAIL)
        self.assertEqual(adapter.search_request("ｄｅｍｏ＿００１"), XCITY_SEARCH)
        self.assertEqual(validate_provider_request(XCITY_SEARCH), ("search", "DEMO001"))
        self.assertEqual(validate_provider_request(XCITY_DETAIL), ("detail", "1001"))

    def test_tpdb_search_and_detail_requests_include_exact_offline_auth_shape(self):
        adapter = ThePornDbMoviesAdapter()
        self.assertEqual(adapter.search_request("Harbor of Stars"), TPDB_SEARCH)
        self.assertEqual(adapter.detail_request(TPDB_TARGET), TPDB_DETAIL)
        self.assertEqual(validate_provider_request(TPDB_SEARCH), ("search", "Harbor of Stars"))
        self.assertEqual(validate_provider_request(TPDB_DETAIL), ("detail", "harbor-of-stars"))
        self.assertNotIn("MDCX_OFFLINE_TEST_MARKER", repr(TPDB_SEARCH))

    def test_tpdb_declines_catalog_number_queries_including_unicode_and_editions(self):
        adapter = ThePornDbMoviesAdapter()
        self.assertTrue(adapter.supports_query("Harbor of Stars"))
        for query in ("DEMO-001", "demo001", "DEMO_001A", "ＤＥＭＯ－００１", "DEMO-0001"):
            with self.subTest(query=query):
                self.assertFalse(adapter.supports_query(query))

    def test_url_validator_rejects_wrong_origins_paths_queries_and_encodings(self):
        bad_xcity = (
            "http://xcity.jp/avod/detail/?id=1001",
            "https://user:secret@xcity.jp/avod/detail/?id=1001",
            "https://xcity.jp.evil.invalid/avod/detail/?id=1001",
            "https://xcity.jp:444/avod/detail/?id=1001",
            "https://xcity.jp/avod/detail/?id=1001#fragment",
            "https://xcity.jp/avod/detail/?id=1001&token=private",
            "https://xcity.jp/avod/detail/?id=1001&id=1002",
            "https://xcity.jp/avod/detail/?id=01001",
            "https://xcity.jp/avod/detail/?id=%31%30%30%31",
            "https://xcity.jp/avod%2fdetail/?id=1001",
            "https://xcity.jp/avod/%2e%2e/avod/detail/?id=1001",
            "https://xcity.jp/avod/detail/?id=1001/../../other",
            "https://xcity.jp/result_published/?q=DEMO001&q=OTHER001",
            "https://xcity.jp/result_published/?q=DEMO001&token=private",
            "https://xcity.jp/result_published/?q=DEMO001#fragment",
            "https://xcity.jp/result_published/?q=%FF",
            "https://xcity.jp/result_published/?q=%00",
            "https://xcity.jp/result_published/?q=",
            "https://xcity.jp/search?q=DEMO001",
        )
        bad_tpdb = (
            "https://theporndb.net/movies/harbor-of-stars",
            "https://api.theporndb.net.evil.invalid/movies/harbor-of-stars",
            "https://user:secret@api.theporndb.net/movies/harbor-of-stars",
            "https://api.theporndb.net:444/movies/harbor-of-stars",
            "https://api.theporndb.net/movies/harbor-of-stars?token=private",
            "https://api.theporndb.net/movies/harbor-of-stars#fragment",
            "https://api.theporndb.net/movies/../harbor-of-stars",
            "https://api.theporndb.net/movies/%2e%2e/harbor-of-stars",
            "https://api.theporndb.net/movies/harbor%2fof-stars",
            "https://api.theporndb.net/movies/harbor%5cof-stars",
            "https://api.theporndb.net/movies/harbor%252fof-stars",
            "https://api.theporndb.net/movies/harbor/of-stars",
            "https://api.theporndb.net/movies?q=Harbor%20of%20Stars",
            "https://api.theporndb.net/movies?q=Harbor%20of%20Stars&per_page=101",
            "https://api.theporndb.net/movies?q=Harbor%20of%20Stars&per_page=100&q=Other",
            "https://api.theporndb.net/movies?q=Harbor+of+Stars&per_page=100",
            "https://api.theporndb.net/movies?q=Harbor%20of%20Stars&per_page=100&token=private",
        )
        for base, urls in ((XCITY_DETAIL, bad_xcity), (TPDB_DETAIL, bad_tpdb)):
            for url in urls:
                with self.subTest(url=url), self.assertRaises(WorkerError) as raised:
                    validate_provider_request(replace(base, url=url))
                self.assertEqual(raised.exception.code, "policy_blocked")
                self.assertNotIn("private", str(raised.exception))

    def test_methods_headers_and_auth_are_fail_closed(self):
        variants = (
            replace(XCITY_SEARCH, method="POST"),
            replace(TPDB_SEARCH, method="get"),
            replace(XCITY_SEARCH, headers=(("Cookie", "private"),)),
            replace(XCITY_SEARCH, credential_ref="ambient"),
            replace(TPDB_SEARCH, headers=()),
            replace(TPDB_SEARCH, headers=TPDB_HEADERS[1:]),
            replace(TPDB_SEARCH, headers=TPDB_HEADERS + (("Cookie", "private"),)),
            replace(TPDB_SEARCH, headers=(("Authorization", "Bearer private"),) + TPDB_HEADERS[1:]),
            replace(TPDB_SEARCH, headers=TPDB_HEADERS + TPDB_HEADERS[:1]),
            replace(TPDB_SEARCH, credential_ref=None),
            replace(TPDB_SEARCH, credential_ref="ambient"),
            replace(XCITY_SEARCH, source="theporndb_movies"),
        )
        for request in variants:
            with self.subTest(request=request), self.assertRaises(WorkerError) as raised:
                validate_provider_request(request)
            self.assertEqual(raised.exception.code, "policy_blocked")
            self.assertNotIn("private", str(raised.exception))

    def test_detail_request_rejects_provider_identity_url_disagreement(self):
        for adapter, target in ((XcityAdapter(), XCITY_TARGET), (ThePornDbMoviesAdapter(), TPDB_TARGET)):
            wrong = replace(target, provider_identity=ProviderIdentity(adapter.source, "9999"))
            with self.subTest(source=adapter.source), self.assertRaises(WorkerError):
                adapter.detail_request(wrong)
            other_source = replace(target, provider_identity=ProviderIdentity("other", target.provider_identity.value))
            with self.subTest(source=adapter.source, wrong_source=True), self.assertRaises(WorkerError):
                adapter.detail_request(other_source)


class SearchDetailParserContractTests(unittest.TestCase):
    def test_xcity_search_extracts_all_provider_ids_without_inventing_catalog_ids(self):
        targets = XcityAdapter().parse_search(xcity_search("/avod/detail/?id=1001", "/avod/detail/?id=1002"))
        self.assertEqual(targets, [XCITY_TARGET, DetailTarget(ProviderIdentity("xcity", "1002"), "https://xcity.jp/avod/detail/?id=1002")])

    def test_xcity_search_ignores_unrelated_navigation_links(self):
        body = b'<a href="https://other.invalid/">Navigation</a>' + xcity_search("/avod/detail/?id=1001")
        self.assertEqual(XcityAdapter().parse_search(body), [XCITY_TARGET])

    def test_xcity_empty_search_is_distinct_from_detail_or_schema_drift(self):
        adapter = XcityAdapter()
        self.assertEqual(adapter.parse_search("該当する作品はみつかりませんでした".encode()), [])
        self.assertEqual(adapter.parse_search(xcity_search()), [])
        for body in (b"<html>changed schema</html>", xcity_detail()):
            with self.subTest(body=body), self.assertRaises(WorkerError) as raised:
                adapter.parse_search(body)
            self.assertEqual(raised.exception.code, "schema_drift")
        with self.assertRaises(WorkerError) as raised:
            adapter.parse_detail(xcity_search("/avod/detail/?id=1001"), XCITY_TARGET)
        self.assertEqual(raised.exception.code, "schema_drift")

    def test_xcity_optional_canonical_link_cannot_contradict_numeric_route_identity(self):
        adapter = XcityAdapter()
        for href in ("https://xcity.jp/avod/detail/?id=1002", "https://other.invalid/avod/detail/?id=1001"):
            body = f'<link rel="canonical" href="{href}">'.encode() + xcity_detail()
            with self.subTest(href=href), self.assertRaises(WorkerError):
                adapter.parse_detail(body, XCITY_TARGET)
        matching_body = f'<link rel="canonical" href="{XCITY_DETAIL.url}">'.encode() + xcity_detail()
        self.assertEqual(adapter.parse_detail(matching_body, XCITY_TARGET).provider_identity, XCITY_TARGET.provider_identity)
        self.assertEqual(adapter.parse_detail(xcity_detail(), XCITY_TARGET).fields["sourceId.validation"], "response_route_only")

    def test_xcity_duplicate_required_spans_cannot_invent_concatenated_fields(self):
        for body in (
            b'<span id="hinban">DEMO-</span><span id="hinban">001</span><span id="program_detail_title">The Lantern Map</span>',
            b'<span id="hinban">DEMO-001</span><span id="hinban">DEMO-001</span><span id="program_detail_title">The Lantern Map</span>',
            b'<span id="hinban">DEMO-001</span><span id="program_detail_title">The Lantern</span><span id="program_detail_title">Map</span>',
            b'<span id="hinban">DEMO-001</span><span id="program_detail_title">Same Title</span><span id="program_detail_title">Same Title</span>',
        ):
            with self.subTest(body=body), self.assertRaises(WorkerError) as raised:
                XcityAdapter().parse_detail(body, XCITY_TARGET)
            self.assertEqual(raised.exception.code, "schema_drift")

    def test_xcity_required_span_selectors_cannot_be_satisfied_by_divs(self):
        for body in (
            b'<div id="hinban">DEMO-001</div><span id="program_detail_title">The Lantern Map</span>',
            b'<span id="hinban">DEMO-001</span><div id="program_detail_title">The Lantern Map</div>',
            b'<div id="hinban">DEMO-001</div><div id="program_detail_title">The Lantern Map</div>',
        ):
            with self.subTest(body=body), self.assertRaises(WorkerError) as raised:
                XcityAdapter().parse_detail(body, XCITY_TARGET)
            self.assertEqual(raised.exception.code, "schema_drift")

    def test_xcity_detail_keeps_numeric_provider_identity_and_catalog_field_separate(self):
        raw = XcityAdapter().parse_detail(xcity_detail(), XCITY_TARGET)
        self.assertEqual(raw.provider_identity, ProviderIdentity("xcity", "1001"))
        self.assertIsNotNone(raw.catalog_identifier)
        self.assertEqual(raw.catalog_identifier, CatalogIdentifier("xcity:hinban", "DEMO-001"))
        self.assertEqual(raw.url, XCITY_DETAIL.url)
        candidate = normalize(raw, source="xcity", query="DEMO-001", body=xcity_detail(), fixture=True)
        self.assertEqual((candidate.id, candidate.sourceId, candidate.year), ("DEMO-001", "1001", 2024))
        self.assertEqual(candidate.payload()["catalogIdentifier"]["value"], "DEMO-001")
        self.assertEqual(candidate.provenance["responseOrigin"], "synthetic_uncaptured")

    def test_tpdb_search_data_array_returns_detail_targets_and_detail_data_object_returns_movie(self):
        adapter = ThePornDbMoviesAdapter()
        self.assertEqual(adapter.parse_search(json_body([tpdb_movie()])), [TPDB_TARGET])
        raw = adapter.parse_detail(json_body(tpdb_movie()), TPDB_TARGET)
        self.assertEqual(raw.provider_identity, TPDB_TARGET.provider_identity)
        self.assertIsNone(raw.catalog_identifier)
        candidate = normalize(raw, source=adapter.source, query="Harbor of Stars", body=json_body(tpdb_movie()), fixture=True)
        self.assertEqual(candidate.id, "theporndb_movies:harbor-of-stars")
        self.assertEqual(candidate.sourceId, "harbor-of-stars")
        self.assertIn("catalogIdentifier", candidate.payload())
        self.assertIsNone(candidate.payload()["catalogIdentifier"])
        self.assertTrue(candidate.payload()["requiresReview"])
        self.assertIn("missing_catalog_identifier", candidate.payload()["reviewReasons"])

    def test_tpdb_search_and_detail_wrappers_are_not_interchangeable(self):
        adapter = ThePornDbMoviesAdapter()
        for body in (json_body(tpdb_movie()), b'{"results":[]}', b'[]', b'null'):
            with self.subTest(stage="search", body=body), self.assertRaises(WorkerError):
                adapter.parse_search(body)
        for body in (json_body([tpdb_movie()]), b'{"results":{}}', b'[]', b'null'):
            with self.subTest(stage="detail", body=body), self.assertRaises(WorkerError):
                adapter.parse_detail(body, TPDB_TARGET)
        self.assertEqual(adapter.parse_search(json_body([])), [])

    def test_tpdb_wrong_detail_slug_is_rejected_even_when_title_matches(self):
        with self.assertRaises(WorkerError) as raised:
            ThePornDbMoviesAdapter().parse_detail(json_body(tpdb_movie(slug="wrong-slug")), TPDB_TARGET)
        self.assertEqual(raised.exception.code, "identity_mismatch")

    def test_tpdb_malformed_duplicate_key_and_nonfinite_json_fail(self):
        for body in (
            b"\xff", b"{", b'{"data":[],"data":[]}',
            b'{"data":[{"slug":"harbor-of-stars","slug":"other","title":"Harbor of Stars"}]}',
            b'{"data":[],"unexpected":NaN}',
        ):
            with self.subTest(body=body), self.assertRaises(WorkerError) as raised:
                ThePornDbMoviesAdapter().parse_search(body)
            self.assertEqual(raised.exception.code, "parse")

    def test_malformed_search_rows_and_unbounded_result_counts_fail(self):
        adapter = ThePornDbMoviesAdapter()
        for rows in ([None], [{}], [{"slug": "harbor-of-stars", "title": 7}], [{"slug": "harbor-of-stars", "title": " "}]):
            with self.subTest(rows=rows), self.assertRaises(WorkerError):
                adapter.parse_search(json_body(rows))
        with self.assertRaises(WorkerError) as raised:
            adapter.parse_search(json_body([tpdb_movie()] * 101))
        self.assertEqual(raised.exception.code, "response_too_large")

    def test_catalog_shaped_tpdb_slug_never_becomes_a_catalog_number(self):
        adapter = ThePornDbMoviesAdapter()
        target = DetailTarget(ProviderIdentity(adapter.source, "demo-001"), "https://api.theporndb.net/movies/demo-001")
        body = json_body(tpdb_movie(slug="demo-001", title="DEMO-001", number="DEMO-001", catalog_id="DEMO-001"))
        raw = adapter.parse_detail(body, target)
        self.assertIsNone(raw.catalog_identifier)
        self.assertIsNone(normalize(raw, source=adapter.source, query="DEMO-001", body=body, fixture=True))

    def test_tpdb_slug_identity_is_opaque_not_catalog_canonicalized(self):
        adapter = ThePornDbMoviesAdapter()
        for slug in ("demo-001", "demo_001", "DEMO-001", "demo-0001", "demo-001A"):
            with self.subTest(slug=slug):
                target = adapter.parse_search(json_body([tpdb_movie(slug=slug)]))[0]
                self.assertEqual(target.provider_identity.value, slug)
                raw = adapter.parse_detail(json_body(tpdb_movie(slug=slug)), target)
                result = normalize(raw, source=adapter.source, query="Harbor of Stars", body=b"", fixture=True)
                self.assertEqual(result.id, f"theporndb_movies:{slug}")
                self.assertEqual(result.sourceId, slug)

    def test_duplicate_search_entries_keep_review_evidence(self):
        xcity = XcityAdapter().parse_search(xcity_search("/avod/detail/?id=1001", "/avod/detail/?id=1001"))
        tpdb = ThePornDbMoviesAdapter().parse_search(json_body([tpdb_movie(), tpdb_movie()]))
        for targets in (xcity, tpdb):
            with self.subTest(source=targets[0].provider_identity.source):
                self.assertEqual(len(targets), 1)
                self.assertEqual(targets[0].duplicate_count, 2)

    def test_unsafe_xcity_search_targets_are_rejected_before_any_request(self):
        for url in (
            "//other.invalid/avod/detail/?id=1001", "https://other.invalid/avod/detail/?id=1001",
            "/avod/detail/?id=1001&token=private", "/avod/detail/?id=1001&id=1002",
            "/avod/detail/?id=1001#fragment", "/avod%2fdetail/?id=1001",
            "/avod/%2e%2e/avod/detail/?id=1001", "javascript:alert(1)",
        ):
            with self.subTest(url=url), self.assertRaises(WorkerError):
                XcityAdapter().parse_search(xcity_search(url))

    def test_unsafe_tpdb_search_slugs_are_rejected_before_any_request(self):
        for slug in ("", "../other", "harbor/stars", "harbor\\stars", "%2Fother", "%252fother", "a?token=private", "a#fragment", "灯台", "a\n"):
            with self.subTest(slug=slug), self.assertRaises(WorkerError):
                ThePornDbMoviesAdapter().parse_search(json_body([tpdb_movie(slug=slug)]))

    def test_unicode_catalog_equivalence_keeps_zeros_and_editions(self):
        self.assertEqual(canonical_id("ＤＥＭＯ－００１Ａ"), "DEMO-001A")
        base = CatalogIdentifier("catalog", "DEMO-001")
        self.assertTrue(comparable_catalog(base, CatalogIdentifier("catalog", "ｄｅｍｏ＿００１")))
        for other in (CatalogIdentifier("other", "DEMO-001"), CatalogIdentifier("catalog", "DEMO-0001"), CatalogIdentifier("catalog", "DEMO-001A"), None):
            with self.subTest(other=other):
                self.assertFalse(comparable_catalog(base, other))

    def test_unicode_catalog_queries_do_not_degrade_to_matching_titles(self):
        raw = XcityAdapter().parse_detail(xcity_detail(identifier="DEMO-099", title="ＤＥＭＯ－００１"), XCITY_TARGET)
        self.assertIsNone(normalize(raw, source="xcity", query="ＤＥＭＯ－００１", body=b"", fixture=True))
        matching = XcityAdapter().parse_detail(xcity_detail(identifier="ＤＥＭＯ－００１"), XCITY_TARGET)
        result = normalize(matching, source="xcity", query="demo001", body=b"", fixture=True)
        self.assertEqual(result.id, "DEMO-001")
        self.assertEqual(result.provenance["matchMethod"], "exact_identifier")
        self.assertFalse(result.payload()["requiresReview"])

    def test_catalog_editions_and_zero_padding_cannot_match_base_identity(self):
        for identifier in ("DEMO-001A", "DEMO-0001", "DEMO-0014"):
            raw = XcityAdapter().parse_detail(xcity_detail(identifier=identifier), XCITY_TARGET)
            with self.subTest(identifier=identifier):
                self.assertIsNone(normalize(raw, source="xcity", query="DEMO-001", body=b"", fixture=True))

    def test_normalize_rejects_url_identity_disagreement(self):
        raw = ThePornDbMoviesAdapter().parse_detail(json_body(tpdb_movie()), TPDB_TARGET)
        for url in ("https://api.theporndb.net/movies/other-slug", "https://other.invalid/movies/harbor-of-stars", "https://api.theporndb.net/movies/harbor-of-stars?token=private"):
            with self.subTest(url=url), self.assertRaises(WorkerError):
                normalize(replace(raw, url=url), source="theporndb_movies", query="Harbor of Stars", body=b"", fixture=True)


class OfflineReplayContractTests(unittest.IsolatedAsyncioTestCase):
    async def test_xcity_exact_search_then_detail_request_sequence(self):
        transport = ReplayTransport([
            (XCITY_SEARCH, Response(200, xcity_search("/avod/detail/?id=1001"), fixture=True)),
            (XCITY_DETAIL, Response(200, xcity_detail(), fixture=True)),
        ])
        result = await matcher(transport).match("DEMO-001", ("xcity",))
        self.assertEqual([(item.id, item.sourceId, item.title) for item in result], [("DEMO-001", "1001", "The Lantern Map")])
        self.assertEqual(transport.calls, [XCITY_SEARCH, XCITY_DETAIL])
        self.assertEqual(result[0].provenance["search.url"], XCITY_SEARCH.url)
        self.assertNotEqual(result[0].provenance["search.bodySha256"], result[0].provenance["bodySha256"])
        transport.assert_complete()

    async def test_tpdb_exact_search_then_detail_request_sequence(self):
        transport = ReplayTransport([
            (TPDB_SEARCH, Response(200, json_body([tpdb_movie()]), fixture=True)),
            (TPDB_DETAIL, Response(200, json_body(tpdb_movie()), fixture=True)),
        ])
        result = await matcher(transport).match("Harbor of Stars", ("theporndb_movies",))
        self.assertEqual([(item.id, item.year) for item in result], [("theporndb_movies:harbor-of-stars", 2023)])
        self.assertEqual(transport.calls, [TPDB_SEARCH, TPDB_DETAIL])
        transport.assert_complete()

    async def test_replay_rejects_reordered_unexpected_and_exhausted_requests(self):
        transport = ReplayTransport([(XCITY_SEARCH, Response(200, b"search", fixture=True))])
        for request in (XCITY_DETAIL, replace(XCITY_SEARCH, url="https://xcity.jp/result_published/?q=DEMO002"), TPDB_SEARCH):
            with self.subTest(request=request), self.assertRaises(WorkerError) as raised:
                await transport.fetch(request)
            self.assertEqual(raised.exception.code, "replay_mismatch")
            self.assertEqual(transport.calls, [])
            with self.assertRaises(AssertionError):
                transport.assert_complete()
        self.assertEqual((await transport.fetch(XCITY_SEARCH)).body, b"search")
        transport.assert_complete()
        with self.assertRaises(WorkerError) as raised:
            await transport.fetch(XCITY_SEARCH)
        self.assertEqual(raised.exception.code, "replay_mismatch")
        self.assertEqual(transport.calls, [XCITY_SEARCH])

    async def test_replay_checks_policy_even_when_invalid_request_is_expected(self):
        for request in (
            replace(XCITY_SEARCH, method="POST"),
            replace(XCITY_SEARCH, url="https://xcity.example.invalid/search?q=DEMO001"),
            replace(TPDB_SEARCH, headers=(("Authorization", "Bearer private"),) + TPDB_HEADERS[1:]),
            replace(TPDB_SEARCH, credential_ref="ambient"),
        ):
            with self.subTest(request=request):
                transport = ReplayTransport([(request, Response(200))])
                with self.assertRaises(WorkerError) as raised:
                    await transport.fetch(request)
                self.assertEqual(raised.exception.code, "policy_blocked")
                self.assertNotIn("private", str(raised.exception))
                self.assertEqual(transport.calls, [])
                with self.assertRaises(AssertionError):
                    transport.assert_complete()

    async def test_fixture_checks_method_url_headers_auth_before_returning_data(self):
        transport = FixtureTransport()
        for request in (
            replace(XCITY_SEARCH, method="POST"),
            replace(XCITY_SEARCH, url="https://xcity.jp/search?q=DEMO001"),
            replace(XCITY_SEARCH, url="https://xcity.example.invalid/search?q=DEMO001"),
            replace(XCITY_DETAIL, url="https://xcity.jp/avod/detail/?id=1001&token=private"),
            replace(XCITY_SEARCH, headers=(("Cookie", "private"),)),
            replace(TPDB_SEARCH, headers=()),
            replace(TPDB_SEARCH, credential_ref=None),
            replace(TPDB_SEARCH, headers=(("Authorization", "Bearer private"),) + TPDB_HEADERS[1:]),
        ):
            with self.subTest(request=request), self.assertRaises(WorkerError) as raised:
                await transport.fetch(request)
            self.assertEqual(raised.exception.code, "policy_blocked")
        self.assertEqual(transport.calls, [])

    async def test_fixture_bodies_obey_distinct_search_and_detail_wrappers(self):
        transport = FixtureTransport()
        adapter = ThePornDbMoviesAdapter()
        search = await transport.fetch(TPDB_SEARCH)
        detail = await transport.fetch(TPDB_DETAIL)
        self.assertIsInstance(json.loads(search.body)["data"], list)
        self.assertIsInstance(json.loads(detail.body)["data"], dict)
        self.assertEqual(adapter.parse_search(search.body), [TPDB_TARGET])
        self.assertEqual(adapter.parse_detail(detail.body, TPDB_TARGET).provider_identity, TPDB_TARGET.provider_identity)
        self.assertTrue(search.fixture and detail.fixture)

    async def test_empty_search_never_fetches_a_detail(self):
        for query, source, request, body in (
            ("DEMO-001", "xcity", XCITY_SEARCH, xcity_search()),
            ("Harbor of Stars", "theporndb_movies", TPDB_SEARCH, json_body([])),
        ):
            with self.subTest(source=source):
                transport = ReplayTransport([(request, Response(200, body, fixture=True))])
                self.assertEqual(await matcher(transport).match(query, (source,)), [])
                transport.assert_complete()

    async def test_catalog_query_never_sends_a_tpdb_request(self):
        transport = ReplayTransport([])
        self.assertEqual(await matcher(transport).match("ＤＥＭＯ－００１", ("theporndb_movies",)), [])
        transport.assert_complete()

    async def test_missing_xcity_year_cannot_be_filled_by_a_tpdb_slug(self):
        transport = ReplayTransport([
            (XCITY_SEARCH, Response(200, xcity_search("/avod/detail/?id=1001"), fixture=True)),
            (XCITY_DETAIL, Response(200, xcity_detail(release=""), fixture=True)),
        ])
        result = await matcher(transport).match("DEMO-001")
        self.assertEqual(len(result), 1)
        self.assertIsNone(result[0].year)
        self.assertEqual(result[0].source, "xcity")
        transport.assert_complete()

    async def test_wrong_xcity_catalog_identifier_never_becomes_a_result(self):
        transport = ReplayTransport([
            (XCITY_SEARCH, Response(200, xcity_search("/avod/detail/?id=1001"), fixture=True)),
            (XCITY_DETAIL, Response(200, xcity_detail(identifier="DEMO-099", title="DEMO-001"), fixture=True)),
        ])
        result = await matcher(transport).match("DEMO-001", ("xcity",))
        self.assertEqual(result, [])
        transport.assert_complete()

    async def test_xcity_unrelated_first_hit_does_not_hide_later_exact_catalog_match(self):
        wrong_detail = replace(XCITY_DETAIL, url="https://xcity.jp/avod/detail/?id=1014")
        transport = ReplayTransport([
            (XCITY_SEARCH, Response(200, xcity_search("/avod/detail/?id=1014", "/avod/detail/?id=1001"), fixture=True)),
            (wrong_detail, Response(200, xcity_detail(identifier="DEMO-0014"), fixture=True)),
            (XCITY_DETAIL, Response(200, xcity_detail(), fixture=True)),
        ])
        errors = []
        result = await matcher(transport).match("DEMO-001", ("xcity",), on_error=lambda source, error, errors=errors: errors.append((source, error.code)))
        self.assertEqual([(item.id, item.sourceId) for item in result], [("DEMO-001", "1001")])
        self.assertEqual(errors, [("xcity", "identity_mismatch")])
        self.assertFalse(result[0].payload()["requiresReview"])
        self.assertNotIn("incomplete_detail_results", result[0].payload()["reviewReasons"])
        transport.assert_complete()

    async def test_xcity_later_schema_or_http_failure_preserves_prior_valid_candidate_for_review(self):
        other_detail = replace(XCITY_DETAIL, url="https://xcity.jp/avod/detail/?id=1002")
        for failure, code, parser_failures in (
            (Response(200, b"<html>schema changed</html>", fixture=True), "schema_drift", 1),
            (Response(401, fixture=True), "auth_required", 0),
        ):
            with self.subTest(code=code):
                transport = ReplayTransport([
                    (XCITY_SEARCH, Response(200, xcity_search("/avod/detail/?id=1001", "/avod/detail/?id=1002"), fixture=True)),
                    (XCITY_DETAIL, Response(200, xcity_detail(), fixture=True)),
                    (other_detail, failure),
                ])
                subject, errors = matcher(transport), []
                result = await subject.match("DEMO-001", ("xcity",), on_error=lambda source, error, errors=errors: errors.append((source, error.code)))
                self.assertEqual([(item.id, item.sourceId, item.year) for item in result], [("DEMO-001", "1001", 2024)])
                self.assertEqual(errors, [("xcity", code)])
                self.assertEqual(subject.scheduler.counters("xcity").parserFailures, parser_failures)
                self.assertTrue(result[0].payload()["requiresReview"])
                self.assertIn("incomplete_detail_results", result[0].payload()["reviewReasons"])
                transport.assert_complete()

    async def test_tpdb_later_malformed_json_or_http_failure_preserves_prior_candidate(self):
        other = tpdb_movie(slug="harbor-of-stars-alt")
        other_request = replace(TPDB_DETAIL, url="https://api.theporndb.net/movies/harbor-of-stars-alt")
        for failure, code, parser_failures in (
            (Response(200, b"{", fixture=True), "parse", 1),
            (Response(401, fixture=True), "auth_required", 0),
        ):
            with self.subTest(code=code):
                transport = ReplayTransport([
                    (TPDB_SEARCH, Response(200, json_body([tpdb_movie(), other]), fixture=True)),
                    (TPDB_DETAIL, Response(200, json_body(tpdb_movie()), fixture=True)),
                    (other_request, failure),
                ])
                subject, errors = matcher(transport), []
                result = await subject.match("Harbor of Stars", ("theporndb_movies",), on_error=lambda source, error, errors=errors: errors.append((source, error.code)))
                self.assertEqual([item.sourceId for item in result], ["harbor-of-stars"])
                self.assertEqual(errors, [("theporndb_movies", code)])
                self.assertEqual(subject.scheduler.counters("theporndb_movies").parserFailures, parser_failures)
                self.assertTrue(result[0].payload()["requiresReview"])
                self.assertIn("incomplete_detail_results", result[0].payload()["reviewReasons"])
                transport.assert_complete()

    async def test_detail_access_failure_stops_before_third_target_and_preserves_first(self):
        second_detail = replace(XCITY_DETAIL, url="https://xcity.jp/avod/detail/?id=1002")
        for failure, code in (
            (Response(401, fixture=True), "auth_required"),
            (Response(200, b'<html><div class="g-recaptcha"></div></html>', fixture=True), "challenge"),
            (Response(302, headers={"Location": "https://other.invalid/"}, fixture=True), "policy_blocked"),
            (Response(429, fixture=True), "rate_limited"),
        ):
            with self.subTest(code=code):
                transport = ReplayTransport([
                    (XCITY_SEARCH, Response(200, xcity_search("/avod/detail/?id=1001", "/avod/detail/?id=1002", "/avod/detail/?id=1003"), fixture=True)),
                    (XCITY_DETAIL, Response(200, xcity_detail(), fixture=True)),
                    (second_detail, failure),
                ])
                subject, errors = matcher(transport), []
                result = await subject.match("DEMO-001", ("xcity",), on_error=lambda source, error, errors=errors: errors.append((source, error.code)))
                self.assertEqual([(item.id, item.sourceId) for item in result], [("DEMO-001", "1001")])
                self.assertEqual(errors, [("xcity", code)])
                self.assertEqual(subject.scheduler.counters("xcity").parserFailures, 0)
                self.assertTrue(result[0].payload()["requiresReview"])
                self.assertIn("incomplete_detail_results", result[0].payload()["reviewReasons"])
                self.assertEqual(transport.calls, [XCITY_SEARCH, XCITY_DETAIL, second_detail])
                transport.assert_complete()

    async def test_wrong_second_tpdb_slug_retains_first_with_incomplete_review(self):
        second_movie = tpdb_movie(slug="harbor-of-stars-alt")
        second_detail = replace(TPDB_DETAIL, url="https://api.theporndb.net/movies/harbor-of-stars-alt")
        transport = ReplayTransport([
            (TPDB_SEARCH, Response(200, json_body([tpdb_movie(), second_movie]), fixture=True)),
            (TPDB_DETAIL, Response(200, json_body(tpdb_movie()), fixture=True)),
            (second_detail, Response(200, json_body(tpdb_movie(slug="wrong-response-slug")), fixture=True)),
        ])
        subject, errors = matcher(transport), []
        result = await subject.match("Harbor of Stars", ("theporndb_movies",), on_error=lambda source, error: errors.append((source, error.code)))
        self.assertEqual([item.sourceId for item in result], ["harbor-of-stars"])
        self.assertEqual(errors, [("theporndb_movies", "identity_mismatch")])
        self.assertEqual(subject.scheduler.counters("theporndb_movies").parserFailures, 1)
        self.assertTrue(result[0].payload()["requiresReview"])
        self.assertIn("incomplete_detail_results", result[0].payload()["reviewReasons"])
        transport.assert_complete()

    async def test_detail_request_budget_truncates_xcity_and_marks_retained_candidates(self):
        self.assertEqual(service.MAX_DETAIL_REQUESTS, 10)
        for count in (11, 25):
            with self.subTest(search_target_count=count):
                ids = [str(2000 + index) for index in range(count)]
                expected = [(XCITY_SEARCH, Response(200, xcity_search(*(f"/avod/detail/?id={value}" for value in ids)), fixture=True))]
                expected.extend((replace(XCITY_DETAIL, url=f"https://xcity.jp/avod/detail/?id={value}"), Response(200, xcity_detail(), fixture=True)) for value in ids[:10])
                transport, errors = ReplayTransport(expected), []
                result = await matcher(transport).match("DEMO-001", ("xcity",), on_error=lambda source, error, errors=errors: errors.append((source, error.code)))
                self.assertEqual(len(result), 10)
                self.assertEqual({item.sourceId for item in result}, set(ids[:10]))
                self.assertEqual(errors, [("xcity", "search_truncated")])
                self.assertTrue(all(item.payload()["requiresReview"] for item in result))
                self.assertTrue(all("search_results_truncated" in item.payload()["reviewReasons"] for item in result))
                self.assertEqual(len(transport.calls), 11)
                transport.assert_complete()

    async def test_detail_request_budget_truncates_tpdb_and_marks_retained_candidates(self):
        movies = [tpdb_movie(slug=f"harbor-of-stars-{index}") for index in range(11)]
        expected = [(TPDB_SEARCH, Response(200, json_body(movies), fixture=True))]
        expected.extend((replace(TPDB_DETAIL, url=f"https://api.theporndb.net/movies/{movie['slug']}"), Response(200, json_body(movie), fixture=True)) for movie in movies[:10])
        transport, errors = ReplayTransport(expected), []
        result = await matcher(transport).match("Harbor of Stars", ("theporndb_movies",), on_error=lambda source, error, errors=errors: errors.append((source, error.code)))
        self.assertEqual({item.sourceId for item in result}, {movie["slug"] for movie in movies[:10]})
        self.assertEqual(errors, [("theporndb_movies", "search_truncated")])
        self.assertTrue(all(item.payload()["requiresReview"] for item in result))
        self.assertTrue(all("search_results_truncated" in item.payload()["reviewReasons"] for item in result))
        self.assertEqual(len(transport.calls), 11)
        transport.assert_complete()

    async def test_truncated_search_with_no_candidates_is_not_protocol_clean_no_match(self):
        movies = [tpdb_movie(slug=f"harbor-of-stars-{index}") for index in range(11)]
        expected = [(TPDB_SEARCH, Response(200, json_body(movies), fixture=True))]
        expected.extend((replace(TPDB_DETAIL, url=f"https://api.theporndb.net/movies/{movie['slug']}"), Response(200, json_body({**movie, "title": "ZZZZZ"}), fixture=True)) for movie in movies[:10])
        transport, events = ReplayTransport(expected), []
        worker = Worker(matcher(transport), events.append)
        worker.submit(Command("truncated-no-candidates", "match", "Harbor of Stars", ("theporndb_movies",)))
        await worker.drain()
        errors = [event for event in events if event["type"] == "error"]
        self.assertEqual([event["error"]["code"] for event in errors], ["search_truncated"])
        completed = [event for event in events if event["type"] == "completed"]
        self.assertEqual(len(completed), 1)
        self.assertEqual(completed[0]["count"], 0)
        self.assertEqual(completed[0]["status"], "sources_failed")
        self.assertEqual(completed[0]["sourceFailures"], 1)
        self.assertFalse(any(event["type"] == "candidate" for event in events))
        self.assertEqual(len(transport.calls), 11)
        transport.assert_complete()

    async def test_cancellation_during_detail_stops_later_details_fallback_and_candidates(self):
        entered, cancelled, release = asyncio.Event(), asyncio.Event(), asyncio.Event()

        class PausedDetailTransport(ReplayTransport):
            async def fetch(self, request):
                response = await super().fetch(request)
                if request == XCITY_DETAIL:
                    entered.set()
                    try:
                        await release.wait()
                    except asyncio.CancelledError:
                        cancelled.set()
                        raise
                return response

        search_request = replace(XCITY_SEARCH, url="https://xcity.jp/result_published/?q=The%20Lantern%20Map")
        transport = PausedDetailTransport([
            (search_request, Response(200, xcity_search("/avod/detail/?id=1001", "/avod/detail/?id=1002"), fixture=True)),
            (XCITY_DETAIL, Response(200, xcity_detail(), fixture=True)),
        ])
        subject, events = matcher(transport), []
        worker = Worker(subject, events.append)
        worker.submit(Command("cancel-detail", "match", "The Lantern Map", ("xcity", "theporndb_movies")))
        await asyncio.wait_for(entered.wait(), 1)
        self.assertEqual(subject.scheduler.counters("xcity").inFlight, 1)
        worker.submit(Command("cancel-detail", "cancel"))
        await asyncio.wait_for(worker.drain(), 1)
        self.assertTrue(cancelled.is_set())
        self.assertEqual(subject.scheduler.counters("xcity").inFlight, 0)
        self.assertEqual(subject.scheduler.counters("xcity").cancellations, 1)
        self.assertNotIn("theporndb_movies", subject.scheduler.health)
        self.assertEqual(transport.calls, [search_request, XCITY_DETAIL])
        self.assertEqual([event["type"] for event in events], ["cancelled"])
        self.assertTrue(events[0]["terminal"])
        transport.assert_complete()

    async def test_wrong_tpdb_detail_slug_is_reported_without_returning_metadata(self):
        transport = ReplayTransport([
            (TPDB_SEARCH, Response(200, json_body([tpdb_movie()]), fixture=True)),
            (TPDB_DETAIL, Response(200, json_body(tpdb_movie(slug="other-movie")), fixture=True)),
        ])
        errors = []
        self.assertEqual(await matcher(transport).match("Harbor of Stars", ("theporndb_movies",), on_error=lambda source, error, errors=errors: errors.append((source, error.code))), [])
        self.assertEqual(errors, [("theporndb_movies", "identity_mismatch")])
        transport.assert_complete()

    async def test_duplicate_search_hit_fetches_detail_once_and_requires_review(self):
        for query, source, search, detail, search_body, detail_body in (
            ("DEMO-001", "xcity", XCITY_SEARCH, XCITY_DETAIL,
             xcity_search("/avod/detail/?id=1001", "/avod/detail/?id=1001"), xcity_detail()),
            ("Harbor of Stars", "theporndb_movies", TPDB_SEARCH, TPDB_DETAIL,
             json_body([tpdb_movie(), tpdb_movie()]), json_body(tpdb_movie())),
        ):
            with self.subTest(source=source):
                transport = ReplayTransport([(search, Response(200, search_body, fixture=True)), (detail, Response(200, detail_body, fixture=True))])
                result = await matcher(transport).match(query, (source,))
                self.assertEqual(len(result), 1)
                self.assertTrue(result[0].payload()["requiresReview"])
                self.assertTrue(any("duplicate" in reason for reason in result[0].payload()["reviewReasons"]))
                transport.assert_complete()

    async def test_distinct_provider_candidates_with_same_title_remain_ambiguous(self):
        other = tpdb_movie(slug="harbor-of-stars-alt", date="2022-01-01")
        other_request = replace(TPDB_DETAIL, url="https://api.theporndb.net/movies/harbor-of-stars-alt")
        transport = ReplayTransport([
            (TPDB_SEARCH, Response(200, json_body([tpdb_movie(), other]), fixture=True)),
            (TPDB_DETAIL, Response(200, json_body(tpdb_movie()), fixture=True)),
            (other_request, Response(200, json_body(other), fixture=True)),
        ])
        result = await matcher(transport).match("Harbor of Stars", ("theporndb_movies",))
        self.assertEqual({item.sourceId for item in result}, {"harbor-of-stars", "harbor-of-stars-alt"})
        self.assertTrue(all(item.payload()["requiresReview"] for item in result))
        self.assertTrue(all("ambiguous_candidates" in item.payload()["reviewReasons"] for item in result))
        transport.assert_complete()

    async def test_fixture_and_replay_matchers_never_open_network_connections(self):
        def forbidden(*_args, **_kwargs):
            raise AssertionError("Offline contracts must never perform live HTTP")
        loop = asyncio.get_running_loop()
        loop_errors = []
        previous_handler = loop.get_exception_handler()
        loop.set_exception_handler(lambda _loop, context: loop_errors.append(context))
        try:
            # Keep socket.socket a class: Windows Proactor checks its existing
            # wakeup socket with isinstance, without opening a network socket.
            with (patch.object(socket.socket, "__new__", side_effect=forbidden) as socket_call,
                  patch("socket.create_connection", side_effect=forbidden) as connect_call,
                  patch("urllib.request.urlopen", side_effect=forbidden) as urlopen_call):
                fixtures = FixtureTransport()
                number = await matcher(fixtures).match("DEMO-001")
                title = await matcher(fixtures).match("Harbor of Stars", ("theporndb_movies",))
                replay = ReplayTransport([(XCITY_SEARCH, Response(200, xcity_search("/avod/detail/?id=1001"), fixture=True)), (XCITY_DETAIL, Response(200, xcity_detail(), fixture=True))])
                replayed = await matcher(replay).match("DEMO-001", ("xcity",))
                replay.assert_complete()
                # Drain pending callbacks before restoring guards or the handler.
                checkpoint = loop.create_future()
                loop.call_soon(checkpoint.set_result, None)
                await checkpoint
                socket_call.assert_not_called()
                connect_call.assert_not_called()
                urlopen_call.assert_not_called()
        finally:
            loop.set_exception_handler(previous_handler)
        self.assertEqual(loop_errors, [], f"Unexpected event-loop exceptions: {loop_errors!r}")
        self.assertEqual(number[0].id, "DEMO-001")
        self.assertEqual(title[0].id, "theporndb_movies:harbor-of-stars")
        self.assertEqual(replayed[0].sourceId, "1001")
        self.assertTrue(all(item.provenance["fixture"] == "true" for item in number + title + replayed))


if __name__ == "__main__":
    unittest.main()

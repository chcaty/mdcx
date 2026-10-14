"""Historical search/detail subsets adapted from the two named legacy crawlers.

Lineage: xcity.py getTitle/getWebNumber/getRelease/main and detail URL example;
theporndb_movies.py main search/detail wrappers and read_data detail fields.
No legacy imports, live-source claim, filename matcher, or all-provider migration.
"""
from __future__ import annotations

import json
import re
import unicodedata
from dataclasses import replace
from html.parser import HTMLParser
from typing import Protocol
from urllib.parse import quote

from .contracts import (
    TPDB_CREDENTIAL_REF,
    TPDB_HEADERS,
    TPDB_ORIGIN,
    XCITY_ORIGIN,
    detail_identity,
    valid_slug,
)
from .models import (
    CatalogIdentifier,
    DetailTarget,
    ProviderIdentity,
    RawMovie,
    Request,
    WorkerError,
    canonical_id,
    is_catalog_identifier,
)


class Adapter(Protocol):
    source: str
    def supports_query(self, query: str) -> bool: ...
    def search_request(self, query: str) -> Request: ...
    def parse_search(self, body: bytes) -> list[DetailTarget]: ...
    def detail_request(self, target: DetailTarget) -> Request: ...
    def parse_detail(self, body: bytes, target: DetailTarget) -> RawMovie: ...


class _XcityHTML(HTMLParser):
    """Bounded descendant text and historical table links, never script execution."""
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.stack: list[tuple[str, dict[str, str]]] = []
        self.fields: dict[str, list[str]] = {}
        self.field_occurrences: dict[str, int] = {}
        self.list_items: list[list[str]] = []
        self.release_items: list[list[str]] = []
        self.current_li: list[str] | None = None
        self.search_table = False
        self.hrefs: list[str] = []
        self.canonicals: list[str] = []
        self.text: list[str] = []

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        identifier = attrs.get("id")
        if tag == "span" and identifier in {"program_detail_title", "hinban"}:
            self.field_occurrences[identifier] = self.field_occurrences.get(identifier, 0) + 1
        if len(self.stack) >= 256 or len(self.list_items) >= 1000 or len(self.hrefs) >= 100:
            raise ValueError("HTML complexity limit")
        if tag == "li":
            self.current_li = []
            self.list_items.append(self.current_li)
        if tag == "table" and attrs.get("class") == "resultList":
            self.search_table = True
        # Mirrors table.resultList/tr/td/a; permit browser-inserted tbody too.
        if (tag == "a" and len(self.stack) >= 3
            and self.stack[-1][0] == "td" and self.stack[-2][0] == "tr"
            and any(t == "table" and a.get("class") == "resultList" for t, a in self.stack[:-2])):
            self.hrefs.append(attrs.get("href", ""))
        if tag == "link" and attrs.get("rel") == "canonical":
            self.canonicals.append(attrs.get("href", ""))
        if tag not in {"br", "img", "meta", "link", "input", "hr", "source", "wbr"}:
            self.stack.append((tag, attrs))

    def handle_endtag(self, tag):
        if tag == "li":
            self.current_li = None
        for i in range(len(self.stack) - 1, -1, -1):
            if self.stack[i][0] == tag:
                del self.stack[i:]
                break

    def handle_data(self, data):
        if any(tag in {"script", "style"} for tag, _ in self.stack):
            return
        self.text.append(data)
        if self.current_li is not None:
            self.current_li.append(data)
            if ("発売日" in data and any(tag == "span" and attrs.get("class") == "koumoku" for tag, attrs in self.stack)
                and not any(item is self.current_li for item in self.release_items)):
                self.release_items.append(self.current_li)
        for identifier in {attrs.get("id") for tag, attrs in self.stack if tag == "span"} & {"program_detail_title", "hinban"}:
            self.fields.setdefault(identifier, []).append(data)


def _html(body: bytes) -> _XcityHTML:
    parser = _XcityHTML()
    try:
        parser.feed(body.decode("utf-8", errors="strict"))
        parser.close()
    except (UnicodeError, ValueError, RecursionError) as exc:
        raise WorkerError("parse", "Invalid HTML document") from exc
    return parser


def _targets_with_duplicate_evidence(targets: list[DetailTarget]) -> list[DetailTarget]:
    # Fetch each opaque provider ID once, but preserve the duplicate evidence.
    unique: dict[ProviderIdentity, DetailTarget] = {}
    for target in targets:
        prior = unique.get(target.provider_identity)
        unique[target.provider_identity] = replace(target, duplicate_count=prior.duplicate_count + 1) if prior else target
    return list(unique.values())


class XcityAdapter:
    source = "xcity"

    def supports_query(self, query: str) -> bool:
        return True

    def search_request(self, query: str) -> Request:
        query = canonical_id(query) if is_catalog_identifier(query) else unicodedata.normalize("NFKC", query)
        return Request(self.source, XCITY_ORIGIN + "/result_published/?q=" + quote(query.replace("-", ""), safe=""))

    def parse_search(self, body: bytes) -> list[DetailTarget]:
        parser = _html(body)
        if "該当する作品はみつかりませんでした" in "".join(parser.text):
            return []
        if not parser.search_table:
            raise WorkerError("schema_drift", "Expected the HTML search result table")
        targets = []
        for href in parser.hrefs:
            # Historical extraction prefixes the provider origin. Absolute URLs
            # are accepted only if they exactly match this bounded same-origin route.
            url = XCITY_ORIGIN + href if href.startswith("/") and not href.startswith("//") else href
            targets.append(DetailTarget(detail_identity(self.source, url), url))
        return _targets_with_duplicate_evidence(targets)

    def detail_request(self, target: DetailTarget) -> Request:
        if detail_identity(self.source, target.url) != target.provider_identity:
            raise WorkerError("identity_mismatch", "Detail target identity is inconsistent")
        return Request(self.source, target.url)

    def parse_detail(self, body: bytes, target: DetailTarget) -> RawMovie:
        self.detail_request(target)
        parser = _html(body)
        title = "".join(parser.fields.get("program_detail_title", []))
        identifier = "".join(parser.fields.get("hinban", [])).strip()
        if (not title or not identifier
            or any(parser.field_occurrences.get(key) != 1 for key in ("hinban", "program_detail_title"))):
            raise WorkerError("schema_drift", "Required HTML detail fields are missing")
        # Optional hardening, not a required legacy selector. Without a canonical
        # link the numeric ID is response-route-bound, not body-verified.
        for canonical_url in parser.canonicals:
            if detail_identity(self.source, canonical_url) != target.provider_identity:
                raise WorkerError("identity_mismatch", "Detail canonical identity does not match its route")
        release = ""
        for fragments in parser.release_items:
            line = "".join(fragments)
            if "発売日" in line and (match := re.search(r"\b(\d{4}/\d{2}/\d{2})\b", line)):
                release = match.group(1)
                break
        return RawMovie(target.provider_identity, CatalogIdentifier("xcity:hinban", identifier), title, release, target.url,
                        {"title": "span#program_detail_title", "id": "span#hinban", "catalogIdentifier": "span#hinban",
                         "sourceId": "request.url.query.id", "year": "li/span.koumoku:発売日",
                         "sourceId.validation": "canonical_link_and_response_route" if parser.canonicals else "response_route_only"})


def _unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate key")
        result[key] = value
    return result


def _json(body: bytes) -> dict:
    try:
        data = json.loads(body, object_pairs_hook=_unique_object,
                          parse_constant=lambda _: (_ for _ in ()).throw(ValueError("nonfinite")))
    except (ValueError, UnicodeError, RecursionError) as exc:
        raise WorkerError("parse", "Invalid JSON document") from exc
    if not isinstance(data, dict):
        raise WorkerError("schema_drift", "Expected a JSON object envelope")
    return data


class ThePornDbMoviesAdapter:
    source = "theporndb_movies"

    def supports_query(self, query: str) -> bool:
        # This subset has no provider->catalog crosswalk; do not search a catalog
        # number then mistake its appearance in a slug or title for identity proof.
        return not is_catalog_identifier(query)

    def search_request(self, query: str) -> Request:
        return Request(self.source, TPDB_ORIGIN + "/movies?q=" + quote(query, safe="") + "&per_page=100",
                       headers=TPDB_HEADERS, credential_ref=TPDB_CREDENTIAL_REF)

    def parse_search(self, body: bytes) -> list[DetailTarget]:
        data = _json(body)
        if not isinstance(data.get("data"), list):
            raise WorkerError("schema_drift", "Expected a JSON search data array")
        if len(data["data"]) > 100:
            raise WorkerError("response_too_large", "Too many source records")
        targets = []
        for row in data["data"]:
            if not isinstance(row, dict) or not valid_slug(row.get("slug")):
                raise WorkerError("parse", "Invalid JSON search provider identity")
            if not isinstance(row.get("title"), str) or not row["title"].strip():
                raise WorkerError("parse", "Invalid JSON search title")
            url = TPDB_ORIGIN + "/movies/" + row["slug"]
            targets.append(DetailTarget(ProviderIdentity(self.source, row["slug"]), url))
        return _targets_with_duplicate_evidence(targets)

    def detail_request(self, target: DetailTarget) -> Request:
        if detail_identity(self.source, target.url) != target.provider_identity:
            raise WorkerError("identity_mismatch", "Detail target identity is inconsistent")
        return Request(self.source, target.url, headers=TPDB_HEADERS, credential_ref=TPDB_CREDENTIAL_REF)

    def parse_detail(self, body: bytes, target: DetailTarget) -> RawMovie:
        self.detail_request(target)
        row = _json(body).get("data")
        if not isinstance(row, dict):
            raise WorkerError("schema_drift", "Expected a JSON detail data object")
        if not valid_slug(row.get("slug")):
            raise WorkerError("parse", "Invalid JSON detail provider identity")
        if row["slug"] != target.provider_identity.value:
            raise WorkerError("identity_mismatch", "Detail provider identity does not match the selected search result")
        if not isinstance(row.get("title"), str) or not isinstance(row.get("date", ""), str):
            raise WorkerError("parse", "Invalid JSON detail fields")
        # Legacy get_number derives a display label from series/date/title. No
        # inspected field establishes a catalog-number namespace shared with xcity.
        return RawMovie(target.provider_identity, None, row["title"], row.get("date", ""), target.url,
                        {"id": "data.slug (source-qualified display)", "sourceId": "data.slug",
                         "sourceId.validation": "exact_search_detail_slug", "catalogIdentifier": "unavailable",
                         "title": "data.title", "year": "data.date"})


ADAPTERS: dict[str, Adapter] = {adapter.source: adapter for adapter in (XcityAdapter(), ThePornDbMoviesAdapter())}
DEFAULT_SOURCES = tuple(ADAPTERS)

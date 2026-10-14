"""Standalone, bounded JavBus catalog-metadata subset of the legacy crawler.

Lineage: mdcx/crawlers/javbus.py direct detail GET, censored search fallback,
movie-box links, get_title/getWebNumber/getRelease. No legacy imports, number
rewrites, artwork, credentials, filename matching, or cross-provider identity.
The transport owns network enablement and access policy; importing this module
or constructing this provider never requests anything.
"""
from __future__ import annotations

import hashlib
import re
import unicodedata
from collections.abc import Callable
from dataclasses import dataclass, field, replace
from html.parser import HTMLParser

from .models import (
    MAX_BODY_BYTES,
    Candidate,
    CatalogIdentifier,
    DetailTarget,
    RawMovie,
    Request,
    WorkerError,
    canonical_id,
    normalize,
)
from .scheduler import Scheduler
from .source_settings import (
    JAVBUS_CATALOG_PATTERN,
    JAVBUS_ORIGIN,
    javbus_detail_identity,
    validate_javbus_origin,
    validate_javbus_request,
)

_IDENTIFIER_LABELS = {"識別碼", "识别码", "品番", "id"}
_RELEASE_LABELS = {"發行日期", "发行日期", "発売日", "release date"}
_VOID = {"area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr"}
_IGNORED = {"script", "style", "template"}
_METADATA_ERRORS = {"parse", "schema_drift", "validation", "identity_mismatch", "ambiguous_identity"}


@dataclass
class _Node:
    tag: str
    attrs: dict[str, str]
    parent: _Node | None = field(default=None, repr=False)
    content: list[str | _Node] = field(default_factory=list)
    closed: bool = False

    def text(self) -> str:
        if self.tag in _IGNORED:
            return ""
        return "".join(part if isinstance(part, str) else part.text() for part in self.content)

    def classes(self) -> set[str]:
        return set(self.attrs.get("class", "").split())


class _Document(HTMLParser):
    """Small nonexecuting tree for label siblings, nested titles, and link routes."""
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.root = _Node("document", {})
        self.stack = [self.root]
        self.nodes: list[_Node] = []
        self.broken = False

    def handle_starttag(self, tag, attrs):
        if len(self.stack) >= 128 or len(self.nodes) >= 10000:
            raise ValueError("HTML complexity limit")
        # Duplicate attributes on an evidence-bearing element are ambiguous.
        names = [key for key, _ in attrs]
        if len(names) != len(set(names)):
            raise ValueError("Duplicate HTML attribute")
        node = _Node(tag, {key: value or "" for key, value in attrs}, self.stack[-1])
        self.stack[-1].content.append(node)
        self.nodes.append(node)
        if tag in _VOID:
            node.closed = True
        else:
            self.stack.append(node)

    def handle_startendtag(self, tag, attrs):
        self.handle_starttag(tag, attrs)
        if tag not in _VOID:
            self.handle_endtag(tag)

    def handle_endtag(self, tag):
        for index in range(len(self.stack) - 1, 0, -1):
            if self.stack[index].tag == tag:
                # Omitted optional HTML closing tags are normal; an unclosed
                # identity/title/link element is not reliable field evidence.
                if any(node.tag in {"span", "h3", "title", "a"} for node in self.stack[index + 1:]):
                    self.broken = True
                self.stack[index].closed = True
                del self.stack[index:]
                return
        if tag not in _VOID:
            self.broken = True

    def handle_data(self, data):
        self.stack[-1].content.append(data)


def _document(body: bytes) -> _Document:
    if not isinstance(body, bytes):
        raise WorkerError("parse", "Invalid HTML document")
    if len(body) > MAX_BODY_BYTES:
        raise WorkerError("response_too_large", "Source body exceeds limit")
    sample = body.lower()
    if any(marker in sample for marker in (
        b"cf-chl-", b"g-recaptcha", b"hcaptcha", b"captcha challenge", b"challenge-platform",
        b"<title>just a moment", b"verify you are human",
    )):
        raise WorkerError("challenge", "Source requires human verification")
    if b"lostpasswd" in sample:
        raise WorkerError("auth_required", "Source requires authorized access")
    parser = _Document()
    try:
        parser.feed(body.decode("utf-8-sig", errors="strict"))
        parser.close()
        if parser.broken or any(node.tag in {"span", "h3", "title", "a"} for node in parser.stack[1:]):
            raise ValueError("Unclosed evidence element")
    except (UnicodeError, ValueError, RecursionError) as exc:
        raise WorkerError("parse", "Invalid HTML document") from exc
    # Template/script/style descendants cannot provide visible metadata.
    def visible(node: _Node) -> bool:
        while node is not None:
            if node.tag in _IGNORED:
                return False
            node = node.parent
        return True
    parser.nodes = [node for node in parser.nodes if visible(node)]
    title = next((node.text().strip().casefold() for node in parser.nodes if node.tag == "title"), "")
    if title in {"access denied", "service unavailable", "502 bad gateway", "503 service unavailable", "403 forbidden"}:
        raise WorkerError("http_error", "Source returned an error document")
    return parser


def _label(node: _Node) -> str:
    return node.text().strip().casefold().rstrip(":：").strip()


def _after_label(node: _Node) -> str:
    """Legacy next-element sibling, otherwise the label's parent tail text."""
    if not node.closed or node.parent is None:
        raise WorkerError("parse", "Invalid HTML label field")
    index = next(index for index, child in enumerate(node.parent.content) if child is node)
    tail = node.parent.content[index + 1:]
    sibling = next((part for part in tail if isinstance(part, _Node)), None)
    if sibling is not None and sibling.text().strip():
        if not sibling.closed:
            raise WorkerError("parse", "Invalid HTML label value")
        return sibling.text().strip()
    return "".join(part if isinstance(part, str) else part.text() for part in tail).strip(" \t\r\n:：")


def _catalog(query: str) -> str:
    if (not isinstance(query, str) or len(query) > 128
        or any(unicodedata.category(char) in {"Cc", "Cs"} for char in query)):
        raise WorkerError("unsupported_query", "This source requires a supported catalog identifier")
    catalog = canonical_id(query)
    if not re.fullmatch(JAVBUS_CATALOG_PATTERN, catalog):
        raise WorkerError("unsupported_query", "This source requires a supported catalog identifier")
    return catalog


class JavBusProvider:
    """Direct catalog detail, then at most one unambiguous search-selected detail."""
    source = "javbus"

    def __init__(self, scheduler: Scheduler, origin: str = JAVBUS_ORIGIN):
        self.scheduler = scheduler
        self.origin = validate_javbus_origin(origin)

    def direct_request(self, query: str) -> Request:
        request = Request(self.source, self.origin + "/" + _catalog(query))
        validate_javbus_request(request, origin=self.origin)
        return request

    def search_request(self, query: str) -> Request:
        request = Request(self.source, self.origin + "/search/" + _catalog(query) + "&type=&parent=ce")
        validate_javbus_request(request, origin=self.origin)
        return request

    def _target(self, url: str) -> DetailTarget:
        identity = javbus_detail_identity(url, origin=self.origin)
        return DetailTarget(identity, url)

    def detail_request(self, target: DetailTarget) -> Request:
        if javbus_detail_identity(target.url, origin=self.origin) != target.provider_identity:
            raise WorkerError("identity_mismatch", "Detail route identity is inconsistent")
        request = Request(self.source, target.url)
        validate_javbus_request(request, origin=self.origin)
        return request

    def parse_search(self, body: bytes, query: str) -> list[DetailTarget]:
        catalog, document = _catalog(query), _document(body)
        links = [node for node in document.nodes if node.tag == "a" and "movie-box" in node.classes()]
        has_container = any(node.attrs.get("id") == "waterfall" for node in document.nodes)
        if not links and not has_container:
            raise WorkerError("schema_drift", "Expected the HTML catalog search results")
        if len(links) > 100:
            raise WorkerError("response_too_large", "Too many source search records")
        matches: dict[str, DetailTarget] = {}
        for node in links:
            href = node.attrs.get("href", "")
            # Relative root routes and exact absolute origins only. In particular
            # urljoin must never normalize away a prohibited dot segment.
            if not href or href.startswith("//") or "\\" in href or "/./" in href or "/../" in href:
                raise WorkerError("policy_blocked", "Search result route is not allowed")
            url = self.origin + href if href.startswith("/") else href
            target = self._target(url)
            if canonical_id(target.provider_identity.value) != catalog:
                continue
            prior = matches.get(url)
            matches[url] = replace(target, duplicate_count=prior.duplicate_count + 1) if prior else target
        if len(matches) > 1:
            raise WorkerError("ambiguous_identity", "Search returned multiple routes for the requested catalog identifier")
        return list(matches.values())

    def parse_detail(self, body: bytes, target: DetailTarget, query: str) -> RawMovie:
        self.detail_request(target)
        catalog = _catalog(query)
        if canonical_id(target.provider_identity.value) != catalog:
            raise WorkerError("identity_mismatch", "Detail route does not match the requested catalog identifier")
        document = _document(body)
        labels = [node for node in document.nodes if node.tag == "span" and _label(node) in _IDENTIFIER_LABELS]
        if not labels:
            raise WorkerError("schema_drift", "Required HTML catalog identifier is missing")
        if len(labels) != 1:
            raise WorkerError("ambiguous_identity", "HTML catalog identifier is ambiguous")
        identifier = _after_label(labels[0])
        parent = labels[0].parent
        position = next(index for index, child in enumerate(parent.content) if child is labels[0])
        tail_text = "".join(part if isinstance(part, str) else part.text() for part in parent.content[position + 1:])
        if " ".join(tail_text.strip(" \t\r\n:：").split()) != " ".join(identifier.split()):
            raise WorkerError("ambiguous_identity", "HTML catalog identifier has multiple possible values")
        if not identifier:
            raise WorkerError("schema_drift", "Required HTML catalog identifier is missing")
        if not re.fullmatch(JAVBUS_CATALOG_PATTERN, canonical_id(identifier)):
            raise WorkerError("validation", "Invalid HTML catalog identifier")
        if canonical_id(identifier) != catalog:
            raise WorkerError("identity_mismatch", "Detail catalog identifier does not match the request")
        title_node = next((node for node in document.nodes if node.tag == "h3"), None)
        if title_node is None or not title_node.text().strip():
            title_node = next((node for node in document.nodes if node.tag == "title"), None)
        if title_node is None:
            raise WorkerError("schema_drift", "Required HTML title is missing")
        original_title = title_node.text().strip()
        title = re.sub(r"\s*-\s*JavBus\s*$", "", original_title, flags=re.IGNORECASE).strip()
        for number in dict.fromkeys((identifier, catalog, target.provider_identity.value)):
            title = re.sub(rf"^\s*{re.escape(number)}(?:\s+|$)", "", title, count=1, flags=re.IGNORECASE).strip()
        if not title:
            raise WorkerError("schema_drift", "Required HTML title is missing")
        canonicals = [node.attrs.get("href", "") for node in document.nodes
                      if node.tag == "link" and "canonical" in node.attrs.get("rel", "").split()]
        for url in canonicals:
            # Optional route evidence is checked, never used as a catalog field.
            if url.startswith("/") and not url.startswith("//"):
                url = self.origin + url
            if javbus_detail_identity(url, origin=self.origin) != target.provider_identity:
                raise WorkerError("identity_mismatch", "Detail canonical route does not match the response route")
        release_labels = [node for node in document.nodes
                          if node.tag == "span" and "header" in node.classes() and _label(node) in _RELEASE_LABELS]
        release, release_raw = "", ""
        if len(release_labels) > 1:
            raise WorkerError("schema_drift", "HTML release date is ambiguous")
        if release_labels:
            release_raw = _after_label(release_labels[0])
            found = re.search(r"(?<!\d)(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})(?!\d)", release_raw)
            release = (f"{found[1]}-{int(found[2]):02d}-{int(found[3]):02d}" if found else release_raw)
        return RawMovie(
            target.provider_identity, CatalogIdentifier("javbus:catalog", identifier), title, release, target.url,
            {"id": "span:catalog-label/sibling-or-parent-text", "catalogIdentifier": "span:catalog-label/sibling-or-parent-text",
             "sourceId": "request.url.path.catalog-route", "sourceId.validation": "response_route_and_canonical_link" if canonicals else "response_route_only",
             "title": title_node.tag + "[1]", "title.documentValue": original_title,
             "title.providerTransforms": "remove_provider_suffix,remove_exact_leading_catalog",
             "year": "span.header:release-label/sibling-or-parent-text", "year.documentValue": release_raw,
             "year.providerTransforms": "date_separator_and_padding", "catalogIdentifier.identityGate": "exact_detail_catalog_to_query"},
        )

    async def match(self, query: str, sources: tuple[str, ...] = ("javbus",),
                    on_error: Callable[[str, WorkerError], None] = lambda *_: None) -> list[Candidate]:
        counters = self.scheduler.counters(self.source)
        try:
            if not isinstance(sources, (tuple, list)) or tuple(sources) != (self.source,):
                raise WorkerError("invalid_sources", "This matcher supports only the configured catalog source")
            direct = self.direct_request(query)
            target = self._target(direct.url)
            search_evidence: dict[str, str] = {}
            try:
                response = await self.scheduler.fetch(direct)
            except WorkerError as error:
                if error.code != "not_found":
                    raise
                search = self.search_request(query)
                try:
                    search_response = await self.scheduler.fetch(search)
                except WorkerError as search_error:
                    # A missing search endpoint does not establish no metadata.
                    if search_error.code == "not_found":
                        raise WorkerError("http_error", "Source search endpoint was not found") from search_error
                    raise
                targets = self.parse_search(search_response.body, query)
                if not targets:
                    counters.noMatches += 1
                    counters.lastError = None
                    return []
                target = targets[0]
                search_evidence = {
                    "search.url": search.url, "search.bodySha256": hashlib.sha256(search_response.body).hexdigest(),
                    "search.fixture": str(search_response.fixture).lower(), "search.duplicateCount": str(target.duplicate_count),
                    "search.uniqueTargets": str(len(targets)), "search.detailLimit": "1", "lookup.directStatus": "404",
                }
                response = await self.scheduler.fetch(self.detail_request(target))
            raw = self.parse_detail(response.body, target, query)
            candidate = normalize(raw, source=self.source, query=query, body=response.body, fixture=response.fixture)
            if candidate is None:
                raise WorkerError("identity_mismatch", "Detail catalog identifier does not match the request")
            reasons = [reason for reason in candidate.provenance.get("reviewReasons", "").split(",") if reason]
            reasons.append("catalog_metadata_requires_review")
            if target.duplicate_count > 1:
                reasons.append("duplicate_search_results")
            candidate = replace(candidate, provenance={
                **candidate.provenance, **search_evidence, "requiresReview": "true", "reviewReasons": ",".join(reasons),
                "adapterVersion": "javbus-catalog-1", "sourcePriority": "1",
                "lookup.method": "search_after_direct_404" if search_evidence else "direct_catalog_detail",
                "lookup.directUrl": direct.url, "providerIdentity.source": self.source,
                "providerIdentity.value": target.provider_identity.value,
                "catalogIdentifier.crossProviderIdentity": "unavailable_no_crosswalk",
            })
            counters.matches += 1
            counters.lastError = None
            return [candidate]
        except WorkerError as error:
            counters.lastError = error.code
            if error.code == "not_found":
                counters.noMatches += 1
            else:
                if error.code in _METADATA_ERRORS:
                    counters.parserFailures += 1
                on_error(self.source, error)
            return []

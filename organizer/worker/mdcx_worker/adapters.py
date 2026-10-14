"""Pure parsers migrated from selected legacy extraction rules, not its CLI.

xcity: mdcx/crawlers/xcity.py getTitle/getWebNumber/getRelease.
theporndb_movies: mdcx/crawlers/theporndb_movies.py read_data.
See repository LICENSE and README; upstream notices are unchanged.
"""
from __future__ import annotations

import json
import re
from html.parser import HTMLParser
from typing import Protocol
from urllib.parse import quote

from .models import RawMovie, Request, WorkerError


class Adapter(Protocol):
    source: str
    def request(self, query: str) -> Request: ...
    def parse(self, body: bytes) -> list[RawMovie]: ...


class _XcityHTML(HTMLParser):
    """Capture descendant text without fragile regexes or script execution."""
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.stack: list[tuple[str, str | None]] = []
        self.fields: dict[str, list[str]] = {}
        self.list_items: list[list[str]] = []
        self.current_li: list[str] | None = None

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if len(self.stack) >= 256 or len(self.list_items) >= 1000:
            raise ValueError("HTML complexity limit")
        if tag == "li":
            self.current_li = []
            self.list_items.append(self.current_li)
        if tag not in {"br", "img", "meta", "link", "input", "hr", "source", "wbr"}:
            self.stack.append((tag, attrs.get("id")))

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
        if self.current_li is not None:
            self.current_li.append(data)
        for identifier in {identifier for _, identifier in self.stack} & {"program_detail_title", "hinban"}:
            self.fields.setdefault(identifier, []).append(data)


class XcityAdapter:
    source = "xcity"

    def request(self, query: str) -> Request:
        return Request(self.source, "https://xcity.example.invalid/search?q=" + quote(query, safe=""))

    def parse(self, body: bytes) -> list[RawMovie]:
        parser = _XcityHTML()
        try:
            parser.feed(body.decode("utf-8", errors="strict"))
        except (UnicodeError, ValueError) as exc:
            raise WorkerError("parse", "Invalid HTML document") from exc
        # A changed detail-page schema is distinct from an empty search result.
        title = "".join(parser.fields.get("program_detail_title", []))
        identifier = "".join(parser.fields.get("hinban", [])).strip()
        if not title or not identifier:
            raise WorkerError("schema_drift", "Required HTML metadata fields are missing")
        release = ""
        for fragments in parser.list_items:
            line = "".join(fragments)
            if "発売日" in line and (match := re.search(r"\b(\d{4}/\d{2}/\d{2})\b", line)):
                release = match.group(1)
                break
        return [RawMovie(identifier, title, release,
            f"https://xcity.example.invalid/detail/{quote(identifier, safe='')}",
            {"title": "span#program_detail_title", "id": "span#hinban", "year": "li:発売日"})]


class ThePornDbMoviesAdapter:
    source = "theporndb_movies"

    def request(self, query: str) -> Request:
        return Request(self.source, "https://theporndb.example.invalid/movies?q=" + quote(query, safe=""))

    def parse(self, body: bytes) -> list[RawMovie]:
        try:
            data = json.loads(body)
        except (ValueError, UnicodeError) as exc:
            raise WorkerError("parse", "Invalid JSON document") from exc
        if not isinstance(data, dict) or not isinstance(data.get("data"), list):
            raise WorkerError("schema_drift", "Expected a JSON data array")
        if len(data["data"]) > 100:
            raise WorkerError("response_too_large", "Too many source records")
        results = []
        for row in data["data"]:
            if not isinstance(row, dict) or not isinstance(row.get("slug"), str) or not row["slug"]:
                raise WorkerError("parse", "Missing JSON movie identifier")
            if not isinstance(row.get("title"), str) or not isinstance(row.get("date", ""), str):
                raise WorkerError("parse", "Invalid JSON movie fields")
            results.append(RawMovie(row["slug"], row["title"], row.get("date", ""),
                "https://theporndb.example.invalid/movies/" + quote(row["slug"], safe=""),
                {"id": "data[].slug", "title": "data[].title", "year": "data[].date"}))
        return results


ADAPTERS: dict[str, Adapter] = {adapter.source: adapter for adapter in (XcityAdapter(), ThePornDbMoviesAdapter())}
DEFAULT_SOURCES = tuple(ADAPTERS)

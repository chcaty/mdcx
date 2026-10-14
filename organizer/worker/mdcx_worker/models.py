"""Typed transport, failure, and normalized metadata boundary (stdlib only)."""
from __future__ import annotations

import hashlib
import re
import unicodedata
from dataclasses import asdict, dataclass, field
from datetime import date
from difflib import SequenceMatcher
from typing import Protocol
from urllib.parse import urlsplit

MAX_BODY_BYTES = 1_048_576


class WorkerError(Exception):
    """Safe messages only: do not expose raw response bodies, queries, or tokens."""

    def __init__(self, code: str, message: str, *, retryable: bool = False):
        super().__init__(message)
        self.code = code
        self.message = message
        self.retryable = retryable

    def payload(self) -> dict:
        return {"code": self.code, "message": self.message, "retryable": self.retryable}


@dataclass(frozen=True)
class Request:
    source: str
    url: str


@dataclass(frozen=True)
class Response:
    status: int
    body: bytes = b""
    headers: dict[str, str] = field(default_factory=dict)
    fixture: bool = False


class Transport(Protocol):
    async def fetch(self, request: Request) -> Response: ...


def canonical(value: str) -> str:
    return "".join(c for c in unicodedata.normalize("NFKC", value).casefold() if c.isalnum())


def canonical_id(value: str) -> str:
    """Narrow catalog-ID equivalence: case/separator only; keep zeros/editions."""
    value = unicodedata.normalize("NFKC", value).strip()
    match = re.fullmatch(r"([A-Za-z]{2,12})[-_ ]?(\d+)([A-Za-z]?)", value)
    if match:
        return f"{match[1].upper()}-{match[2]}{match[3].upper()}"
    return value.casefold()


def is_catalog_identifier(value: str) -> bool:
    return re.fullmatch(r"[A-Z]{2,12}-[0-9]+[A-Z]?", canonical_id(value)) is not None


def clean_text(value: object, field_name: str, *, required: bool = True) -> str:
    if not isinstance(value, str) or len(value) > 1000:
        raise WorkerError("validation", f"Invalid {field_name} field")
    value = " ".join(unicodedata.normalize("NFKC", value).split())
    if (required and not value) or len(value) > 1000 or any(unicodedata.category(c) == "Cc" for c in value):
        raise WorkerError("validation", f"Invalid {field_name} field")
    return value


@dataclass(frozen=True)
class RawMovie:
    identifier: str
    title: str
    release: str
    url: str
    fields: dict[str, str]


@dataclass(frozen=True)
class Candidate:
    id: str
    title: str
    year: int | None
    source: str
    sourceUrl: str
    confidence: float
    provenance: dict

    def payload(self) -> dict:
        return {**{k: v for k, v in asdict(self).items() if v is not None},
                "requiresReview": self.provenance.get("requiresReview") == "true",
                "reviewReasons": [reason for reason in self.provenance.get("reviewReasons", "").split(",") if reason]}


def normalize(raw: RawMovie, *, source: str, query: str, body: bytes, fixture: bool) -> Candidate | None:
    title = clean_text(raw.title, "title")
    identifier = clean_text(raw.identifier, "identifier")
    if len(identifier) > 128 or not isinstance(raw.release, str) or len(raw.release) > 64:
        raise WorkerError("validation", "Metadata field exceeds length limit")
    url = urlsplit(raw.url)
    if url.scheme != "https" or not url.hostname or url.username or url.password or url.query or url.fragment:
        raise WorkerError("validation", "Unsafe metadata URL")
    year = None
    warnings = []
    if raw.release:
        try:
            parsed = date.fromisoformat(raw.release.replace("/", "-"))
            if not 1888 <= parsed.year <= 2100:
                raise ValueError("year out of range")
            year = parsed.year
        except (ValueError, TypeError):
            warnings.append("invalid_release_date")
    q, title_key, identifier_key = canonical(query), canonical(title), canonical_id(identifier)
    query_id = canonical_id(query)
    if is_catalog_identifier(query) and query_id != identifier_key:
        return None  # Identifier searches may never degrade to a title-only hit.
    if query_id == identifier_key:
        confidence, method = 0.99, "exact_identifier"
    elif q == title_key:
        confidence, method = 0.95, "exact_title"
    else:
        # Conservative fuzzy title suggestions only; no automatic identity claim.
        ratio = SequenceMatcher(None, q, title_key, autojunk=False).ratio()
        if len(q) < 5 or ratio < 0.65:
            return None
        confidence, method = round(min(0.84, ratio * 0.9), 3), "title_similarity"
    if warnings:
        confidence = round(max(0.0, confidence - 0.05), 3)
    return Candidate(
        id=canonical_id(identifier), title=title, year=year, source=source, sourceUrl=raw.url,
        confidence=confidence,
        provenance={
            "adapterVersion": "1", "fixture": str(fixture).lower(), "bodySha256": hashlib.sha256(body).hexdigest(),
            "matchMethod": method, "confidenceScale": "heuristic_v1_not_probability",
            "requiresReview": str(method != "exact_identifier" or bool(warnings)).lower(),
            "reviewReasons": ",".join(warnings + ([] if method == "exact_identifier" else ["title_only_match"])),
            "validation": "valid", "warnings": ",".join(warnings),
            **{f"field.{key}": value for key, value in raw.fields.items()},
            **{f"field.{key}.source": source for key in ("id", "title", "year")},
            **{f"field.{key}.url": raw.url for key in ("id", "title", "year")},
            "field.id.rawValue": raw.identifier, "field.title.rawValue": raw.title,
            "field.year.rawValue": raw.release,
            "field.title.transforms": "unicode_nfkc,whitespace_collapse",
            "field.year.transforms": "validated_iso_date_to_year",
            "field.title.validation": "valid_nonempty",
            "field.year.validation": "valid" if year else ("invalid" if raw.release else "missing"),
        },
    )

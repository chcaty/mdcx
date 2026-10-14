"""Typed transport and identity boundary; provider IDs are not catalog numbers."""
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
    """Safe constants only: never expose response bodies, queries, or tokens."""

    def __init__(self, code: str, message: str, *, retryable: bool = False):
        super().__init__(message)
        self.code, self.message, self.retryable = code, message, retryable

    def payload(self) -> dict:
        return {"code": self.code, "message": self.message, "retryable": self.retryable}


@dataclass(frozen=True)
class Request:
    source: str
    url: str
    method: str = "GET"
    headers: tuple[tuple[str, str], ...] = field(default=(), repr=False)
    credential_ref: str | None = None


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
    """Catalog equivalence only: case/separator normalization keeps zeros/editions."""
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
    if (required and not value) or any(unicodedata.category(c) in {"Cc", "Cs"} for c in value):
        raise WorkerError("validation", f"Invalid {field_name} field")
    return value


@dataclass(frozen=True)
class ProviderIdentity:
    """Opaque identifier scoped to exactly one provider; never canonicalized."""
    source: str
    value: str


@dataclass(frozen=True)
class CatalogIdentifier:
    namespace: str
    value: str


def comparable_catalog(left: CatalogIdentifier | None, right: CatalogIdentifier | None) -> bool:
    return bool(left and right and left.namespace == right.namespace
                and canonical_id(left.value) == canonical_id(right.value))


@dataclass(frozen=True)
class DetailTarget:
    provider_identity: ProviderIdentity
    url: str
    duplicate_count: int = 1


@dataclass(frozen=True)
class RawMovie:
    provider_identity: ProviderIdentity
    catalog_identifier: CatalogIdentifier | None
    title: str
    release: str
    url: str
    fields: dict[str, str]


@dataclass(frozen=True)
class Candidate:
    id: str  # Backward-compatible display/review key, not an inter-provider join key.
    title: str
    year: int | None
    source: str
    sourceUrl: str
    confidence: float
    provenance: dict
    sourceId: str | None = None
    catalogIdentifier: CatalogIdentifier | None = None

    def payload(self) -> dict:
        # A missing catalog identity is explicit null; optional year stays omitted.
        result = asdict(self)
        if self.year is None:
            del result["year"]
        return {**result,
                "requiresReview": self.provenance.get("requiresReview") == "true",
                "reviewReasons": [reason for reason in self.provenance.get("reviewReasons", "").split(",") if reason]}


def normalize(raw: RawMovie, *, source: str, query: str, body: bytes, fixture: bool) -> Candidate | None:
    title = clean_text(raw.title, "title")
    identity = raw.provider_identity
    if (identity.source != source or not isinstance(identity.value, str) or not identity.value
        or len(identity.value) > 128 or any(unicodedata.category(c) in {"Cc", "Cs"} for c in identity.value)):
        raise WorkerError("validation", "Invalid provider identity")
    catalog = raw.catalog_identifier
    if ((source == "xcity" and (catalog is None or catalog.namespace != "xcity:hinban"))
        or (source == "theporndb_movies" and catalog is not None)):
        raise WorkerError("validation", "Provider catalog evidence violates the identity contract")
    if catalog:
        value = clean_text(catalog.value, "catalog identifier")
        if len(value) > 128 or not catalog.namespace:
            raise WorkerError("validation", "Invalid catalog identity")
        catalog = CatalogIdentifier(catalog.namespace, canonical_id(value))
    if not isinstance(raw.release, str) or len(raw.release) > 64:
        raise WorkerError("validation", "Metadata field exceeds length limit")
    try:
        url = urlsplit(raw.url)
        if (url.scheme != "https" or not url.hostname or url.username or url.password
            or url.fragment or url.port not in (None, 443)):
            raise ValueError("unsafe origin")
        if source in {"xcity", "theporndb_movies"}:
            from .contracts import detail_identity
            if detail_identity(source, raw.url) != identity:
                raise ValueError("wrong provider route")
        elif url.query:
            raise ValueError("unsafe query")
    except ValueError as exc:
        raise WorkerError("validation", "Unsafe metadata URL") from exc
    year, warnings = None, []
    if raw.release:
        try:
            parsed = date.fromisoformat(raw.release.replace("/", "-"))
            if not 1888 <= parsed.year <= 2100:
                raise ValueError("year out of range")
            year = parsed.year
        except (ValueError, TypeError):
            warnings.append("invalid_release_date")
    q, title_key = canonical(query), canonical(title)
    if is_catalog_identifier(query):
        if not catalog or canonical_id(query) != catalog.value:
            return None  # A slug or matching title cannot satisfy a catalog-ID query.
        confidence, method = 0.99, "exact_identifier"
    elif catalog and canonical_id(query) == catalog.value:
        confidence, method = 0.99, "exact_identifier"
    elif q == title_key:
        confidence, method = 0.95, "exact_title"
    else:
        ratio = SequenceMatcher(None, q, title_key, autojunk=False).ratio()
        if len(q) < 5 or ratio < 0.65:
            return None
        confidence, method = round(min(0.84, ratio * 0.9), 3), "title_similarity"
    if warnings:
        confidence = round(max(0.0, confidence - 0.05), 3)
    reasons = warnings + ([] if method == "exact_identifier" else ["title_only_match"])
    if catalog is None:
        reasons.append("missing_catalog_identifier")
    display_id = catalog.value if catalog else f"{source}:{identity.value}"
    return Candidate(
        id=display_id, sourceId=identity.value, catalogIdentifier=catalog,
        title=title, year=year, source=source, sourceUrl=raw.url, confidence=confidence,
        provenance={
            "adapterVersion": "2", "identitySchema": "2", "fixture": str(fixture).lower(),
            "responseOrigin": "synthetic_uncaptured" if fixture else "injected_response_unverified",
            "bodySha256": hashlib.sha256(body).hexdigest(),
            "matchMethod": method, "confidenceScale": "heuristic_v1_not_probability",
            "requiresReview": str(bool(reasons)).lower(), "reviewReasons": ",".join(reasons),
            "validation": "valid", "warnings": ",".join(warnings),
            **{f"field.{key}": value for key, value in raw.fields.items()},
            **{f"field.{key}.source": source for key in ("id", "sourceId", "catalogIdentifier", "title", "year")},
            **{f"field.{key}.url": raw.url for key in ("id", "sourceId", "catalogIdentifier", "title", "year")},
            "field.id.rawValue": raw.catalog_identifier.value if catalog else identity.value,
            "field.id.transforms": "catalog_canonical_display" if catalog else "source_qualified_provider_display",
            "field.sourceId.rawValue": identity.value, "field.sourceId.transforms": "none_opaque_provider_identity",
            "field.catalogIdentifier.namespace": catalog.namespace if catalog else "",
            "field.catalogIdentifier.validation": "source_field_evidence" if catalog else "unavailable_no_crosswalk",
            "field.title.rawValue": raw.title, "field.year.rawValue": raw.release,
            "field.title.transforms": "unicode_nfkc,whitespace_collapse",
            "field.year.transforms": "validated_iso_date_to_year", "field.title.validation": "valid_nonempty",
            "field.year.validation": "valid" if year else ("invalid" if raw.release else "missing"),
        },
    )

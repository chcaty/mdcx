"""Lazy provider priority with separate search/detail requests and typed identities."""
from __future__ import annotations

import asyncio
import hashlib
from collections.abc import Callable
from dataclasses import replace

from .adapters import ADAPTERS, DEFAULT_SOURCES, Adapter
from .models import (
    Candidate,
    WorkerError,
    canonical,
    canonical_id,
    comparable_catalog,
    is_catalog_identifier,
    normalize,
)
from .scheduler import Scheduler

MAX_DETAIL_REQUESTS = 10
METADATA_ERRORS = {"parse", "schema_drift", "validation", "identity_mismatch"}


def _review(candidate: Candidate, *reasons: str) -> Candidate:
    existing = candidate.provenance.get("reviewReasons", "").split(",")
    merged = list(dict.fromkeys(reason for reason in [*existing, *reasons] if reason))
    return replace(candidate, provenance={**candidate.provenance, "requiresReview": "true", "reviewReasons": ",".join(merged)})


class Matcher:
    def __init__(self, scheduler: Scheduler, adapters: dict[str, Adapter] | None = None):
        self.scheduler = scheduler
        self.adapters = adapters if adapters is not None else ADAPTERS

    async def match(self, query: str, sources: tuple[str, ...] = DEFAULT_SOURCES,
                    on_error: Callable[[str, WorkerError], None] = lambda *_: None) -> list[Candidate]:
        selected: list[Candidate] = []
        for priority, source in enumerate(sources, 1):
            adapter = self.adapters[source]
            counters = self.scheduler.counters(source)
            if not adapter.supports_query(query):
                counters.noMatches += 1
                if selected:
                    selected[0].provenance["rejectedFallback"] = f"{source}:no_comparable_catalog_identity"
                continue
            try:
                search_request = adapter.search_request(query)
                search_response = await self.scheduler.fetch(search_request)
                candidates = []
                try:
                    targets = adapter.parse_search(search_response.body)
                except WorkerError as error:
                    if error.code in METADATA_ERRORS:
                        counters.parserFailures += 1
                    raise
                truncated = len(targets) > MAX_DETAIL_REQUESTS
                incomplete = False
                stopped_reason = ""
                processed = 0
                for target in targets[:MAX_DETAIL_REQUESTS]:
                    catalog_nonmatch = False
                    processed += 1
                    try:
                        detail_request = adapter.detail_request(target)
                        response = await self.scheduler.fetch(detail_request)
                        raw = adapter.parse_detail(response.body, target)
                        if (raw.catalog_identifier and is_catalog_identifier(query)
                            and canonical_id(raw.catalog_identifier.value) != canonical_id(query)):
                            catalog_nonmatch = True
                            raise WorkerError("identity_mismatch", "Detail catalog identifier does not match the request")
                        candidate = normalize(raw, source=source, query=query, body=response.body, fixture=response.fixture)
                        if candidate is not None:
                            provenance = {**candidate.provenance, "sourcePriority": str(priority),
                                          "search.url": search_request.url,
                                          "search.bodySha256": hashlib.sha256(search_response.body).hexdigest(),
                                          "search.fixture": str(search_response.fixture).lower(),
                                          "search.duplicateCount": str(target.duplicate_count),
                                          "search.uniqueTargets": str(len(targets)),
                                          "search.detailLimit": str(MAX_DETAIL_REQUESTS),
                                          "providerIdentity.source": raw.provider_identity.source,
                                          "providerIdentity.value": raw.provider_identity.value}
                            candidate = replace(candidate, provenance=provenance)
                            if target.duplicate_count > 1:
                                candidate = _review(candidate, "duplicate_search_results")
                            candidates.append(candidate)
                    except WorkerError as error:
                        # One broken or unrelated detail must not erase another
                        # valid result or hide an exact match later in the list.
                        counters.lastError = error.code
                        if error.code in METADATA_ERRORS:
                            counters.parserFailures += 1
                        if error.code == "not_found":
                            counters.noMatches += 1
                        if not catalog_nonmatch:
                            incomplete = True
                        on_error(source, error)
                        if error.code in {"auth_required", "challenge", "policy_blocked", "rate_limited", "replay_mismatch"}:
                            stopped_reason = error.code
                            break  # No repeated access attempts after a systemic denial.
                if truncated and processed == MAX_DETAIL_REQUESTS:
                    counters.lastError = "search_truncated"
                    on_error(source, WorkerError("search_truncated", "Offline detail budget reached; search results are incomplete"))
                    candidates = [_review(candidate, "search_results_truncated") for candidate in candidates]
                if incomplete:
                    candidates = [_review(candidate, "incomplete_detail_results") for candidate in candidates]
                if stopped_reason:
                    candidates = [replace(candidate, provenance={**candidate.provenance,
                                  "search.stoppedReason": stopped_reason}) for candidate in candidates]
                candidates.sort(key=lambda candidate: (-candidate.confidence, candidate.id, candidate.title))
                if not candidates:
                    counters.noMatches += 1
                    if selected and targets:
                        selected[0].provenance["rejectedFallback"] = f"{source}:identifier_or_title_mismatch"
                    continue
                counters.matches += len(candidates)
                if not selected:
                    selected = candidates
                    if len(selected) > 1:
                        return [_review(candidate, "ambiguous_candidates") for candidate in selected]
                    if selected[0].year is not None or selected[0].provenance["matchMethod"] != "exact_identifier":
                        return selected
                    continue
                primary = selected[0]
                same_identity = [candidate for candidate in candidates
                                 if comparable_catalog(candidate.catalogIdentifier, primary.catalogIdentifier)]
                if len(same_identity) != 1:
                    selected = [_review(primary, "fallback_identity_conflict")]
                    selected[0].provenance["rejectedFallback"] = f"{source}:catalog_namespace_or_value_conflict"
                    continue
                fallback = same_identity[0]
                provenance = dict(primary.provenance)
                if canonical(primary.title) != canonical(fallback.title):
                    primary = _review(primary, "conflicting_title")
                    provenance = {**primary.provenance, "rejectedTitle": f"{source}:lower_priority_conflict"}
                if primary.year is None and fallback.year is not None:
                    provenance.update({key: value for key, value in fallback.provenance.items() if key.startswith("field.year")})
                    provenance.update({"field.year.fallbackPriority": str(priority),
                                       "field.year.bodySha256": fallback.provenance["bodySha256"],
                                       "field.year.identityGate": "explicit_catalog_namespace_and_value"})
                    return [replace(primary, year=fallback.year, provenance=provenance)]
                selected = [replace(primary, provenance=provenance)]
            except asyncio.CancelledError:
                raise
            except WorkerError as error:
                counters.lastError = error.code
                if error.code == "not_found":
                    counters.noMatches += 1
                else:
                    on_error(source, error)
        return selected

"""Lazy candidate priority, identity-gated field fallback, and review evidence."""
from __future__ import annotations

import asyncio
from collections.abc import Callable
from dataclasses import replace

from .adapters import ADAPTERS, DEFAULT_SOURCES, Adapter
from .models import (
    Candidate,
    WorkerError,
    canonical,
    canonical_id,
    is_catalog_identifier,
    normalize,
)
from .scheduler import Scheduler


class Matcher:
    def __init__(self, scheduler: Scheduler, adapters: dict[str, Adapter] | None = None):
        self.scheduler = scheduler
        self.adapters = adapters if adapters is not None else ADAPTERS

    async def match(self, query: str, sources: tuple[str, ...] = DEFAULT_SOURCES,
                    on_error: Callable[[str, WorkerError], None] = lambda *_: None) -> list[Candidate]:
        # Preserve priority and avoid speculative traffic. Parallelism is across
        # independent match requests, not unconditional calls to every provider.
        selected: list[Candidate] = []
        for priority, source in enumerate(sources, 1):
            adapter = self.adapters[source]
            counters = self.scheduler.counters(source)
            try:
                response = await self.scheduler.fetch(adapter.request(query))
                try:
                    raw_movies = adapter.parse(response.body)
                    if (source == "xcity" and raw_movies and is_catalog_identifier(query)
                        and any(canonical_id(raw.identifier) != canonical_id(query) for raw in raw_movies)):
                        raise WorkerError("identity_mismatch", "Detail page identifier does not match the request")
                    candidates = [candidate for raw in raw_movies if (candidate := normalize(
                        raw, source=source, query=query, body=response.body, fixture=response.fixture)) is not None]
                except WorkerError:
                    counters.parserFailures += 1
                    raise
                candidates.sort(key=lambda candidate: (-candidate.confidence, candidate.id, candidate.title))
                candidates = [replace(candidate, provenance={**candidate.provenance, "sourcePriority": str(priority)}) for candidate in candidates]
                if not candidates:
                    counters.noMatches += 1
                    if selected and raw_movies:
                        selected[0].provenance["rejectedFallback"] = f"{source}:identifier_or_title_mismatch"
                    continue
                counters.matches += len(candidates)
                if not selected:
                    selected = candidates
                    if len(selected) > 1:
                        # Never collapse conflicting IDs or pick a winner solely
                        # because it happened to arrive first or sort first.
                        return [replace(candidate, provenance={**candidate.provenance,
                            "requiresReview": "true", "reviewReasons": "ambiguous_candidates"}) for candidate in selected]
                    if selected[0].year is not None or selected[0].provenance["matchMethod"] != "exact_identifier":
                        return selected
                    # Only missing optional year can trigger another source in this slice.
                    continue
                primary = selected[0]
                same_identity = [candidate for candidate in candidates if canonical_id(candidate.id) == canonical_id(primary.id)]
                if len(same_identity) != 1:
                    primary.provenance["rejectedFallback"] = f"{source}:identity_conflict_or_ambiguity"
                    primary.provenance["requiresReview"] = "true"
                    primary.provenance["reviewReasons"] = "fallback_identity_conflict"
                    continue
                fallback = same_identity[0]
                provenance = dict(primary.provenance)
                if canonical(primary.title) != canonical(fallback.title):
                    provenance["rejectedTitle"] = f"{source}:lower_priority_conflict"
                    provenance["requiresReview"] = "true"
                    provenance["reviewReasons"] = "conflicting_title"
                if primary.year is None and fallback.year is not None:
                    # A good prior title is never overwritten. Only a valid missing
                    # field is filled, with its original source/selector preserved.
                    provenance.update({key: value for key, value in fallback.provenance.items() if key.startswith("field.year")})
                    provenance["field.year.fallbackPriority"] = str(priority)
                    provenance["field.year.bodySha256"] = fallback.provenance["bodySha256"]
                    provenance["field.year.identityGate"] = "exact_canonical_identifier"
                    selected = [replace(primary, year=fallback.year, provenance=provenance)]
                    return selected
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

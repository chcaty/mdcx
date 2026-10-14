# Offline source contract correction

Historical implementation/audit record. Later configured-source and portable-delivery changes are documented in [architecture](architecture.md), [validation](validation.md) and [Windows delivery](windows-delivery.md); the scope statements below describe their recorded baseline.

Baseline preserved: `e289bc88e951a3c599b61a271bac9571ae7e48db`.

## Why this correction is required

The first prototype exercised real parsing of handcrafted raw HTML/JSON, but its fixtures were structurally narrow. They were not captured or sanitized provider responses. Two shortcuts made the initial acceptance evidence weaker than a real adapter contract:

1. xcity's fabricated search request directly returned detail HTML. The real historical adapter searches `result_published/`, extracts detail links, and then reads a separate detail page.
2. ThePornDB's synthetic `slug` values looked like `DEMO-001` catalog numbers. In the existing adapter, a slug addresses an internal movie record. Its display number is derived separately from site/date or title. Treating that slug as an xcity `hinban` would create false identity evidence and unsafe cross-source enrichment.

Passing the original fixture tests did not establish real-site compatibility or correct identifier equivalence. The correction removes those shortcuts rather than presenting them as a quality benchmark.

## Identity boundary

A candidate now distinguishes:

- `source` + `sourceId`: provider record identity (xcity detail route ID or exact ThePornDB slug)
- `catalogIdentifier`: nullable `{namespace, value}` established from a specific metadata field; xcity `hinban` uses `xcity:hinban`
- `id`: backward-compatible UI/display identifier; ThePornDB values without catalog evidence are explicitly source-qualified

A ThePornDB record cannot declare an xcity catalog identity merely because its slug resembles a number. Missing catalog evidence remains visible and requires human review. Namespaced equality or explicit trustworthy cross-reference evidence is required before any future cross-provider field merge. Display-ID similarity is never that evidence.

JSONL framing stays at version 1, while candidate provenance declares identity schema 2. Upgrade the client and worker together: candidates without this identity schema and separated fields are rejected rather than guessed.

The C# process boundary validates the provider, source ID, catalog/display consistency and exact supported historical detail-route URL. Query, fragment, port, wrong-host and route-ID mismatches are rejected. No URL is fetched by this validation.

## Offline request contract

The worker correction exercises separate search/detail response shapes through strict request replay. Real historical origin/route strings appear in recorded expectations solely to check request construction. Replay performs no DNS, sockets, authentication or remote writes. The adapter policy remains fixture-only by default; no credential field is accepted in the JSONL protocol.

The supported subset is based on the checked-in legacy implementation, not a current live-service guarantee:

- xcity search HTML → selected, validated numeric-ID detail route → detail HTML
- ThePornDB movie search `data[]` → selected provider slug route → detail `data{}`

The original runtime/source lineage remains in `scraper-audit.md`. Neutral fictional metadata and synthetic response bodies are deliberately retained. A realistic shape is useful contract coverage; it is not evidence that a source is currently reachable, authorized, complete or accurate.

## Scope kept unchanged

No desktop UI files are changed. The existing interface is retained only as a functional integration harness while visual redesign is reviewed separately. No live HTTP implementation, cookies, actual API tokens, user-media writes, NFO output, full legacy source migration or automatic acceptance is added.

Before enabling real access, confirm current source documentation/permissions, select allowed providers, implement reviewed TLS/redirect/DNS/body/auth boundaries, use a secure credential flow when required, and test an explicitly authorized small sample. The offline implementation can be completed without those decisions, but live connection cannot be claimed without them.

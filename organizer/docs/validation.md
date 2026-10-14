# Validation record

Verified 2026-10-14 UTC on **Debian GNU/Linux 13, x86-64**, with Microsoft .NET SDK **10.0.402** and Python **3.12.14**. This records the final local candidate, not a released product or a live scraping benchmark.

## Passed

- Locked NuGet restore for all four projects
- Complete Release solution build: **0 warnings, 0 errors**, warnings-as-errors and current SDK analyzers enabled
- Core/infrastructure executable checks: **32/32 passed**
- Desktop MVVM interaction smoke: **24/24 checks passed**
- Python parser/scheduler/protocol/quality tests: **138/138 passed**
- Python `compileall`
- Ruff check for the new worker
- .NET folder-mode whitespace verification, and Git whitespace check
- Current NuGet vulnerability query for Desktop including transitive packages: no vulnerabilities reported by the queried NuGet source; this is not a permanent security guarantee
- Real Avalonia Headless + Skia renders for the candidate-review and plan-preview tabs, inspected at 1480 × 960
- Code/data-safety review followed by regression tests for each substantive issue found

All media used by verification were tiny fictional placeholders in temporary directories. The Core checks and Desktop smoke use isolated temporary databases. Source-file byte preservation is asserted; plans do not create their target directories. No live provider, user credentials/cookies, real media files, GitHub writes, PRs, merges, signing or deployment were used.

## Covered behaviors

Core checks cover Unicode/NFKC identifier parsing, ambiguous IDs, directory recursion, versions remaining separate, sidecar ownership, cancellation/repeatability, linked paths, dry-run determinism, manual correction, traversal, Windows reserved/invalid names, case/NFC collisions, existing destinations, non-directory ancestors, UTF-8 component limits, truncation edge cases, media/sidecar changes, incomplete-scan review preservation, stale-review race rejection, SQLite integrity/idempotence/migration refusal, and bounded subprocess behavior under noisy, malformed, oversized, stale, timed-out, cancelled, or nonterminal-error output.

Desktop checks cover the scan→match→explicit review→preview workflow, filtering, candidate provenance/manual locks, no mutation, review persistence, stale index rejection, repeated actions, cancellation/restart, late results, source/selection changes and failure-to-idle recovery. They test the ViewModel workflow; rendered screenshots separately exercise the real XAML layout.

Worker tests cover authored HTML/JSON parsing, strict search→detail request replay, separate provider/catalog/display identities, exact-ID negative cases, edition/leading-zero handling, ambiguous title candidates, valid field preservation, generic missing-year fallback only with explicit shared catalog evidence, field provenance, lazy priority, no sockets, global/per-host high-water marks, retry classification, Retry-After/cooldown/admission, cancellation, body bounds, protocol limits, duplicate IDs, malformed follow-ups and clean process streaming/EOF. The two shipped adapters have no established cross-provider catalog mapping, so they do not enrich one another. See worker README for precise coverage boundary.

## Offline contract correction after the delivered baseline

The delivered `e289bc8` baseline remains in local history. Its recorded checks were 31 Core, 24 UI and 87 Python tests. The correction adds an independent review and full rerun with the counts above; no Desktop or legacy source files changed. Existing screenshots show the unchanged baseline interface, whose visual redesign is a separate task.

- xcity now replays a separate result-list request and numeric-ID detail request; its catalog field is namespaced `xcity:hinban`
- ThePornDB now replays a search `data[]` and selected-slug detail `data{}`; its slug cannot become a catalog identifier or join another provider by resemblance
- Literal request expectations verify GET, complete URL/query encoding, headers and inert credential-reference shape independently of adapter builders
- Tests block socket construction, connection helpers and `urlopen`, then assert zero calls for fixture and replay matching; no external provider request is made
- Wrong route/detail identity, duplicate required DOM fields, mixed valid/broken results, access denial, the ten-detail cap and detail-stage cancellation have regression coverage
- The C# boundary requires identity schema 2 and rejects inconsistent source IDs, catalog/display identity, URL routes and missing review requirements

All provider bodies are hand-authored neutral synthetic fixtures, not captured or sanitized real responses. These tests validate a historical contract subset derived from repository code, not current live-site accuracy. xcity's numeric record identity is response-route-bound unless an optional canonical link supplies additional evidence. See `offline-contract-correction.md`.

## Review-driven regressions fixed

- Nonterminal source errors no longer abort a valid fallback at the C# boundary
- Incomplete scans no longer prune saved human reviews
- Review writes compare the inspected fingerprint transactionally rather than stamp stale approval onto a newer indexed file
- Sidecars now have change fingerprints and portable suffix checks
- UTF-8 component limits and post-truncation empty/trailing-name checks prevent falsely safe paths
- Teardown is bounded when an arbitrary worker's orphan child holds inherited pipes
- Incomplete candidate payloads cannot enter the UI without valid source/provenance/URL/year fields

## Not verified / not implemented

- Windows/macOS execution, native system folder pickers and real desktop keyboard behavior
- Live-source accuracy, permission/authentication/TLS integration, rate limits observed on real sites, all legacy adapters, full legacy application test suite
- Large-library throughput/memory benchmarks, content-hash duplicate proof, ffprobe, or installer/signing/update behavior
- Real file execution, recovery, undo, NFO/image output, or journal crash safety (design only)
- Semantic `dotnet format` could not connect its out-of-process build-host pipe in this sandbox. Folder-mode whitespace validation and the full compiler/analyzer build passed; semantic formatting is not claimed as passed.

The subprocess boundary is not a security sandbox for arbitrary replacement scripts. The shipped worker starts no child processes and has no live-network implementation. Orphan descendants after an arbitrary parent exits are not guaranteed to be terminated; pipe cleanup is bounded.

## Reproduce

Run `scripts/check.sh` (Linux/macOS) or `scripts/check.ps1` (PowerShell; supplied but not executed on Windows here). The shell script accepts `DOTNET` and `PYTHON` executable overrides. Headless screenshots can be reproduced with:

```sh
dotnet src/Organizer.Desktop/bin/Release/net10.0/Organizer.Desktop.dll --demo /path/to/synthetic-source --screenshot /tmp/review.png
# Add --screenshot-plan for the plan inspector.
```

In the restricted validation environment, SDK/cache/runtime directories were redirected to writable workspace directories and parallel build servers disabled. Normal installations can use their usual .NET SDK/user cache. No OS security configuration was changed to run checks.

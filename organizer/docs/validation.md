# Validation record

The record below describes the original Linux-only source candidate. The subsequent Windows packaging pipeline and its observed run results are documented separately in [Windows delivery](windows-delivery.md); historical limitations below should not be interpreted as a current CI result.

Verified 2026-10-14 UTC on **Debian GNU/Linux 13, x86-64**, with Microsoft .NET SDK **10.0.402** and Python **3.12.14**. This records the final local candidate, not a released product or a live scraping benchmark.

## Passed

- Locked NuGet restore for all four projects
- Complete Release solution build: **0 warnings, 0 errors**, warnings-as-errors and current SDK analyzers enabled
- Core/infrastructure executable checks: **34/34 passed**
- Desktop MVVM interaction smoke: **68/68 checks passed**
- Actual Avalonia Headless pointer/keyboard checks: **20/20** at normal size and **22/22** at 1200×800 with 125% text
- Python parser/scheduler/protocol/quality tests: **138/138 passed**
- Python `compileall`
- Ruff check for the new worker
- .NET folder-mode whitespace verification, and Git whitespace check
- Current NuGet vulnerability query for Desktop including transitive packages: no vulnerabilities reported by the queried NuGet source; this is not a permanent security guarantee
- Real Avalonia Headless + Skia renders of the approved workbench at 1440×900, reviewed-only preview, empty state, 1200×800, 125% text and combined small/large-text state
- Synthetic 1,000-row long-path render with source-byte preservation; this is not a throughput benchmark
- Build-telemetry opt-out preflight: actual environment required, negative and positive cases verified before the statistics task
- Code/data-safety review followed by regression tests for each substantive issue found

All media used by verification were tiny fictional placeholders in temporary directories. The Core checks and Desktop smoke use isolated temporary databases. Source-file byte preservation is asserted; plans do not create their target directories. No live provider, user credentials/cookies, real media files, GitHub writes, PRs, merges, signing or deployment were used.

## Covered behaviors

Core checks cover Unicode/NFKC identifier parsing, ambiguous IDs, directory recursion, versions remaining separate, sidecar ownership, cancellation/repeatability, linked paths, dry-run determinism, manual correction, traversal, Windows reserved/invalid names, case/NFC collisions, existing destinations, non-directory ancestors, UTF-8 component limits, truncation edge cases, media/sidecar changes, incomplete-scan review preservation, stale-review race rejection, SQLite integrity/idempotence/migration refusal, and bounded subprocess behavior under noisy, malformed, oversized, stale, timed-out, cancelled, or nonterminal-error output.

Desktop workflow checks cover scan→match→explicit review→preview, independent focus/checkboxes, hidden selection retention, same-snapshot rescan, bounded batch matching, sequential confirmation, invalid/out-of-queue confirmation, typed title/provider queries, unresolved ambiguity, preserved manual reviews, undo, sidecar-only change rejection, legacy-review notices, persisted identity evidence, stale index rejection and close-after-commit cancellation. The separate Headless suite sends actual pointer/key events to realized XAML controls, including header mixed→all→none, filtering, Tab/shortcuts, navigation, inspector controls and logs. The small/large-text run also verifies wheel access to the lower naming draft.

Worker tests cover authored HTML/JSON parsing, strict search→detail request replay, separate provider/catalog/display identities, exact-ID negative cases, edition/leading-zero handling, ambiguous title candidates, valid field preservation, generic missing-year fallback only with explicit shared catalog evidence, field provenance, lazy priority, no sockets, global/per-host high-water marks, retry classification, Retry-After/cooldown/admission, cancellation, body bounds, protocol limits, duplicate IDs, malformed follow-ups and clean process streaming/EOF. The two shipped adapters have no established cross-provider catalog mapping, so they do not enrich one another. See worker README for precise coverage boundary.

## Offline contract correction after the delivered baseline

The delivered `e289bc8` baseline remains in local history. Its recorded checks were 31 Core, 24 UI and 87 Python tests. The separate `b9e4309` contract correction passed 32 Core, 24 UI and 138 Python tests without Desktop changes. The later approved native redesign is described below; legacy source files remain unchanged.

- xcity now replays a separate result-list request and numeric-ID detail request; its catalog field is namespaced `xcity:hinban`
- ThePornDB now replays a search `data[]` and selected-slug detail `data{}`; its slug cannot become a catalog identifier or join another provider by resemblance
- Literal request expectations verify GET, complete URL/query encoding, headers and inert credential-reference shape independently of adapter builders
- Tests block socket construction, connection helpers and `urlopen`, then assert zero calls for fixture and replay matching; no external provider request is made
- Wrong route/detail identity, duplicate required DOM fields, mixed valid/broken results, access denial, the ten-detail cap and detail-stage cancellation have regression coverage
- The C# boundary requires identity schema 2 and rejects inconsistent source IDs, catalog/display identity, URL routes and missing review requirements

All provider bodies are hand-authored neutral synthetic fixtures, not captured or sanitized real responses. These tests validate a historical contract subset derived from repository code, not current live-site accuracy. xcity's numeric record identity is response-route-bound unless an optional canonical link supplies additional evidence. See `offline-contract-correction.md`.

## Approved native redesign

The approved 1440×900 layout is now implemented as native controls. Independent visual comparison passed for the normal frame; small/large-text states were separately rendered and inspected. The source identity correction remains intact. Additional data-safety review drove the guarded review invalidation, sidecar snapshot, candidate-switch and close-after-commit regressions. See `ui-redesign.md` for exact behavior and conservative handling of older unstamped reviews.

The final verification explicitly disabled both .NET SDK and Avalonia build telemetry. An installed official Avalonia.BuildServices README revealed the separate opt-out during this pass; earlier builds set only the .NET variable, so absence of earlier anonymous build telemetry cannot be proved retrospectively. No source/provider/media content is sent by the shipped fixture worker.

## Review-driven regressions fixed

- Nonterminal source errors no longer abort a valid fallback at the C# boundary
- Incomplete scans no longer prune saved human reviews
- Review writes compare the inspected fingerprint transactionally rather than stamp stale approval onto a newer indexed file
- Sidecars now have change fingerprints and portable suffix checks
- UTF-8 component limits and post-truncation empty/trailing-name checks prevent falsely safe paths
- Teardown is bounded when an arbitrary worker's orphan child holds inherited pipes
- Incomplete candidate payloads cannot enter the UI without valid source/provenance/URL/year fields

## Not verified / not implemented

- Windows/macOS execution, native system folder pickers, clipboard and real desktop keyboard/chrome behavior; Headless routed input is not a substitute for platform testing
- Live-source accuracy, permission/authentication/TLS integration, rate limits observed on real sites, all legacy adapters, full legacy application test suite
- Large-library throughput/memory benchmarks, content-hash duplicate proof, ffprobe, or installer/signing/update behavior
- Real file execution, recovery, undo, NFO/image output, or journal crash safety (design only)
- Semantic `dotnet format` could not connect its out-of-process build-host pipe in this sandbox. Folder-mode whitespace validation and the full compiler/analyzer build passed; semantic formatting is not claimed as passed.

The subprocess boundary is not a security sandbox for arbitrary replacement scripts. The shipped worker starts no child processes and has no live-network implementation. Orphan descendants after an arbitrary parent exits are not guaranteed to be terminated; pipe cleanup is bounded.

## Reproduce

Run `scripts/check.sh` (Linux/macOS) or `scripts/check.ps1` (PowerShell; supplied but not executed on Windows here). The shell script accepts `DOTNET` and `PYTHON` executable overrides. Headless screenshots can be reproduced with:

```sh
dotnet src/Organizer.Desktop/bin/Release/net10.0/Organizer.Desktop.dll --demo /path/from/create_demo.py --screenshot-fixture --screenshot /tmp/review.png
# Add --screenshot-plan for reviewed-only preview.
# Add --screenshot-width 1200 --screenshot-height 800 --font-scale 1.25 for the combined responsive case.
```

In the restricted validation environment, SDK/cache/runtime directories were redirected to writable workspace directories and parallel build servers disabled. Normal installations can use their usual .NET SDK/user cache. No OS security configuration was changed to run checks.

## Configurable metadata correction (2026-10-14)

A later local correction starts from delivered `e8c540e156235e716f13888717a905efc522c89a`. The complete Linux aggregate passed with .NET 10.0.402 and Python 3.12.14 plus the exact hash-locked HTTP dependency set:

- Release build: 0 warnings/errors; locked restore and analyzers
- Core/infrastructure: 56/56 checks, including persistent JSONL session reuse, 8192-query lifetime budget, strict identity/terminal validation, cancellation/stale replies and concurrent shutdown admission
- Desktop MVVM: 140/140 checks, including explicit save/enable/reset, restart, no automatic query, error semantics and settings changes during in-flight work
- Actual routed-input views: 28/28 at 1440×900, 30/30 at 1200×800 with 125% text; settings dialog rendering and unverified-online disclosure included
- Python provider/transport/protocol/quality: 216/216 tests
- Dependency packaging: 13/13 tests; installer compiler parser: 11/11 checks
- Ruff, actionlint, PowerShell parsing, compileall, whitespace and negative/positive telemetry preflight
- Independent transport/identity/configuration/packaging and final concurrency review, with both discovered shutdown races fixed and retained as regressions

The persistent-session implementation owner additionally ran three final 56/56 repeats, each including the full 8192-query stress and 24 concurrent admission/disposal iterations across both modes. This is a bounded correctness stress, not a throughput claim.

The desktop integration starts the real JSONL entrypoint with an explicitly saved temporary configuration, injects HTTPX MockTransport, parses neutral HTML, returns candidates and checks UI state. Two matches reuse one worker PID. Socket/DNS guards prohibit external requests. Missing page identity for the unchanged `BF-658` query produces a parsing failure with zero candidates. No metadata for that identifier was invented or retrieved online.

The updated Windows portable ZIP must pass its own CI run: it bundles isolated Python plus seven pinned HTTP wheels and tests both native Win32 demo startup and a bundled neutral HTTP mock probe, plus adjacent data ownership, upgrade retention and clear startup errors. The older installer/uninstall record is separate. Refer to that run's artifact reports for actual Windows results. Linux checks alone do not establish Windows installation or live-source availability. No source site, images, credentials or real media were used by this correction's verification.

## Portable delivery correction (2026-10-14)

The user requested a portable build instead of an installer. The final local Linux checks for this additional change passed with the same SDK, Python and locked dependencies:

- Core/infrastructure: 56/56
- Desktop MVVM: 196/196, including portable path ownership, malformed/duplicate marker and manifest handling, linked data/SQLite sidecars, writable checks, preservation, future-schema refusal and explicit diagnostic isolation
- Actual Headless routed-input: 31/31 normal, 33/33 small/125% text; readable startup error rendering and nonzero diagnostic failures
- Python: 216/216; dependency packaging: 13/13; portable archive/layout: 55/55; retained installer parser: 11/11
- Independent portable resolver/adversarial checks: 46/46; independent archive suite: 55/55; final code and visual review passed

The ZIP uses an explicit marker and fixed adjacent data, does not contain user data, and does not require the optional Inno installer path. A manifest-identified portable build refuses a missing marker instead of switching to installed storage. Native Windows CI is responsible for the actual extracted build, Unicode/space paths, worker, startup/error screenshots, readonly ACL/data junction failures, upgrade preservation and absence of installation registration/shortcuts. Refer to the exact successful run before claiming those native checks passed. No live-source or BF-658 online verification is included.

## Windows ZIP-name CI correction

The first configured-source/portable CI run at `0e48daa` passed the full Linux job but stopped the Windows job in dependency-archive tests before .NET/native package acceptance. Python's Windows `ZipInfo` normalized a malicious backslash before the test wrote it; the verifier also inspected only the normalized name. The correction preserves adversarial bytes in both ZIP headers and checks `orig_filename` before any extraction, rejecting separator normalization and NUL truncation. Dependency tests increased to 16 and pass locally both normally and under simulated Windows separator behavior; all seven pinned official wheels still validate. No test was skipped or weakened. The following CI run remains the authority for actual Windows results.

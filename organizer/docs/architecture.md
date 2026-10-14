# Architecture decisions and next boundary

## ADR 001: native workbench and small domain core

Avalonia is the desktop shell, C# records/services are the domain core, Microsoft.Data.Sqlite owns the local index, and a Python 3.12+ JSONL child process is the scraper boundary. No React dependency, HTTP backend, server port, or paid grid control is needed. The legacy app remains byte-for-byte unchanged at the base commit. The target product's final primary OS is not confirmed; this candidate was developed/tested on Linux and retains cross-platform APIs.

`Organizer.Core` knows nothing about UI, SQLite, or Python. `Organizer.Infrastructure` owns indexing and subprocess lifetime. `Organizer.Desktop` owns presentation state, explicit review and selection/generation guards. `worker/` owns parsing, validation, source policy, bounded scheduling and replay diagnostics. Protocol schema is versioned; C# tolerates unknown additive fields but rejects incompatible versions, oversized output, invalid candidates and premature process exit.

## ADR 002: source preservation precedes automation

A media asset is a specific video path, not a title/identifier. Versions and possible duplicates remain separate; content equality is not inferred. Scanner IDs hash the full source path; size+mtime fingerprints are change detection, **not content hashes**. Sidecars require unambiguous stem ownership. Unknown names can be manually reviewed. Ambiguous sidecars block planning until future ownership review exists.

Plans are deterministic immutable data. Case-fold/NFC collision checks are conservative across platforms; links/reparse points are skipped/blocked, target ancestors are checked, and path length is constrained conservatively. Metadata cannot inject directories. All plan operations are suggestions only. A rescan can race with filesystem changes: no claim of a locked snapshot is made. A future executor must revalidate and use platform-specific safe handles immediately before each operation.

The scanner currently returns a complete read-only scan result; it is cancellable and the UI list is virtualized, but it is not a streaming million-file index. Next scaling work is bounded scan batches, progress, source-watch reconciliation, cancellation-aware target lookup and measured benchmarks. No throughput claims are made.

## ADR 003: local index ownership

SQLite schema `user_version=1` creates scans, assets, reviews, plans and tasks in one migration transaction. A single asynchronous semaphore serializes connection access; blocking SQLite operations run away from the UI thread. WAL, foreign keys and bounded busy timeout are enabled. Scan refresh is atomic. Incomplete scans never prune missing assets/reviews. Review writes require the fingerprint the user actually inspected and reject a newer indexed fingerprint. A changed fingerprint removes an old review; removed paths cascade review deletion. Repeated plan IDs upsert rather than multiply. Future-schema databases are refused without destructive downgrade.

Manual review is the authority for this slice. Candidate parsing does not directly update an accepted review, file, NFO or image. Stored provenance preserves the basis of acceptance. Before real metadata replacement is added, implement a field diff with `manual/locked > existing verified > new scrape`, explicit replacement policy, raw/original title separate from display/translation, and per-element provenance for lists.

## ADR 004: worker lifetime and scheduling

Version 1 uses newline-delimited JSON on stdin/stdout; stderr is diagnostics only. Each .NET match uses an isolated child process, drains both output streams concurrently, bounds event size/count/candidates, enforces a deadline, ignores stale request IDs, and terminates the running worker tree on cancellation or completion with bounded pipe teardown. This is process separation, not a security sandbox: an arbitrary replacement worker can access the user account, and orphan descendants after a parent has already exited are not guaranteed to be killed. The shipped fixture worker spawns no children. This deliberately avoids long-lived cross-request contamination in the first slice. The worker itself supports bounded concurrent requests, duplicate-ID rejection, terminal-state discipline and a cancellation command, allowing future session reuse.

Candidate fallback is ordered/lazy; traffic is not blindly fanned out. The scheduler bounds global/per-host work and retries only transient failures while respecting Retry-After. Source health is diagnostic, not metadata accuracy. This release only exposes fixture transport; no live-network opt-in is hidden in UI. The actual parser migration and protocol are documented by the worker.

## Future execution journal (design only, not enabled)

1. Persist immutable reviewed plan, source identity/fingerprint, target nonexistence, per-item desired operation and user approval boundary. Never treat an old preview as execution permission.
2. Acquire an application execution lease; resolve source/target to safe platform handles; reject links/reparse surprises, case/Unicode conflicts, stale bytes and cross-device ambiguity.
3. Write a durable `prepared` journal entry before any filesystem mutation. For cross-volume copies, write a temporary file in the target directory, verify size/content hash and flush before atomic no-overwrite publish. Source deletion needs a separate committed step, never inferred from successful copy.
4. Mark each operation `published`, then `committed`; preserve enough data for idempotent crash recovery. Sidecars are separate journalled items in the same group. Interrupted groups remain visible rather than silently declaring success.
5. Undo verifies the current target still matches the journalled identity and original path remains free. It must never overwrite newly created data. Explain non-reversible outcomes and ask at the proper permission boundary.

Before enabling execution: platform integration tests for no-overwrite semantics, hardlinks/reparse points, crash injection at every journal transition, disk-full/permission errors, cross-volume moves, interruption/restart, rollback races, and independent safety review. None of this design is represented as implemented today.

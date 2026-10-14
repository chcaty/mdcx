# MDCx Media Organizer · local vertical slice

A new native **Avalonia + .NET 10** media-file workbench, kept separate from the legacy app. The shell is Chinese; the core and tests are cross-platform. This first candidate is **preview-only**: there is no move, rename, overwrite, delete, NFO-write, or remote-publish API.

## What works now

- Choose a source directory and scan recursively without following symbolic links/reparse points.
- Keep each video/version as a separate asset. Recognize common catalog IDs, attach unambiguous named subtitles/sidecars, and surface missing/ambiguous identifiers. Equal IDs are not treated as proof of duplicate content.
- Dense native workbench: source/status panel, virtualized file grid, metadata/candidate/plan inspector, task log; filtering, keyboard shortcuts, cancellation and stale-result protection.
- Run the isolated Python worker against hand-authored, structure-inspired neutral HTML/JSON fixtures; inspect evidence, provenance, ambiguity and source fallback. Every candidate in normal interactive mode needs a human click to accept. Manual correction is supported.
- Persist the scan index, reviewed metadata and dry-run plans in a versioned application-owned SQLite database. Changed source fingerprints invalidate old reviews. Repeated plans are idempotent by content ID.
- Preview deterministic target filenames and preserved subtitle language suffixes; block collisions, existing targets, unsafe ancestors, traversal, links, changed source files, duplicate selections, reserved-name/path-limit problems.

## Run

Prerequisites: **.NET SDK 10.0.x**, **Python 3.12+**, and a desktop supported by Avalonia. Packages are pinned with lock files. The legacy app still separately requires Python >=3.13.4 and its own dependencies; the new fixture worker uses only the Python standard library.

From this directory:

```sh
dotnet restore MediaOrganizer.sln --locked-mode
dotnet run --project src/Organizer.Desktop
```

If Python is named `python` on Windows, pass `-- --python python`. `--worker /absolute/path/to/worker/main.py` overrides automatic repository lookup. The app is fixture-only even when pointed at a real source folder: real folder scanning is read-only; matching uses synthetic metadata, visibly labeled. Do not accept demo metadata as real metadata.

Create a safe demo source:

```sh
python3 scripts/create_demo.py
# Copy the printed source path into the app, or:
dotnet run --project src/Organizer.Desktop -- --demo /printed/source/path
```

The demo script only writes placeholder files in a newly created temporary directory. Delete that demo directory yourself when finished.

## Verify

```sh
./scripts/check.sh
# Windows: run the dotnet restore/build/run and Python unittest commands in check.sh separately.
```

The executable test harness returns nonzero on failure. It does not need a third-party test framework. Run offline worker tests with `python3 -m unittest discover -s worker/tests -v`. See `docs/validation.md` for actual checks and limitations rather than assuming all target operating systems were tested.

## Data location and safety

The index is `MDCxOrganizer/index.db` under the OS local application-data directory, never in the selected media directory. The index contains local filenames and metadata; keep it private. It is a rebuildable local index, not a media backup. Database writes are serialized, transactional and parameterized, with WAL/foreign keys and explicit migration version checks. The Python process cannot organize files and uses fixture transport only. No credentials are requested.

A safe preview is a point-in-time check, not authorization or proof that a future move will succeed. The planned journal/recovery design must be implemented and reviewed before a real executor can be enabled. See [architecture](docs/architecture.md), [scraper audit](docs/scraper-audit.md), and [licensing notice](NOTICE.md).

## Deliberately deferred

- Real move/copy/link execution, transactional crash recovery, rollback, undo and NFO/image writing
- Live/provider authentication, site health verification, all 38 legacy sources, rule/config migration, complete legacy metadata fields
- Real-poster downloads, ffprobe technical metadata, hashing-based duplicate detection, series/edition/multipart naming grammars
- Automatic acceptance, confidence calibration, embedding/AI matching, background unattended processing
- Packaged installers, signing, auto-update, Windows/macOS runtime verification, large-library performance claims

The current source parsers demonstrate a migrated subset and historical search/detail contracts under strict offline request replay, not a completed scraper replacement. Provider-internal IDs are separate from namespaced catalog identifiers; a ThePornDB slug is never treated as an xcity catalog number. Request routes are checked without opening sockets, and current live schema/availability remain unverified. Exact identity and field-provenance checks matter more than HTTP success. Test results describe frozen fictional fixtures; they do not establish live-site accuracy or coverage.

# MDCx Media Organizer · local vertical slice

A new native **Avalonia + .NET 10** media-file workbench, kept separate from the legacy app. The shell is Chinese; the core and tests are cross-platform. This first candidate is **preview-only**: there is no move, rename, overwrite, delete, NFO-write, or remote-publish API.

## What works now

- Choose a source directory and scan recursively without following symbolic links/reparse points.
- Keep each video/version as a separate asset. Recognize common catalog IDs, attach unambiguous named subtitles/sidecars, and surface missing/ambiguous identifiers. Equal IDs are not treated as proof of duplicate content.
- Approved warm-neutral/deep-green native workbench: virtualized media rows, independent checked batch and focused inspector, sequential review, unreviewed naming drafts and reviewed-only preview, collapsed logs, filtering and keyboard navigation.
- Choose the fictional demo mode or explicitly configure/enable the independent catalog-metadata source. The default never queries online. Inspect evidence, provenance, ambiguity and source failures; current source contracts are tested with neutral HTML/JSON and injected HTTP, not live-site acceptance. Every candidate in normal interactive mode needs a human click to accept. Manual correction is supported.
- Persist the scan index, reviewed metadata and dry-run plans in a versioned application-owned SQLite database. Changed source fingerprints invalidate old reviews. Repeated plans are idempotent by content ID.
- Preview deterministic target filenames and preserved subtitle language suffixes; block collisions, existing targets, unsafe ancestors, traversal, links, changed source files, duplicate selections, reserved-name/path-limit problems.

## Windows portable build

The feature branch's independent Windows CI produces a **self-contained portable ZIP** by default. Extract it to a writable folder and run `Organizer.Desktop.exe`; the archive includes .NET and Python. It does not install a service, write an uninstall registry entry or create shortcuts. Keep `portable.json`, `package-manifest.json`, `python/` and `worker/` with the executable. Configuration and index live in the adjacent `data/` directory. No user data is shipped in the archive.

Close the app and back up `data/` before upgrading. Extract the new program over the old program while retaining `data/`, or copy that folder into a freshly extracted version. Existing installed-app data is never imported, moved or deleted automatically. An unwritable or damaged portable location gives a clear startup error rather than silently switching data directories.

The executable is unsigned. Verify the ZIP source and SHA-256 before choosing to run it; do not disable Windows security controls. See [Windows delivery](docs/windows-delivery.md) for the exact CI acceptance, direct artifact format, limitations and data safety. A workflow file alone is not evidence that a build passed. The earlier e8c540e installer is fixture-only and does not contain the source-configuration correction.

## Run from source

Prerequisites: **.NET SDK 10.0.x**, **Python 3.12+**, and a desktop supported by Avalonia. Packages are pinned with lock files. The legacy app still separately requires Python >=3.13.4 and its own dependencies; the default fixture worker uses the Python standard library; the configured HTTP path and full test suite additionally use the locked HTTPX wheel set.

From this directory:

```sh
export AVALONIA_TELEMETRY_OPTOUT=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
python3 -m pip --isolated install --require-hashes --only-binary=:all: --no-deps -r worker/requirements-live.txt
dotnet restore MediaOrganizer.sln --locked-mode
dotnet run --project src/Organizer.Desktop
```

PowerShell: set `$env:AVALONIA_TELEMETRY_OPTOUT='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'` first. Both check scripts set these variables. A project preflight refuses the Avalonia build task unless its documented environment opt-out is present.

If Python is named `python` on Windows, pass `-- --python python`. `--worker /absolute/path/to/worker/main.py` overrides automatic repository lookup. Demo mode always uses synthetic metadata, visibly labeled; it cannot establish real-source absence. Real folder scanning remains read-only. Source settings start disabled and need an explicit save/enable action; selecting a mode or scanning never sends a query. Live availability is unverified, and the current supported real-provider subset is ordinary catalog IDs only. Do not accept demo metadata as real metadata.

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
# Windows: ./scripts/check.ps1
```

The executable test harness returns nonzero on failure. It does not need a third-party test framework. Run offline worker tests with `python3 -m unittest discover -s worker/tests -v`. See `docs/validation.md` for actual checks and limitations rather than assuming all target operating systems were tested.

## Data location and safety

Portable builds use `data/index.db` beside the executable; source settings are `data/metadata-source.json`. Installed/development builds without the portable marker retain the OS local application-data `MDCxOrganizer` directory. The index contains local filenames and metadata; keep it private. It is a rebuildable local index, not a media backup. Database writes are serialized, transactional and parameterized, with WAL/foreign keys and explicit migration version checks. The Python process cannot organize files. Default demo mode uses fixture transport; the separate configured source needs explicit enablement. No credentials are requested or stored. Source settings share the application-owned data directory. Portable updates preserve that folder; the older optional installer also retains its separate data on uninstall. Media-group review stamps include sidecar change fingerprints; editing withdraws stored acceptance, while undo rechecks the snapshot. Older unstamped preview-version reviews require an explicitly surfaced recheck.

A safe preview is a point-in-time check, not authorization or proof that a future move will succeed. The planned journal/recovery design must be implemented and reviewed before a real executor can be enabled. See [architecture](docs/architecture.md), [scraper audit](docs/scraper-audit.md), and [licensing notice](NOTICE.md).

## Deliberately deferred

- Real move/copy/link execution, transactional crash recovery, rollback, undo and NFO/image writing
- Live-site acceptance, provider authentication, all 38 legacy sources, rule/config migration, complete legacy metadata fields
- Real-poster downloads, ffprobe technical metadata, hashing-based duplicate detection, series/edition/multipart naming grammars
- Automatic acceptance, confidence calibration, embedding/AI matching, background unattended processing
- Signing, auto-update, macOS verification, broad Windows hardware/desktop validation, large-library performance claims

The source parsers and new configurable HTTP path implement a narrow metadata subset, not a completed scraper replacement. Current acceptance uses offline injected request/response tests; it does not establish real-site availability or accuracy. Provider-internal IDs are separate from namespaced catalog identifiers; a ThePornDB slug is never treated as an xcity catalog number. Request routes are checked without opening sockets, and current live schema/availability remain unverified. Exact identity and field-provenance checks matter more than HTTP success. Test results describe frozen fictional fixtures; they do not establish live-site accuracy or coverage.

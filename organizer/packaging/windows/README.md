# Windows x64 portable preview packaging

The default delivery is one directly downloadable **portable ZIP**. Extract it
once into a writable folder and run `Organizer.Desktop.exe`. No installer,
registry registration, shortcut, system Python, .NET installation, or runtime
package download is required. The app keeps its private data next to itself.

Build on GitHub's `windows-2022` runner with .NET SDK **10.0.402**. Inno Setup is
not discovered, required or executed by the portable build. For manual commands,
set telemetry environment variables **before starting PowerShell**, including
`POWERSHELL_TELEMETRY_OPTOUT=1`, `DOTNET_CLI_TELEMETRY_OPTOUT=1` and
`AVALONIA_TELEMETRY_OPTOUT=1`; setting PowerShell's opt-out inside a script is too
late for process startup. The workflow sets these at job-process scope.

```powershell
pwsh -NoProfile -File organizer/scripts/package-windows.ps1 -PackageKind portable -BuildLabel "ci-123-1"
pwsh -NoProfile -File organizer/scripts/test-windows-portable.ps1 `
  -Archive organizer/artifacts/windows/MDCxOrganizer-0.1.0-ci-123-1-win-x64-portable.zip
```

The native test intentionally requires GitHub Actions by default. Manual tests
require a **clean disposable Windows user account** and explicit `-AllowNonCi`.
It refuses an existing Organizer data folder, installation or running app,
accepts no media-source path, and changes ACLs only on its own synthetic data.

## Deliverables and archive contract

- `MDCxOrganizer-0.1.0-<build>-win-x64-portable.zip`: unsigned, directly extractable
- `SHA256SUMS.txt`: archive hash, also printed in the CI delivery summary
- Inside the ZIP: `README-WINDOWS.txt`, `package-manifest.json`, `portable.json`,
  app binaries, `python/`, `worker/`, `licenses/`, and an offline diagnostic
- Separate CI diagnostics: native JSON/PNG, offline HTTP probe, startup and
  friendly-error reports, `portable-test.json`, and build/check logs

The pinned official artifact action uses `archive: false` for the one package
file, so GitHub does not wrap the ZIP in another ZIP. There is no enclosing
program directory inside it. The README and manifest need no separate download.
The main executable lives at the archive root, beside .NET native libraries.
There is no `data/`, saved source configuration, SQLite index, installer,
uninstaller, shortcut, Python bytecode or test cache in the archive.

A unique hidden `.staging-*` directory remains for build debugging but is never
uploaded. Publishing is folder-based self-contained `win-x64`, neither trimmed
nor single-file. Windows RID restores use separate checked-in locks and
`--locked-mode`. Users do not need an SDK, Python or pip.

`portable.json` is exactly `{"formatVersion":1}`. It selects the fixed adjacent
`data/` directory; it cannot redirect writes to arbitrary locations. The
manifest records `packageKind: portable` and `dataMode: portable`. Known portable
builds fail closed if the marker disappears. Missing, malformed, linked or
unwritable portable storage produces a user-visible startup error, without
silently switching to `%LOCALAPPDATA%`. Keep the marker with the executable.

`portable-layout.ps1` validates both staging and archive inventories, hashes,
sizes, root layout and strict marker format. It rejects traversal, absolute
paths, Windows-invalid/reserved names, case-insensitive duplicate paths,
file/directory collisions, links, extra files, nested folder wrappers and user
data. Cross-platform tests exercise these checks using authored tiny files.

## Keeping data when updating

Close every app process and back up the entire adjacent `data/` folder first.
Extract a new verified ZIP into the same program folder and overwrite program
files, retaining `data/` and unknown user files. Do not delete the old folder
before extracting. Moving the whole closed app folder also moves its data.
There is no updater or migration from the optional installed version. Future
schema changes require their own compatibility verification; current acceptance
checks replacement and byte preservation using a synthetic previous-build file.

## Runtime provenance and isolation

`python-runtime.json` pins Python **3.13.16** from the official Python download
host and the SHA-256 printed on its release page. The download is checked
**before extraction or execution**. The original Python license and native
components are retained. `python313._pth` lists only `python313.zip`, `.`, `Lib/site-packages`, and
`../worker`; `import site` stays disabled. The fixture worker and independent
JavBus parser use the standard library. The opt-in HTTP transport adds HTTPX
**0.28.1**, httpcore **1.0.9**, AnyIO **4.14.2**, h11 **0.16.0**, idna **3.18**,
certifi **2026.7.22**, and typing_extensions **4.16.0** (the last supports
Python 3.11/3.12 development; it is harmless in the embedded 3.13 bundle).

`python-dependencies.json` pins each official PyPI wheel URL, byte size,
SHA-256, dependency metadata, license path, and release/source metadata URL.
`bundle_python_dependencies.py` uses only the embedded standard library to
download and verify the complete closure before extraction. It refuses native
wheels, relocation directories, executable `.pth` hooks, traversal, collisions,
missing licenses, unexpected metadata, and redirects. It never invokes pip,
build hooks, or entrypoints. The exact wheels are extracted under the private
`python/Lib/site-packages`; their original `.dist-info` and licenses remain there.
Copies of metadata/licenses plus `inventory.json` and the lock are also retained
under `licenses/python`. The app does not import the original Qt crawler,
lxml, or legacy global managers. Its source and complete fictional fixture corpus
are copied explicitly, without test caches. The app resolves these bundled paths
independently of its current directory. No runtime downloader is shipped.

The embedded distribution does not supply the Windows Universal C Runtime;
Windows 10/11 provide it as an OS component. No system component is downloaded or installed at runtime. Existing Python
`vcruntime` DLLs remain inside its bundle. The optional legacy installer checks
`ucrtbase.dll`; portable use relies on a supported, updated Windows system.

`collect-licenses.ps1` copies package manifests and all package-provided license
and third-party notice files. When a NuGet package declares only MIT or
Apache-2.0, it includes the full license plus that exact package's copyright
metadata. Unknown missing-license cases fail the build. The .NET runtime
license and third-party notices are mandatory, and Python, upstream project
README restrictions, GPLv3 text, and project NOTICE are retained. The original
license ambiguity is not resolved by this packaging work.

## Default configuration and HTTP boundary

`source-settings.example.json` is a disabled example, not an automatic opt-in:
`network_enabled` is false and `proxy` is null. The portable ZIP contains no `data/` subtree or saved user settings. Saving
source settings creates `<program>/data/metadata-source.json` next to `index.db`.
Program-file overwrite upgrades preserve both. The user must explicitly enable the independent JavBus
source before a metadata request can use HTTP. Fixture mode remains separate.

The HTTP implementation verifies TLS, ignores ambient proxies/credentials,
rejects cookies, redirects and compressed bodies, caps response sizes, and
uses bounded deadlines, retries, request concurrency and per-host pacing.
Only reviewed HTTPS catalog/detail routes at `https://www.javbus.com` are
accepted. An explicit credential-free HTTP(S) proxy is optional. No arbitrary
host, custom headers, credential entry, TLS-off switch, browser challenge
bypass, artwork/media download, or write to scanned media is supported.
The fixed hostname policy is not a DNS-rebinding firewall. Source permission,
terms, real response compatibility and production DNS/egress controls have not
been validated; no live request was made by this packaging work.

CA certificates and HTTP libraries are pinned rather than auto-updated. Review
upstream security/CA changes and rebuild the package when pins are changed.
To prepare developer/CI tests, use Python 3.11+ and install the exact closure:

```sh
python -m pip --isolated install --require-hashes --only-binary=:all: --no-deps -r organizer/worker/requirements-live.txt
```

Check scripts verify the installed versions and lock/requirements agreement;
they never install dependencies implicitly. CI installs prerequisites before
running offline tests. Packaging itself uses no developer Python or pip.

## Actual portable Windows verification

The Windows job runs only after the complete Linux/Windows offline checks pass.
It validates the archive before extraction into a Chinese-and-spaces path, then
checks every payload hash. The real extracted executable runs from an unrelated
working directory with SDK/system Python/checkout paths unavailable. Existing
`--smoke-test-package` opens, renders and closes a native Win32 Avalonia HWND,
uses the bundled Python through the C# client, matches fictional `DEMO-001`,
and creates a dry-run preview from isolated generated media and SQLite data.
Its report and PNG are retained; all synthetic source hashes remain unchanged.

A separate bundled Python probe verifies all seven dependency versions and
module paths, the CA bundle, disabled configuration, JavBus runtime wiring,
and an authored HTTPX `MockTransport` response. Socket/DNS connection guards
and intentionally invalid ambient proxy/certificate settings prove the source
probe stays offline and ignores environment configuration. No source permission
or live compatibility is established by these checks.

`--smoke-test-startup --startup-smoke-report <new temp JSON>` additionally tests
the real default portable paths and native startup without a scan or lookup.
The script then creates test-owned SQLite records, disabled settings and data
sentinels, changes a program file to simulate an older build, overwrites from
the verified ZIP, checks all data hashes, checks program-file replacement and
starts the upgraded app. It never supplies external configuration or real media.

Three failure probes deny write access to the owned data folder, replace it
with a junction to another owned test directory, then temporarily remove the marker. Each must show and close an actual native error window,
return nonzero with the expected friendly error code, leave data hashes unchanged
and create no LocalAppData fallback. ACL, junction and marker restoration use `finally`.
No installation path, known app uninstall registration, or Start-menu/desktop
shortcut may appear or change. Shortcut contents are only hashed in memory;
only synthetic reports are uploaded. No claim of all-registry-write tracing is
made. Native smoke is integration coverage, not full manual UX validation.

## Optional legacy installer

The old installer flow is retained only for explicit manual compatibility use:

```powershell
pwsh -NoProfile -File organizer/scripts/package-windows.ps1 -PackageKind installer -BuildLabel local
pwsh -NoProfile -File organizer/scripts/test-windows-package.ps1 `
  -Installer C:\absolute\path\to\MDCxOrganizer-0.1.0-local-win-x64-setup.exe -AllowNonCi
```

Only that branch requires the preinstalled official Inno Setup 6 compiler and
uses its version probe. It packages `README-INSTALLER.txt` as the user README,
omits `portable.json`, keeps per-user LocalAppData storage, and uses the existing
data-preserving uninstall test. The default CI does not build or publish an
installer and does not claim the optional installer was natively retested in
the portable job. No package creates a Release, tag, auto-update or deployment.

## References verified 2026-10-14

- HTTP origins/release hashes: `releasePage` and `metadataApi` in `python-dependencies.json`
- HTTPX mocks: https://www.python-httpx.org/advanced/transports/#mock-transports
- Python release: https://www.python.org/downloads/release/python-31316/
- Embedded Python: https://docs.python.org/3.13/using/windows.html#the-embeddable-package
- PowerShell telemetry: https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_telemetry?view=powershell-7.5
- Artifact direct-file mode: https://github.com/actions/upload-artifact/tree/v7.0.2#uploading-without-archiving
- .NET Windows support: https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md

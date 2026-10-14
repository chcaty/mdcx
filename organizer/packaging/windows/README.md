# Windows x64 private preview packaging

Build from the feature branch on GitHub's `windows-2022` runner with .NET SDK
10.0.402 and its preinstalled **Inno Setup 6** compiler. The build does not
install Inno Setup, use a third-party bootstrapper, publish a GitHub Release,
create a tag, or sign the executable.

```powershell
pwsh -NoProfile -File organizer/scripts/package-windows.ps1 -BuildLabel "ci-123-1"
pwsh -NoProfile -File organizer/scripts/test-windows-package.ps1 `
  -Installer organizer/artifacts/windows/MDCxOrganizer-0.1.0-ci-123-1-win-x64-setup.exe
```

The second command intentionally requires GitHub Actions by default. For a
manual test, use a **disposable Windows user account** and explicitly add
`-AllowNonCi`. It refuses existing app registration, a default installation,
an existing index, or a running app. It never accepts a media-source path.

## Deliverables

- `MDCxOrganizer-0.1.0-<build>-win-x64-setup.exe`: unsigned, per-user installer
- `SHA256SUMS.txt`: installer SHA-256
- `README-WINDOWS.txt`: user instructions, platform limitations and licensing
- `package-manifest.json`: source commit, SDK/compiler/Python version, payload hashes
- Separate `windows-test` reports: native smoke JSON and PNG, installation and
  uninstallation logs, and `install-test.json`

A unique hidden `.staging-*` build directory is retained for debugging. Upload
only the deliverables above; it is not a portable artifact. Publishing is a
folder-based self-contained `win-x64` app, not a single-file or trimmed build.
All target-RID dependency restores use `--locked-mode` and the repository's
separate Windows lockfiles. No installed SDK, Python or pip is needed by users.

## Runtime provenance and isolation

`python-runtime.json` pins Python **3.13.16** from the official Python download
host and the SHA-256 printed on its release page. The download is checked
**before extraction or execution**. The original Python license and native
components are retained. `python313._pth` lists only `python313.zip`, `.`, and
`../worker`; `import site` stays disabled. The worker uses the standard library
only. Its source, package modules, and complete fictional fixture corpus are
copied explicitly, without test caches. The app resolves these bundled paths
independently of its current directory. No runtime downloader is shipped.

The embedded distribution does not supply the Windows Universal C Runtime;
Windows 10/11 provide it as an OS component. The installer checks `ucrtbase.dll`
and reports missing Windows updates instead of downloading or installing a
system component. Existing Python `vcruntime` DLLs remain inside its bundle.

`collect-licenses.ps1` copies package manifests and all package-provided license
and third-party notice files. When a NuGet package declares only MIT or
Apache-2.0, it includes the full license plus that exact package's copyright
metadata. Unknown missing-license cases fail the build. The .NET runtime
license and third-party notices are mandatory, and Python, upstream project
README restrictions, GPLv3 text, and project NOTICE are retained. The original
license ambiguity is not resolved by this packaging work.

## Installer boundaries

- `PrivilegesRequired=lowest`; native x64 only; no machine-wide registration
- `%LOCALAPPDATA%\Programs\MDCxOrganizer` binaries are separate from the private
  `%LOCALAPPDATA%\MDCxOrganizer` index
- No automatic launch, restart, update task, association or PATH modification
- No recursive deletion rules; uninstall uses only Inno's installed-file log
- Data paths are rejected as installation targets, including ancestors and descendants
- The unsigned installer may produce Windows publisher/reputation warnings

## Actual installed-app verification

The Windows test checks every installed payload hash. It launches the actual
installed `Organizer.Desktop.exe`, from an unrelated temporary working directory
with SDK/Python paths unavailable, using `--smoke-test-package` and a fresh
`--package-smoke-report` path. The app must open/render/close a native Win32
Avalonia HWND, use the bundled interpreter through the C# worker client,
match `DEMO-001`, and produce a dry-run preview using its own new temp media
and SQLite index. The native JSON report and PNG are copied to test artifacts.
This is a native startup/fixture integration check, not broad manual UX coverage.

The test separately creates one tiny synthetic SQLite database and unique
sentinel under the real user-data directory after verifying no index exists.
After silent uninstall, it verifies both hashes unchanged and verifies an
unrecognized file in the install folder remains. It then removes only the
exact synthetic data files it created, leaving failure temp directories for
inspection. It does not scan or write a real media folder.

## References verified 2026-10-14

- Python 3.13.16 release/hash: https://www.python.org/downloads/release/python-31316/
- Embedded Python limitations: https://docs.python.org/3.13/using/windows.html#the-embeddable-package
- Inno privilege modes: https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm
- Inno install arguments: https://jrsoftware.org/ishelp/topic_setupcmdline.htm
- Inno uninstall arguments: https://jrsoftware.org/ishelp/topic_uninstcmdline.htm
- Runner catalog (then InnoSetup 6.7.1): https://github.com/actions/runner-images/blob/main/images/windows/Windows2022-Readme.md
- .NET supported Windows versions: https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md

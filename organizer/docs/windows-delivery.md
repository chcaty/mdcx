# Windows portable build and verification pipeline

The independent `.github/workflows/organizer-windows.yml` validates and packages the new `organizer/` application. Its default deliverable is a portable Windows x64 ZIP, not an installer. It does not publish a GitHub Release, tag, package-registry entry or deployment, and it does not change the legacy application's workflows.

## Triggers and trust

- Pushes to `feat/media-organizer-workbench-20261014` affecting `organizer/**` or this workflow run automatically.
- Pull requests into that branch, `master` or `develop` run on opened, synchronize, reopened and ready-for-review events for the same paths. No PR is created by this workflow.
- Manual dispatch is declared, but GitHub exposes it only when the workflow is on the default branch. This feature-branch delivery does not change `master`; push is the active trigger.
- Checkout credentials are not persisted. The token has `contents: read`, no secrets/signing credentials are used, and actions are pinned to full commit SHAs. New commits cancel superseded runs of this workflow/ref.
- PowerShell, .NET SDK and Avalonia telemetry opt-outs are set in the job environment before their processes start. Avalonia's separate environment-variable requirement also has negative/positive preflight tests.
- Official package restoration/build-time downloads use the network. Metadata tests use temporary fictional media and strict fixtures/injected HTTP, never source-site crawling. The application sends a source query only after the user explicitly saves/enables that source and starts a match.

## Download and run

Download **MDCxOrganizer-…-win-x64-portable.zip** from the successful **Organizer Windows CI** run. The pinned upload-artifact v7 action uses `archive: false`, so this is the actual portable ZIP, not another ZIP wrapped around it. Extract the complete archive into a writable folder and run `Organizer.Desktop.exe`. The archive root contains the executable, private .NET/Python runtimes, `worker/`, `portable.json`, `package-manifest.json`, readme and licenses. No separate .NET/Python installation, pip download, administrator access, registry registration or shortcut is required.

Keep the portable marker and all program files together. The application uses `data/index.db` and `data/metadata-source.json` beside the executable. A missing/invalid marker on a manifest-identified portable package, linked/unsafe data files, or an unwritable data directory produces a startup error; it does not silently load installed-app data.

The app executable is **unsigned**. Verify the downloaded archive and SHA-256 from the same run before deciding to launch it. Windows may show an unknown-publisher/security warning; no certificate or reputation is claimed, and security warnings are not bypassed.

## Upgrade and remove

1. Close the running app and back up the entire `data/` folder.
2. Extract the new program files over the old program directory while retaining `data/`, or extract into a new directory and copy your backed-up `data/` into it.
3. Start the new executable. The archive deliberately contains no `data/` subtree, so unpacking its program files cannot replace the index/settings.

The application has no updater or automatic migration from the older installed edition. Installed `%LOCALAPPDATA%\MDCxOrganizer` data stays untouched. Removing a portable folder is a user-managed deletion and also removes any `data/` inside it; back up what you want to retain. No uninstaller or media cleanup is run.

## What successful CI proves

1. Linux and Windows execute the full Core/infrastructure, desktop MVVM, actual Headless routed-input and Python suites with Release analyzers, locked restores and lint.
2. Windows publishes a self-contained x64 .NET application using a separate locked RID graph.
3. The package includes a pinned official embedded CPython runtime, standard library and seven exact hash-locked HTTP dependency wheels, including the CA bundle and licenses. Its `_pth` keeps imports isolated; no runtime pip/download or ambient PYTHONPATH is used.
4. The portable archive has the expected root layout and no user `data/` content. CI extracts it into a temporary path containing spaces and Chinese characters.
5. The extracted app opens a real Win32 Avalonia window from an unrelated working directory, uses its bundled worker/interpreter and matches neutral fixtures. Its native smoke records the HWND, screenshot, isolated test index and unchanged synthetic source bytes.
6. Separate startup probes verify actual adjacent portable storage, clear failure for unwritable data and missing marker, and no fallback to an installed data location. Upgrade/re-extraction checks preserve data and source settings.
7. A bundled HTTP probe checks dependency paths, disabled defaults and HTTPX MockTransport → JavBus parser with socket/DNS guards and hostile ambient proxy/CA variables. It does not verify live-source access.

Read the reports for the exact run; a workflow file or Linux run alone is not proof of native Windows acceptance. New portable coverage supersedes only the corresponding packaging limitations in the historical `validation.md` record.

## Source and product limits

The old published e8c540e installer is fixture-only and does not contain this correction. The updated source has an explicit demo/real-source settings entry, a persistent worker and configured HTTP transport. Demo records are synthetic; the configured provider's current live-page structure, availability and catalog coverage remain unverified. No online lookup was performed for BF-658, and no successful match for it is claimed. Only identifier/title/optional year are supported by this provider; authentication, posters and all-source parity are not implemented.

Media organization remains **preview-only**: no move/rename/delete executor, NFO/image writing, crash-recovery journal or auto-update exists. The Windows Server 2022 x64 runner does not establish behavior on every Windows 10/11 device, Windows ARM64, macOS, or full manual desktop use. Source/index paths may be private; protect your portable `data/` folder.

## Reproduce

Set `POWERSHELL_TELEMETRY_OPTOUT=1` in the parent environment **before starting PowerShell**, as required by [PowerShell's official telemetry documentation](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_telemetry). On a disposable Windows x64 development machine with .NET SDK 10.0.402 and Python:

```powershell
python -m pip --isolated install --require-hashes --only-binary=:all: --no-deps -r organizer/worker/requirements-live.txt
pwsh -NoProfile -File ./organizer/scripts/check.ps1
pwsh -NoProfile -File ./organizer/scripts/package-windows.ps1 -PackageKind portable -BuildLabel local
pwsh -NoProfile -File ./organizer/scripts/test-windows-portable.ps1 -Archive C:\absolute\path\to\portable.zip -AllowNonCi
```

Portable packaging does not require Inno Setup. The older installer remains an optional developer-only `-PackageKind installer` path requiring the official preinstalled Inno Setup 6 compiler; it is no longer the default CI deliverable. Outputs/logs are in ignored `organizer/artifacts/`. CI retains portable downloads for 30 days and diagnostics for 14 days, not permanently as a Release.

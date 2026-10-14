# Windows build and installer pipeline

The separate `.github/workflows/organizer-windows.yml` workflow validates and packages only the new `organizer/` application. It never publishes a GitHub Release, tag, package registry entry, or deployment, and it does not modify the legacy application's workflows.

## Triggers and trust

- Pushes to `feat/media-organizer-workbench-20261014` affecting `organizer/**` or this workflow run automatically.
- Pull requests into that branch, `master`, or `develop` run on opened, synchronize, reopened, and ready-for-review events for the same paths. No PR is created by the workflow.
- A `workflow_dispatch` entry is included. GitHub only exposes manual dispatch when the workflow is present on the default branch; this feature-only delivery does not change `master` to enable that UI. Push is the working initial trigger.
- Checkout credentials are not persisted. The token has `contents: read`; there are no secrets or signing credentials. Third-party actions are pinned to full commit SHAs. New commits cancel superseded runs of this workflow/ref.
- Tests use generated temporary media and strict offline parser fixtures. Package restoration and official build-time downloads use the network; the scraper tests and installed application do not crawl providers.

## What a successful run proves

1. Linux and Windows execute the complete Core/infrastructure, MVVM, Headless routed-input, and Python regression suites with Release compiler/analyzers and isolated Ruff lint.
2. The actual environment-variable requirement for Avalonia's documented telemetry opt-out is tested negatively and positively, without invoking its statistics task in the negative test.
3. Windows publishes a self-contained x64 .NET application. Portable and Windows RID dependency graphs have separate checked-in NuGet locks; both restore in locked mode.
4. The package includes the pinned official CPython embedded runtime, standard library, worker modules, neutral HTML/JSON fixtures, and license notices. No separate .NET/Python installation, pip, login, or provider credentials are needed at runtime.
5. The per-user installer is installed in an isolated test location. The installed `.exe`, launched from a different working directory, starts a real Win32 Avalonia window and matches a neutral fixture through the bundled Python process. The native smoke records its interpreter, worker, window handle, rendered PNG, isolated index, safe preview and unchanged source bytes.
6. Uninstall is tested without deleting the user's application data or media. The installer does not silently launch the app after installation.

A workflow definition alone is not proof of a passing build. See the specific Actions run and its diagnostics for observed results. The previous Linux-only record in `validation.md` remains historical; Windows coverage comes from an actual successful run of this new pipeline.

## Download and install

Open the successful **Organizer Windows CI** run and download its **MDCxOrganizer-…-win-x64-setup.exe** artifact. The pinned official upload-artifact v7 action uses `archive: false`, so the installer is available directly as an `.exe`, without a ZIP wrapper. The separate **MDCx-Organizer-Windows-x64-information-…** ZIP contains its SHA-256, package manifest and readme. A directly shared installer attachment contains the same `.exe` bytes.

The installer is **unsigned**. No signing certificate or reputation is claimed; Windows may show an unknown-publisher or security warning. Verify the download source and supplied SHA-256 before deciding whether to install. The assistant does not bypass those warnings. Installation is per-user and does not require administrator access.

The installed application remains **preview-only and fixture-only**. Matching results are synthetic demonstrations, not real film metadata. There is no real move/rename/delete executor, live scraper, auto-update, or crash-recovery journal. Uninstall retains `%LOCALAPPDATA%\MDCxOrganizer` and never removes media. Full manual desktop interaction, Windows junction/reparse behavior (existing link checks skip Windows), other Windows editions/hardware, Windows ARM64, macOS, and live-source behavior are not established by the Windows Server 2022 x64 runner.

## Reproduce

On a Windows x64 development machine with .NET SDK 10.0.402 and Inno Setup 6 installed from their official sources:

```powershell
pwsh -NoProfile -File ./organizer/scripts/check.ps1
pwsh -NoProfile -File ./organizer/scripts/package-windows.ps1 -BuildLabel local
# Only in a disposable Windows user account:
pwsh -NoProfile -File ./organizer/scripts/test-windows-package.ps1 -Installer C:\absolute\path\to\Setup.exe -AllowNonCi
```

The scripts set both .NET SDK and Avalonia telemetry opt-outs. The packaging script verifies the embedded Python archive against its pinned SHA-256 before extraction and uses the preinstalled Inno compiler rather than bootstrapping an installer. Outputs and diagnostics are under the ignored `organizer/artifacts/` directory. CI retains installer artifacts for 30 days and diagnostics for 14 days; they are not a permanent Release distribution.

MDCx Organizer Preview - Windows x64
===================================

This unsigned private preview installer includes the .NET runtime, Avalonia,
a private Python runtime, fixture metadata, and pinned HTTP libraries for the
independent optional JavBus catalog source. No .NET or
Python installation, pip package, PATH edit, or runtime download is required.

Target: currently supported Windows 11 on native x64. The installer minimum
is Windows build 19041; that minimum is not a support claim for obsolete
Windows 10 releases. Windows 7, 32-bit Windows, and Windows on ARM are not targets.
The native CI test runs on Windows Server 2022, not every Windows 10/11 device.

INSTALL
1. Verify the installer SHA-256 against SHA256SUMS.txt from the same CI build.
2. Run the .exe installer as your ordinary user. It does not request admin.
3. Start "MDCx Organizer Preview" from the Start menu.

The installer is unsigned, so Windows may display an unknown-publisher or
SmartScreen warning. Verify the source and hash before deciding whether to
run it. Do not disable Windows security controls. No automatic launch follows
installation, including silent installation. No auto-update is installed.

SAFETY AND SCOPE
This is a preview-only media workbench. Scanning is read-only. Metadata comes
from authored fictional fixtures by default and must not be mistaken for
verified media metadata. Source networking defaults off. Source settings can
explicitly enable the optional JavBus catalog lookup; its metadata requires
review. This integration has only been tested with fictional local responses,
not the live source. Check applicable source permissions/terms before use.
TLS verification is required; no login, cookies, redirects, challenge bypass,
artwork download or arbitrary source URL is supported. System proxy settings
are ignored; only a saved explicit credential-free HTTP(S) proxy may be used. The app does not move, rename, overwrite,
delete or publish media. No real credentials or downloaded media are included.

PROGRAMS AND DATA
Default program folder: %LOCALAPPDATA%\Programs\MDCxOrganizer
Private index:          %LOCALAPPDATA%\MDCxOrganizer\index.db
Source settings:        %LOCALAPPDATA%\MDCxOrganizer\metadata-source.json

The installed source-settings.example.json is disabled and is only an example.
It does not activate a source or overwrite your saved settings.

Uninstall with Windows Settings > Apps > MDCx Organizer Preview, or run the
uninstaller in the program folder. Uninstall removes installed application
files and its Start-menu shortcut. It preserves the local data/index directory,
including saved source settings,
all scanned media, and unrecognized files. Indexes may contain private paths.
Back up anything important yourself; this application is not a media backup.

LICENSES AND SOURCE
The bundled licenses directory retains the repository GPLv3 text, original
README restrictions, project notices, Python licenses, and dependency licenses
and metadata. The original README additionally restricts commercial use and
promotion. That licensing ambiguity remains unresolved. This private preview
does not grant permission for commercial use or public redistribution.

Build and Python provenance are in package-manifest.json and
licenses/python-runtime.json. Exact HTTP package versions, official PyPI URLs,
wheel hashes, original package metadata and licenses are under licenses/python.
The pinned CA bundle and libraries change only when the installer is rebuilt. Source is on the feature branch that produced
the matching CI artifact, at the commit recorded in package-manifest.json.

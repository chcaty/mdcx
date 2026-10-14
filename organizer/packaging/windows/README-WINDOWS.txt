MDCx Organizer Preview - Windows x64
===================================

This unsigned private preview installer includes the .NET runtime, Avalonia,
a private Python runtime, and the fixture-only metadata worker. No .NET or
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
from authored fictional fixtures, never live scraping, so it must not be
mistaken for verified media metadata. The app does not move, rename, overwrite,
delete or publish media. No real credentials or downloaded media are included.

PROGRAMS AND DATA
Default program folder: %LOCALAPPDATA%\Programs\MDCxOrganizer
Private index:          %LOCALAPPDATA%\MDCxOrganizer\index.db

Uninstall with Windows Settings > Apps > MDCx Organizer Preview, or run the
uninstaller in the program folder. Uninstall removes installed application
files and its Start-menu shortcut. It preserves the local data/index directory,
all scanned media, and unrecognized files. Indexes may contain private paths.
Back up anything important yourself; this application is not a media backup.

LICENSES AND SOURCE
The bundled licenses directory retains the repository GPLv3 text, original
README restrictions, project notices, Python licenses, and dependency licenses
and metadata. The original README additionally restricts commercial use and
promotion. That licensing ambiguity remains unresolved. This private preview
does not grant permission for commercial use or public redistribution.

Build and Python provenance are in package-manifest.json and
licenses/python-runtime.json. Source is on the feature branch that produced
the matching CI artifact, at the commit recorded in package-manifest.json.

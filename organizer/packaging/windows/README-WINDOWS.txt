MDCx Organizer Preview - Windows x64 PORTABLE
=============================================

解压即用：将整个 ZIP 解压到可写的普通文件夹，运行 Organizer.Desktop.exe。
不需要安装 .NET 或 Python；不会创建注册表安装记录或快捷方式。
设置和索引保存在程序旁的 data 文件夹。更新前关闭程序并备份 data，
将新版程序文件覆盖到原文件夹，保留 data。不要在 ZIP 内直接运行程序。

This unsigned private preview ZIP includes the .NET runtime, Avalonia, a
private Python runtime, fixture metadata, and pinned HTTP libraries for the
independent optional JavBus catalog source. No installer, .NET/Python setup,
pip, administrator access, PATH edit or runtime download is required.

Target: currently supported Windows 11 on native x64. Windows 7, 32-bit Windows
and Windows on ARM are not targets. The native CI test runs on Windows Server
2022, not every Windows 10/11 device. Keep Windows OS components up to date.

EXTRACT AND RUN
1. Verify the ZIP SHA-256 against the matching successful CI run summary.
   Local builds also produce SHA256SUMS.txt beside the ZIP.
2. Extract the ENTIRE ZIP into a writable folder such as D:\Apps\MDCxOrganizer.
   The executable, python, worker and portable.json must remain together.
3. Double-click Organizer.Desktop.exe. Do not run it inside the ZIP viewer.

There is no setup wizard, Start-menu shortcut, uninstall registration, file
association or auto-update. The application is unsigned; Windows may display
an unknown-publisher or SmartScreen warning. Verify the source and hash before
deciding whether to run it. Do not disable Windows security controls.

PROGRAM-ADJACENT DATA
Program:         <extracted folder>\Organizer.Desktop.exe
Portable marker: <extracted folder>\portable.json
Private index:   <extracted folder>\data\index.db
Source settings: <extracted folder>\data\metadata-source.json

The data folder is created when first needed and is not included in the ZIP.
No fallback to AppData occurs if the portable data folder cannot be written.
Move the whole extracted folder to a writable location after closing the app;
do not use Program Files, a read-only disk, or grant administrator rights to
work around this. Keep portable.json unchanged. A missing or invalid portable
marker produces an error for this portable build rather than opening another
installation's data. There is no automatic migration from an older installer.

source-settings.example.json is a disabled example, not your saved settings.
It does not activate a source or overwrite your settings. Indexes may contain
private paths and reviewed metadata. Back up the whole data folder with the app
closed. This application is not a media backup. Avoid sharing a live data folder
between concurrent app processes or cloud-sync clients.

UPDATE OR REMOVE
1. Close all running copies of the application.
2. Back up the complete data folder to a separate safe location.
3. Extract the new verified portable ZIP into the same program folder and allow
   replacement of program files. Keep the existing data folder and its contents.
   New archives do not contain a data folder. Do not delete the old folder first.
4. Run Organizer.Desktop.exe again. Saved settings and index remain in data.

The overwrite procedure is tested with synthetic database, settings and cache
files. It is not a guarantee of compatibility with every future database format.
To move the application, close it and move the whole folder, including data.
There is no uninstaller. Deleting the whole folder also deletes its saved data;
keep any needed backup first. Scanned source media is separate and untouched.

SAFETY AND SCOPE
This is a preview-only media workbench. Scanning is read-only. Metadata comes
from authored fictional fixtures by default and must not be mistaken for
verified media metadata. Source networking defaults off. Source settings can
explicitly enable the optional JavBus catalog lookup; its metadata requires
review. This integration has only been tested with fictional local responses,
not the live source. Check applicable source permissions/terms before use.
TLS verification is required; no login, cookies, redirects, challenge bypass,
artwork download or arbitrary source URL is supported. System proxy settings
are ignored; only a saved explicit credential-free HTTP(S) proxy may be used.
The app does not move, rename, overwrite, delete or publish media. No real
credentials, user settings, database or downloaded media are included.

LICENSES AND SOURCE
The licenses directory retains the repository GPLv3 text, original README
restrictions, project notices, Python licenses and dependency licenses/metadata.
The original README additionally restricts commercial use and promotion. That
licensing ambiguity remains unresolved. This private preview does not grant
permission for commercial use or public redistribution.

Build and Python provenance are in package-manifest.json and
licenses/python-runtime.json. Exact HTTP package versions, official PyPI URLs,
wheel hashes, metadata and licenses are under licenses/python. The pinned CA
bundle and libraries change only when the package is rebuilt. Source is on the
feature branch that produced the matching CI artifact, at the commit recorded
in package-manifest.json.

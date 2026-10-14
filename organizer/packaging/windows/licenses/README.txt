License expressions in NuGet manifests are expanded only when the restored
package does not itself contain a license file. MIT text is paired with the
package's exact copyright metadata. Apache-2.0 full text is copied verbatim
from https://www.apache.org/licenses/LICENSE-2.0.txt (retrieved 2026-10-14).
The original .nuspec and any included notices are retained for every package.

Native SQLite is in the public domain (https://www.sqlite.org/copyright.html).
SQLitePCLRaw bindings/wrappers use Apache-2.0 per their package metadata.
The packaged Python distribution retains its original full LICENSE.txt;
SkiaSharp, HarfBuzzSharp and ANGLE bundled notices are copied from their exact
restored packages. The .NET runtime-pack license and third-party notices are
also included. The license inventory includes build-time/cross-platform
packages as well as runtime packages to avoid silently dropping notices.

These are dependency notices, not a resolution of the root project's GPLv3
and additional README restrictions. Preserve the application's own notices.

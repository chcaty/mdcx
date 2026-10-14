using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Organizer.Core;

/// <summary>Read-only scanner. One asset per video; same identifier is never a duplicate verdict.</summary>
public sealed partial class MediaScanner
{
    private static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".ts", ".m2ts", ".webm", ".iso" };
    private static readonly HashSet<string> Sidecar = new(StringComparer.OrdinalIgnoreCase) { ".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt", ".nfo", ".jpg", ".jpeg", ".png" };
    [GeneratedRegex(@"(?<![A-Za-z0-9])([A-Za-z]{2,12})[-_ ]?(\d{2,8})(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    public static string? ExtractIdentifier(string stem)
    {
        var values = IdentifierPattern().Matches(stem.Normalize(NormalizationForm.FormKC))
            .Select(m => $"{m.Groups[1].Value.ToUpperInvariant()}-{m.Groups[2].Value}").Distinct(StringComparer.Ordinal).ToArray();
        return values.Length == 1 ? values[0] : null;
    }

    public Task<ScanResult> ScanAsync(string root, CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(root, cancellationToken), cancellationToken);

    private static ScanResult Scan(string root, CancellationToken token)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        if (PathSafety.HasLinkComponent(root)) throw new IOException("The source root must not contain a symbolic link or reparse point.");
        var assets = new List<MediaAsset>();
        var complete = true;
        var warnings = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(directory).Order(StringComparer.Ordinal).ToArray(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { complete = false; warnings.Add($"Unreadable directory: {directory}: {e.Message}"); continue; }
            var files = new List<string>();
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (PathSafety.HasLinkComponent(entry)) { warnings.Add($"Skipped link: {entry}"); continue; }
                    if (Directory.Exists(entry)) pending.Push(entry);
                    else if (File.Exists(entry)) files.Add(entry);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { complete = false; warnings.Add($"Unreadable entry: {entry}: {e.Message}"); }
            }
            var videos = files.Where(p => Video.Contains(Path.GetExtension(p))).ToArray();
            var sidecars = files.Where(p => Sidecar.Contains(Path.GetExtension(p))).ToArray();
            var owners = sidecars.ToDictionary(s => s, s => videos.Where(v => MatchesStem(Path.GetFileNameWithoutExtension(v), Path.GetFileName(s))).ToArray());
            foreach (var video in videos)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(video);
                    var stem = Path.GetFileNameWithoutExtension(video);
                    var id = ExtractIdentifier(stem);
                    var issues = new List<string>();
                    if (id is null) issues.Add("Identifier missing or ambiguous; manual review required.");
                    var attached = new List<SidecarFile>();
                    foreach (var side in owners.Where(kv => kv.Value.Contains(video, StringComparer.Ordinal)))
                    {
                        if (side.Value.Length > 1) issues.Add($"Ambiguous sidecar not attached: {Path.GetFileName(side.Key)}");
                        else
                        {
                            var sideInfo = new FileInfo(side.Key);
                            attached.Add(new SidecarFile(side.Key, Path.GetFileName(side.Key)[stem.Length..])
                            { Fingerprint = $"{sideInfo.Length}:{sideInfo.LastWriteTimeUtc.Ticks}" });
                        }
                    }
                    var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(video))).ToLowerInvariant();
                    assets.Add(new MediaAsset(key, video, Path.GetRelativePath(root, video), stem, id, info.Length, attached, issues)
                    { Fingerprint = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" });
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { complete = false; warnings.Add($"Changed/unreadable file: {video}: {e.Message}"); }
            }
            foreach (var orphan in owners.Where(p => p.Value.Length == 0)) warnings.Add($"Unattached sidecar: {orphan.Key}");
        }
        token.ThrowIfCancellationRequested();
        // Stable row order regardless of directory traversal or OS enumeration order.
        return new ScanResult(root, assets.OrderBy(a => a.RelativePath, StringComparer.Ordinal).ToArray(), warnings) { IsComplete = complete };
    }

    private static bool MatchesStem(string stem, string sidecarName)
    {
        // Prefer exact-prefix ownership. Never attach common folder posters automatically.
        return sidecarName.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase);
    }
}

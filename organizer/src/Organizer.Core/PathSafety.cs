using System.Text;

namespace Organizer.Core;

public static class PathSafety
{
    public static string CollisionKey(string path) => Path.GetFullPath(path).Normalize(NormalizationForm.FormC).ToUpperInvariant();

    public static bool HasLinkComponent(string path)
    {
        var full = Path.GetFullPath(path);
        FileSystemInfo? entry = File.Exists(full) ? new FileInfo(full) : new DirectoryInfo(full);
        while (entry is not null)
        {
            // LinkTarget also detects dangling links, where Exists is false.
            if (entry.LinkTarget is not null || (entry.Exists && entry.Attributes.HasFlag(FileAttributes.ReparsePoint))) return true;
            entry = entry switch { FileInfo f => f.Directory, DirectoryInfo d => d.Parent, _ => null };
        }
        return false;
    }

    public static IEnumerable<string> CheckPortableAncestors(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        foreach (var part in parts.SkipLast(1))
        {
            if (!Directory.Exists(current)) yield break;
            var matches = Directory.EnumerateFileSystemEntries(current).Where(p =>
                string.Equals(Path.GetFileName(p).Normalize(NormalizationForm.FormC), part.Normalize(NormalizationForm.FormC), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) yield break;
            if (matches.Length > 1) { yield return $"Case/Unicode ambiguous parent: {current}/{part}"; yield break; }
            current = matches[0];
            if (HasLinkComponent(current)) { yield return $"Linked parent: {current}"; yield break; }
            if (!Directory.Exists(current)) { yield return $"Parent is not a directory: {current}"; yield break; }
        }
    }

    public static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    // Case and NFC equivalence are treated as collisions on every OS, for portable plans.
    public static bool PortableEntryExists(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        IEnumerable<string> parents = [root];
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = new List<string>();
            foreach (var parent in parents)
            {
                if (!Directory.Exists(parent)) continue;
                next.AddRange(Directory.EnumerateFileSystemEntries(parent).Where(p =>
                    string.Equals(Path.GetFileName(p).Normalize(NormalizationForm.FormC), part.Normalize(NormalizationForm.FormC), StringComparison.OrdinalIgnoreCase)));
            }
            if (next.Count == 0) return false;
            parents = next;
        }
        return parents.Any();
    }
}

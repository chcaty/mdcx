using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Organizer.Core;

/// <summary>Produces immutable previews only. No execute/move/delete API exists in this slice.</summary>
public sealed partial class PathPlanner
{
    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedName();
    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1F\x7F]")]
    private static partial Regex InvalidName();

    public OrganizationPlan CreatePlan(string targetRoot, IReadOnlyList<(MediaAsset Asset, ReviewedMetadata Metadata)> selections)
    {
        var operations = new List<PlanOperation>();
        var issues = new List<PlanIssue>();
        try
        {
            if (!Path.IsPathFullyQualified(targetRoot)) throw new ArgumentException("Target root must be an absolute path.");
            targetRoot = Path.GetFullPath(targetRoot);
            if (PathSafety.HasLinkComponent(targetRoot)) issues.Add(new("unsafe-root", "Target root contains a link or reparse point."));
            if (File.Exists(targetRoot)) issues.Add(new("unsafe-root", "Target root is a file."));
            foreach (var selection in selections.OrderBy(s => s.Asset.SourcePath, StringComparer.Ordinal))
            {
                var (asset, metadata) = selection;
                if (asset.Warnings.Any(w => w.StartsWith("Ambiguous sidecar", StringComparison.Ordinal))) issues.Add(new("scan-warning", $"Review scan warnings before organizing: {asset.RelativePath}"));
                if (string.IsNullOrWhiteSpace(metadata.Identifier) || string.IsNullOrWhiteSpace(metadata.Title))
                { issues.Add(new("metadata-required", $"Identifier and title are required: {asset.RelativePath}")); continue; }
                if (metadata.Year is < 1888 or > 2200) { issues.Add(new("invalid-year", $"Invalid year: {asset.RelativePath}")); continue; }
                var name = CleanName(metadata.Identifier) + " - " + CleanName(metadata.Title);
                if (metadata.Year is not null) name += $" ({metadata.Year})";
                var folder = Path.Combine(targetRoot, name);
                Add(asset.SourcePath, Path.Combine(folder, name + Path.GetExtension(asset.SourcePath).ToLowerInvariant()), "video", asset.Fingerprint);
                foreach (var side in asset.Sidecars)
                {
                    if (!side.Suffix.StartsWith('.') || InvalidName().IsMatch(side.Suffix) || side.Suffix.EndsWith('.') || side.Suffix.EndsWith(' '))
                    { issues.Add(new("unsafe-sidecar", $"Invalid sidecar suffix: {side.Suffix}")); continue; }
                    Add(side.Path, Path.Combine(folder, name + side.Suffix), "sidecar", side.Fingerprint);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { issues.Add(new("unsafe-path", e.Message)); }
        foreach (var collision in operations.GroupBy(o => PathSafety.CollisionKey(o.Target)).Where(g => g.Count() > 1))
            issues.Add(new("collision", $"Multiple files would use: {collision.First().Target}"));
        foreach (var repeated in operations.GroupBy(o => PathSafety.CollisionKey(o.Source)).Where(g => g.Count() > 1))
            issues.Add(new("repeated-source", $"A source occurs more than once: {repeated.First().Source}"));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { targetRoot, operations, issues });
        return new OrganizationPlan(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), operations, issues);

        void Add(string source, string target, string kind, string? fingerprint)
        {
            if (!PathSafety.IsWithin(targetRoot, target)) { issues.Add(new("path-escape", target)); return; }
            foreach (var issue in PathSafety.CheckPortableAncestors(target)) issues.Add(new("unsafe-ancestor", issue));
            if (target.Split(Path.DirectorySeparatorChar).Any(component => component.Length > 255 || Encoding.UTF8.GetByteCount(component) > 255))
                issues.Add(new("component-too-long", $"Filename exceeds portable component limit: {target}"));
            if (target.Length > 240) issues.Add(new("path-too-long", $"Portable path limit (240) exceeded: {target}"));
            if (!File.Exists(source)) issues.Add(new("missing-source", source));
            if (PathSafety.HasLinkComponent(source) || PathSafety.HasLinkComponent(target)) issues.Add(new("unsafe-link", $"Link/reparse path: {source} → {target}"));
            if (File.Exists(source) && !string.IsNullOrEmpty(fingerprint))
            {
                var info = new FileInfo(source);
                if (fingerprint != $"{info.Length}:{info.LastWriteTimeUtc.Ticks}") issues.Add(new("stale-source", $"Source changed since scanning: {source}"));
            }
            if (PathSafety.CollisionKey(source) == PathSafety.CollisionKey(target)) issues.Add(new("no-op", $"Source already occupies target: {source}"));
            else if (PathSafety.PortableEntryExists(target)) issues.Add(new("existing-target", $"Target exists (case/Unicode aware): {target}"));
            operations.Add(new PlanOperation(source, target, kind));
        }
    }

    private static string CleanName(string value)
    {
        value = value.Normalize(NormalizationForm.FormC).Trim();
        if (value is "." or ".." || value.Contains('/') || value.Contains('\\')) throw new ArgumentException("Metadata must not contain directory traversal or path separators.");
        value = InvalidName().Replace(value, "_").TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Metadata produces an empty filename.");
        if (ReservedName().IsMatch(value)) value = "_" + value;
        // Rune-aware truncation avoids splitting surrogate pairs.
        value = string.Concat(value.EnumerateRunes().Take(90).Select(r => r.ToString())).TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Metadata produces an empty filename after truncation.");
        return value;
    }
}

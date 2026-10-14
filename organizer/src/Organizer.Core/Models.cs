namespace Organizer.Core;

public sealed record SidecarFile(string Path, string Suffix)
{
    public string Fingerprint { get; init; } = "";
}
public sealed record MediaAsset(string Id, string SourcePath, string RelativePath, string Stem,
    string? Identifier, long Size, IReadOnlyList<SidecarFile> Sidecars, IReadOnlyList<string> Warnings)
{
    // Change detection only. This is intentionally not a content hash or proof of duplication.
    public string Fingerprint { get; init; } = "";
}
public sealed record ScanResult(string Root, IReadOnlyList<MediaAsset> Assets, IReadOnlyList<string> Warnings)
{
    public bool IsComplete { get; init; } = true;
}
public sealed record MetadataCandidate(string Id, string Title, int? Year, string Source, string SourceUrl,
    double Confidence, IReadOnlyDictionary<string, string> Provenance)
{
    public bool RequiresReview { get; init; } = true;
    public IReadOnlyList<string> ReviewReasons { get; init; } = [];
}
public sealed record ReviewedMetadata(string Identifier, string Title, int? Year)
{
    public string Origin { get; init; } = "manual";
    public bool Locked { get; init; } = true;
    public IReadOnlyDictionary<string, string> Provenance { get; init; } = new Dictionary<string, string>();
}
public sealed record PlanOperation(string Source, string Target, string Kind);
public sealed record PlanIssue(string Code, string Message);
public sealed record OrganizationPlan(string Id, IReadOnlyList<PlanOperation> Operations, IReadOnlyList<PlanIssue> Issues)
{
    public bool IsSafe => Issues.Count == 0;
    public bool DryRun => true;
}

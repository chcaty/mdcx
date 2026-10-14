using System.Text.RegularExpressions;
using Organizer.Core;

namespace Organizer.Desktop.ViewModels;

/// <summary>A file snapshot, its independent batch checkbox, and its session-only review draft.</summary>
public sealed class AssetRow : ObservableObject
{
    private ReviewedMetadata? _review;
    private bool _isChecked;
    private bool _hasUnsavedEdits;

    public AssetRow(MediaAsset asset)
    {
        Asset = asset;
        DraftIdentifier = BaselineIdentifier = asset.Identifier ?? "";
    }

    public MediaAsset Asset { get; private set; }
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
    public bool HasUnsavedEdits { get => _hasUnsavedEdits; internal set { if (Set(ref _hasUnsavedEdits, value)) RefreshMetadata(); } }
    public IReadOnlyList<CandidateRow> CachedCandidates { get; internal set; } = [];
    internal CandidateRow? CachedSelection { get; set; }
    internal bool MatchAttempted { get; set; }
    internal bool HasManualEdits { get; set; }
    internal string MatchFailure { get; set; } = "";
    internal string MatchFailureCode { get; set; } = "";
    internal bool SnapshotInvalidated { get; set; }
    internal string QueryOverride { get; set; } = "";
    internal string DraftIdentifier { get; set; } = "";
    internal string DraftTitle { get; set; } = "";
    internal string DraftYear { get; set; } = "";
    internal string BaselineIdentifier { get; set; } = "";
    internal string BaselineTitle { get; set; } = "";
    internal string BaselineYear { get; set; } = "";
    internal ReviewedMetadata? SavedReview { get; set; }

    public string Filename => Path.GetFileName(Asset.SourcePath);
    public string SourcePath => Asset.SourcePath;
    public string RelativePath => Asset.RelativePath;
    public string Identifier => Review?.Identifier ?? CachedSelection?.Candidate.CatalogIdentifier?.Value ?? Asset.Identifier ?? "未识别";
    public string DisplayTitle => !string.IsNullOrWhiteSpace(DraftTitle) ? DraftTitle : Review?.Title ?? Asset.Stem;
    public string FormatText => Path.GetExtension(Asset.SourcePath).TrimStart('.').ToUpperInvariant();
    public string ResolutionText
    {
        get
        {
            var match = Regex.Match(Asset.Stem, @"(?i)(?<![a-z0-9])(2160p|1080p|720p|4k)(?![a-z0-9])");
            return match.Success ? $"{match.Value.ToUpperInvariant()} · 文件名" : "";
        }
    }
    public string SizeText => Asset.Size >= 1_073_741_824 ? $"{Asset.Size / 1_073_741_824d:0.00} GB" : $"{Asset.Size / 1_048_576d:0.0} MB";
    public string SidecarsText => Asset.Sidecars.Count == 0 ? "无伴随文件" : $"伴随文件 × {Asset.Sidecars.Count}";
    public string StatusText => Review is not null ? "已审核" : SnapshotInvalidated || !string.IsNullOrEmpty(MatchFailure) ||
        (MatchAttempted && CachedCandidates.Count == 0) || (!MatchAttempted && Asset.Warnings.Count > 0) ? "需检查" : "待审核";
    public string EvidenceSummary => SnapshotInvalidated ? "源文件已变化 · 请重新扫描" : HasUnsavedEdits ? "资料已修改 · 需重新确认" :
        Review is not null ? Review.Origin == "manual" ? "人工确认 · 手动资料" : $"人工确认 · {EvidenceSource}" :
        MatchFailureCode == "live_unavailable" ? "来源未启用或不可用" :
        MatchFailureCode == "unsupported_query" ? "所选来源不支持此检索类型" :
        !string.IsNullOrEmpty(MatchFailure) ? "识别失败 · 可重试" : CachedCandidates.Count > 1 && CachedSelection is null ? $"标题检索 · {CachedCandidates.Count} 个候选" :
        CachedSelection?.Confidence ?? (MatchAttempted ? "未找到候选" : "尚未识别");
    private string EvidenceSource => CachedSelection?.Candidate.Source ?? (Review?.Provenance.TryGetValue("providerIdentity.source", out var source) == true ? source : "保留来源证据");
    public string MetadataSummary
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(DraftYear)) parts.Add(DraftYear);
            if (!string.IsNullOrWhiteSpace(DraftIdentifier) && CachedSelection is { } chosen && DraftIdentifier != chosen.Candidate.Id) parts.Add($"整理标识 {DraftIdentifier}");
            else if (CachedSelection?.Candidate.CatalogIdentifier is { } catalog) parts.Add($"番号 {catalog.Value}");
            else if (CachedSelection is { } selected) parts.Add($"来源 ID · {selected.Candidate.SourceId}");
            else if (Review is { Origin: "reviewed-candidate" } reviewed && reviewed.Provenance.TryGetValue("review.candidate.catalog.value", out var catalogValue)) parts.Add($"来源番号 {catalogValue}");
            else if (Review is { Origin: "reviewed-candidate" } sourceReview && sourceReview.Provenance.TryGetValue("review.candidate.sourceId", out var sourceId)) parts.Add($"来源 ID · {sourceId}");
            else if (Review is { Origin: "manual" }) parts.Add($"整理标识 {DraftIdentifier}");
            else if (Asset.Identifier is not null) parts.Add($"文件名番号 {Asset.Identifier}");
            if (Review is null) parts.Add("尚未审核");
            return string.Join(" · ", parts);
        }
    }
    // Artwork is only a synthetic fixture illustration, never a downloaded or inferred real poster.
    public string ThumbnailKind
    {
        get
        {
            var fixtureCandidate = CachedSelection?.Candidate;
            var isFixture = fixtureCandidate?.Title == DisplayTitle && fixtureCandidate.Provenance.GetValueOrDefault("fixture") == "true" ||
                Review is { Origin: "reviewed-candidate" } review && review.Provenance.GetValueOrDefault("fixture") == "true";
            return isFixture ? DisplayTitle switch { "The Lantern Map" => "lantern", "Northbound" or "Harbor of Stars" => "mountain", _ => "placeholder" } : "placeholder";
        }
    }
    public string WarningText => SnapshotInvalidated ? "源文件或伴随文件已变化；重新扫描后再审核。" :
        Asset.Warnings.Count == 0 ? "未发现扫描问题" : string.Join("\n", Asset.Warnings);
    public ReviewedMetadata? Review
    {
        get => _review;
        set { if (Set(ref _review, value)) RefreshMetadata(); }
    }

    internal void UpdateScannedAsset(MediaAsset asset)
    {
        Asset = asset;
        Raise(nameof(Asset)); Raise(nameof(SidecarsText)); Raise(nameof(SizeText));
        RefreshMetadata();
    }

    internal void RestoreReview(ReviewedMetadata? review)
    {
        SavedReview = review;
        Review = review;
        DraftIdentifier = BaselineIdentifier = review?.Identifier ?? Asset.Identifier ?? "";
        DraftTitle = BaselineTitle = review?.Title ?? "";
        DraftYear = BaselineYear = review?.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        HasUnsavedEdits = false; HasManualEdits = false;
        RefreshMetadata();
    }

    internal void RefreshMetadata()
    {
        foreach (var name in new[] { nameof(Identifier), nameof(DisplayTitle), nameof(StatusText), nameof(EvidenceSummary), nameof(MetadataSummary), nameof(ThumbnailKind), nameof(WarningText) }) Raise(name);
    }
}

public sealed record CandidateRow(MetadataCandidate Candidate)
{
    public string Title => Candidate.Title;
    public string Detail => $"{Candidate.Year?.ToString() ?? "年份未知"} · {Candidate.Source} · {Identity}";
    public string Identity => Candidate.CatalogIdentifier is { } catalog ? $"{catalog.Namespace}: {catalog.Value}" : $"来源 ID: {Candidate.SourceId} · 无番号";
    public string Confidence => Candidate.CatalogIdentifier is { Namespace: "xcity:hinban" or "javbus:catalog" } &&
        Candidate.Provenance.TryGetValue("matchMethod", out var method) && method == "exact_identifier"
        ? $"番号一致 · {Candidate.Source}" : $"标题候选 · {Candidate.Source}";
    public string Provenance => string.Join("\n", Candidate.Provenance.Select(x => $"{x.Key}: {x.Value}"));
}

public sealed record LogEntry(string Time, string Level, string Message);
public sealed record PlanRow(string Kind, string Source, string Target);

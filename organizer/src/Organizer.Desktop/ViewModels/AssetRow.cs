using Organizer.Core;

namespace Organizer.Desktop.ViewModels;

public sealed class AssetRow(MediaAsset asset) : ObservableObject
{
    private ReviewedMetadata? _review;
    public MediaAsset Asset { get; } = asset;
    public string Filename => Path.GetFileName(Asset.SourcePath);
    public string RelativePath => Asset.RelativePath;
    public string Identifier => Review?.Identifier ?? Asset.Identifier ?? "未识别";
    public string SizeText => Asset.Size >= 1_073_741_824 ? $"{Asset.Size / 1_073_741_824d:0.00} GB" : $"{Asset.Size / 1_048_576d:0.0} MB";
    public string SidecarsText => Asset.Sidecars.Count == 0 ? "—" : string.Join("  ", Asset.Sidecars.Select(s => s.Suffix));
    public string StatusText => Review is not null ? "已审核" : Asset.Warnings.Count > 0 ? "需检查" : "待匹配";
    public string WarningText => Asset.Warnings.Count == 0 ? "未发现扫描问题" : string.Join("\n", Asset.Warnings);
    public ReviewedMetadata? Review
    {
        get => _review;
        set { if (Set(ref _review, value)) { Raise(nameof(StatusText)); Raise(nameof(Identifier)); } }
    }
}

public sealed record CandidateRow(MetadataCandidate Candidate)
{
    public string Title => Candidate.Title;
    public string Detail => $"{Candidate.Id} · {Candidate.Year?.ToString() ?? "年份未知"} · {Candidate.Source}";
    public string Confidence => Candidate.Provenance.TryGetValue("matchMethod", out var method) && method == "exact_identifier"
        ? "标识完全一致" : "标题候选 · 需复核";
    public string Provenance => string.Join("\n", Candidate.Provenance.Select(x => $"{x.Key}: {x.Value}"));
}

public sealed record LogEntry(string Time, string Level, string Message);
public sealed record PlanRow(string Kind, string Source, string Target);

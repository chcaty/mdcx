using System.Collections.ObjectModel;
using System.Globalization;
using Organizer.Core;
using Organizer.Infrastructure;

namespace Organizer.Desktop.ViewModels;

/// <summary>Single-user preview workbench. No filesystem mutation service is exposed here.</summary>
public sealed class WorkbenchViewModel : ObservableObject, IDisposable
{
    private readonly StartupOptions _options;
    private readonly MediaScanner _scanner = new();
    private readonly PathPlanner _planner = new();
    private readonly PythonScraperClient _scraper;
    private readonly List<AssetRow> _allAssets = [];
    private readonly IndexStore? _store;
    private CancellationTokenSource? _operation;
    private long _generation;
    private bool _disposed;
    private bool _isBusy;
    private bool _updatingFields;
    private bool _refreshingAssets;
    private bool _applyingFilter;
    private string _busyKind = "";
    private string _sourcePath = "";
    private string _targetPath = "";
    private string _search = "";
    private string _selectedFilter = "全部文件";
    private AssetRow? _selectedAsset;
    private CandidateRow? _selectedCandidate;
    private string _identifier = "";
    private string _title = "";
    private string _year = "";
    private string _status = "选择一个源文件夹，开始只读扫描。";
    private string _planStatus = "尚未生成计划";
    private string _candidateStatus = "选中文件后，使用离线样例匹配或手动填写。";
    private string _indexStatus = "本地索引";
    private string _planIssues = "";

    public WorkbenchViewModel(StartupOptions options)
    {
        _options = options;
        _scraper = new PythonScraperClient(options.PythonExecutable, options.ResolveWorkerPath());
        ScanCommand = new(ScanAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(SourcePath));
        MatchCommand = new(MatchAsync, () => !IsBusy && SelectedAsset is not null);
        AcceptCandidateCommand = new(AcceptCandidateAsync, () => !IsBusy && SelectedCandidate is not null && SelectedAsset is not null);
        SaveReviewCommand = new(SaveReviewAsync, () => !IsBusy && SelectedAsset is not null);
        BuildPlanCommand = new(BuildPlanAsync, () => !IsBusy && _allAssets.Any(a => a.Review is not null) && !string.IsNullOrWhiteSpace(TargetPath));
        CancelCommand = new(Cancel, () => IsBusy);
        ClearFilterCommand = new(() => { Search = ""; SelectedFilter = "全部文件"; });
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local)) local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            var directory = Path.Combine(local, "MDCxOrganizer");
            _store = new IndexStore(options.IndexPath ?? Path.Combine(directory, "index.db"));
        }
        catch (Exception ex)
        {
            _indexStatus = "索引不可用 · 本次会话可继续";
            AddLog("警告", $"无法打开本地索引：{ex.Message}");
        }
        AddLog("就绪", "预览模式。扫描、匹配与计划不会移动、改名或覆盖媒体文件。");
        AddLog("来源", "离线样例仅用于演示解析流程，不代表联网检索结果。");
    }

    public ObservableCollection<AssetRow> Assets { get; } = [];
    public ObservableCollection<CandidateRow> Candidates { get; } = [];
    public ObservableCollection<PlanRow> PlanOperations { get; } = [];
    public ObservableCollection<LogEntry> Logs { get; } = [];
    public IReadOnlyList<string> Filters { get; } = ["全部文件", "待匹配", "已审核", "需检查"];
    public AsyncCommand ScanCommand { get; }
    public AsyncCommand MatchCommand { get; }
    public AsyncCommand AcceptCandidateCommand { get; }
    public AsyncCommand SaveReviewCommand { get; }
    public AsyncCommand BuildPlanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearFilterCommand { get; }

    public string SourcePath
    {
        get => _sourcePath;
        set
        {
            if (!Set(ref _sourcePath, value)) return;
            CancelCurrent(false);
            _allAssets.Clear(); Assets.Clear(); SelectedAsset = null;
            ClearPlan(); RefreshSummary(); RefreshCommands();
        }
    }
    public string TargetPath
    {
        get => _targetPath;
        set { if (Set(ref _targetPath, value)) { if (_busyKind == "plan") CancelCurrent(false); ClearPlan(); RefreshCommands(); } }
    }
    public string Search { get => _search; set { if (Set(ref _search, value)) ApplyFilter(); } }
    public string SelectedFilter { get => _selectedFilter; set { if (Set(ref _selectedFilter, value)) ApplyFilter(); } }
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) { Raise(nameof(IsIdle)); RefreshCommands(); } } }
    public bool IsIdle => !IsBusy;
    public bool HasSelection => SelectedAsset is not null;
    public bool HasCandidates => Candidates.Count > 0;
    public bool HasPlan => PlanOperations.Count > 0;
    public bool HasPlanIssues => !string.IsNullOrEmpty(PlanIssues);
    public bool IsEmpty => _allAssets.Count == 0;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string CandidateStatus { get => _candidateStatus; private set => Set(ref _candidateStatus, value); }
    public string PlanStatus { get => _planStatus; private set => Set(ref _planStatus, value); }
    public string PlanIssues { get => _planIssues; private set { if (Set(ref _planIssues, value)) Raise(nameof(HasPlanIssues)); } }
    public string IndexStatus { get => _indexStatus; private set => Set(ref _indexStatus, value); }
    public string FileSummary => $"{Assets.Count} / {_allAssets.Count} 个文件";
    public string ReviewSummary => $"{_allAssets.Count(a => a.Review is not null)} 个已审核";
    public string ScanSummary => $"{_allAssets.Count} 个媒体 · {_allAssets.Sum(a => a.Asset.Sidecars.Count)} 个伴随文件";
    public string SelectedFilename => SelectedAsset?.Filename ?? "未选择文件";
    public string SelectedPath => SelectedAsset?.Asset.SourcePath ?? "从中间的文件列表选择一个条目";
    public string SelectedSidecars => SelectedAsset is null ? "—" : SelectedAsset.Asset.Sidecars.Count == 0 ? "无伴随文件" : string.Join("\n", SelectedAsset.Asset.Sidecars.Select(s => Path.GetFileName(s.Path)));
    public string SelectedWarnings => SelectedAsset?.WarningText ?? "";
    public string SelectedCandidateProvenance => SelectedCandidate?.Provenance ?? "暂无来源信息";
    public string SelectedCandidateUrl => SelectedCandidate?.Candidate.SourceUrl ?? "";
    public bool HasCandidateReviewReasons => SelectedCandidate?.Candidate.ReviewReasons.Count > 0;
    public string SelectedCandidateReviewReasons => SelectedCandidate is null ? "" : string.Join("\n", SelectedCandidate.Candidate.ReviewReasons);

    public AssetRow? SelectedAsset
    {
        get => _selectedAsset;
        set
        {
            if (_refreshingAssets || !Set(ref _selectedAsset, value)) return;
            if (_busyKind == "match" || (!_applyingFilter && _busyKind == "review")) CancelCurrent(false);
            Candidates.Clear(); SelectedCandidate = null;
            CandidateStatus = "离线样例：DEMO-001 / DEMO-002。其他文件可手动审核。";
            LoadEditor(value?.Review?.Identifier ?? value?.Asset.Identifier ?? "", value?.Review?.Title ?? "", value?.Review?.Year);
            Raise(nameof(HasSelection)); Raise(nameof(SelectedFilename)); Raise(nameof(SelectedPath)); Raise(nameof(SelectedSidecars)); Raise(nameof(SelectedWarnings)); Raise(nameof(HasCandidates));
            RefreshCommands();
        }
    }
    public CandidateRow? SelectedCandidate
    {
        get => _selectedCandidate;
        set { if (Set(ref _selectedCandidate, value)) { Raise(nameof(SelectedCandidateProvenance)); Raise(nameof(SelectedCandidateUrl)); Raise(nameof(SelectedCandidateReviewReasons)); Raise(nameof(HasCandidateReviewReasons)); RefreshCommands(); } }
    }
    public string EditorIdentifier { get => _identifier; set { if (Set(ref _identifier, value)) InvalidateEditedReview(); } }
    public string EditorTitle { get => _title; set { if (Set(ref _title, value)) InvalidateEditedReview(); } }
    public string EditorYear { get => _year; set { if (Set(ref _year, value)) InvalidateEditedReview(); } }

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_options.DemoPath)) return;
        SourcePath = Path.GetFullPath(_options.DemoPath);
        TargetPath = Path.Combine(Path.GetDirectoryName(SourcePath) ?? SourcePath, "organized-preview");
        await ScanAsync();
    }

    public Task ScanAsync() => RunAsync("scan", "正在扫描文件…", async (generation, token) =>
    {
        var source = SourcePath;
        var result = await _scanner.ScanAsync(source, token);
        if (!IsCurrent(generation, token)) return;
        var indexed = await PersistAsync(() => _store!.SaveScanAsync(result, token), token);
        IReadOnlyDictionary<string, ReviewedMetadata> reviews = new Dictionary<string, ReviewedMetadata>();
        if (indexed && _store is not null)
        {
            try { reviews = await _store.LoadReviewsAsync(token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { AddLog("警告", $"无法恢复已保存的审核：{ex.Message}"); }
        }
        if (!IsCurrent(generation, token)) return;
        _allAssets.Clear();
        _allAssets.AddRange(result.Assets.Select(a => new AssetRow(a) { Review = reviews.GetValueOrDefault(a.Id) }));
        ClearPlan(); ApplyFilter();
        SelectedAsset = Assets.FirstOrDefault();
        foreach (var warning in result.Warnings) AddLog("警告", warning);
        Status = !result.IsComplete ? $"扫描部分完成 · {ScanSummary} · 部分路径无法读取，请查看日志。"
            : result.Assets.Count == 0 ? "扫描完成，未找到支持的媒体文件。" : $"扫描完成 · {ScanSummary}";
        AddLog("扫描", Status);
        RefreshSummary();
    });

    public Task MatchAsync()
    {
        var selected = SelectedAsset;
        if (selected is null || IsBusy) return Task.CompletedTask;
        Candidates.Clear(); SelectedCandidate = null; Raise(nameof(HasCandidates));
        var query = string.IsNullOrWhiteSpace(EditorIdentifier) ? selected.Asset.Stem : EditorIdentifier.Trim();
        return RunAsync("match", "正在读取离线样例候选…", async (generation, token) =>
        {
            var results = await _scraper.MatchAsync(query, token);
            if (!IsCurrent(generation, token) || SelectedAsset != selected) return;
            foreach (var candidate in results) Candidates.Add(new(candidate));
            SelectedCandidate = Candidates.FirstOrDefault();
            CandidateStatus = results.Count == 0 ? "没有匹配的样例候选。请在下方手动填写并确认。" : $"找到 {results.Count} 个离线候选 · 请人工核对";
            Status = CandidateStatus; Raise(nameof(HasCandidates));
            AddLog("匹配", $"{selected.Filename}：{CandidateStatus}");
        });
    }

    public async Task AcceptCandidateAsync()
    {
        var candidate = SelectedCandidate?.Candidate;
        if (candidate is null || SelectedAsset is null || IsBusy) return;
        LoadEditor(candidate.Id, candidate.Title, candidate.Year);
        await SaveReviewAsync(candidate);
    }

    public Task SaveReviewAsync() => SaveReviewAsync(null);

    private Task SaveReviewAsync(MetadataCandidate? candidate)
    {
        var selected = SelectedAsset;
        if (selected is null || IsBusy) return Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(EditorIdentifier) || string.IsNullOrWhiteSpace(EditorTitle))
        {
            Status = "请填写标识和标题后再确认审核。"; AddLog("提示", Status); return Task.CompletedTask;
        }
        int? year = null;
        if (!string.IsNullOrWhiteSpace(EditorYear))
        {
            if (!int.TryParse(EditorYear, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 1888 || parsed > DateTime.Now.Year + 5)
            { Status = "年份需为有效的四位年份，或留空。"; AddLog("提示", Status); return Task.CompletedTask; }
            year = parsed;
        }
        var review = new ReviewedMetadata(EditorIdentifier.Trim(), EditorTitle.Trim(), year)
        {
            Origin = candidate is null ? "manual" : "reviewed-candidate",
            Locked = true,
            Provenance = candidate?.Provenance ?? new Dictionary<string, string>
            {
                ["identifier"] = "manual",
                ["title"] = "manual",
                ["year"] = "manual"
            }
        };
        return RunAsync("review", "正在保存人工审核…", async (generation, token) =>
        {
            var saved = await PersistAsync(() => _store!.SaveReviewAsync(selected.Asset.Id, review, selected.Asset.Fingerprint, token), token);
            if (!IsCurrent(generation, token) || SelectedAsset != selected) return;
            if (_store is not null && !saved)
            {
                selected.Review = null; ClearPlan(); RefreshSummary();
                Status = "审核未保存。索引可能已更新或无法写入，请重新扫描后重试。";
                AddLog("警告", Status);
                return;
            }
            selected.Review = review;
            ClearPlan(); RefreshSummary();
            Status = $"已审核：{review.Identifier} · {review.Title}";
            AddLog("审核", Status);
            ApplyFilter();
        });
    }

    public Task BuildPlanAsync()
    {
        var selections = _allAssets.Where(a => a.Review is not null).Select(a => (a.Asset, a.Review!)).ToArray();
        if (selections.Length == 0 || string.IsNullOrWhiteSpace(TargetPath)) return Task.CompletedTask;
        var target = TargetPath;
        return RunAsync("plan", "正在检查路径与冲突…", async (generation, token) =>
        {
            var plan = await Task.Run(() => _planner.CreatePlan(target, selections), token);
            if (!IsCurrent(generation, token)) return;
            await PersistAsync(() => _store!.SavePlanAsync(plan, token), token);
            if (!IsCurrent(generation, token)) return;
            PlanOperations.Clear();
            foreach (var op in plan.Operations) PlanOperations.Add(new(op.Kind, op.Source, op.Target));
            PlanIssues = string.Join("\n", plan.Issues.Select(i => $"{i.Code} · {i.Message}"));
            PlanStatus = plan.IsSafe ? $"检查通过 · {plan.Operations.Count} 项操作 · 仅预览" : $"发现 {plan.Issues.Count} 个问题 · 已阻止执行";
            Status = PlanStatus; Raise(nameof(HasPlan));
            AddLog(plan.IsSafe ? "计划" : "警告", PlanStatus);
        });
    }

    public void Cancel() => CancelCurrent(true);

    private async Task RunAsync(string kind, string message, Func<long, CancellationToken, Task> action)
    {
        if (_disposed || IsBusy) return;
        var source = new CancellationTokenSource();
        _operation = source;
        var generation = ++_generation;
        _busyKind = kind; IsBusy = true; Status = message;
        var taskId = Guid.NewGuid().ToString("N");
        var state = "completed";
        var detail = message;
        try
        {
            await RecordTaskAsync(taskId, kind, "running", message, source.Token);
            if (IsCurrent(generation, source.Token)) await action(generation, source.Token);
            state = IsCurrent(generation, source.Token) ? "completed" : "cancelled";
            detail = state == "completed" ? Status : "已取消或被新的选择替代。";
        }
        catch (OperationCanceledException)
        {
            state = "cancelled"; detail = "已取消。媒体文件未被更改。";
            if (generation == _generation) Status = detail;
        }
        catch (Exception ex)
        {
            state = "failed"; detail = ex.Message;
            if (generation == _generation)
            {
                Status = $"操作未完成：{ex.Message}";
                if (kind == "match") CandidateStatus = "样例匹配失败。检查 Python 与 worker 路径；也可手动审核。";
                AddLog("错误", Status);
            }
        }
        finally
        {
            await RecordTaskAsync(taskId, kind, state, detail, CancellationToken.None);
            if (generation == _generation) { _operation = null; _busyKind = ""; IsBusy = false; }
            source.Dispose();
        }
    }

    private async Task RecordTaskAsync(string id, string kind, string state, string detail, CancellationToken token)
    {
        if (_store is null || _disposed) return;
        try { await _store.RecordTaskAsync(id, kind, state, detail.Length > 500 ? detail[..500] : detail, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (!_disposed) { IndexStatus = "任务历史保存失败 · 本次会话可继续"; AddLog("警告", $"任务历史保存失败：{ex.Message}"); }
        }
    }

    private bool IsCurrent(long generation, CancellationToken token) => !_disposed && generation == _generation && !token.IsCancellationRequested;

    private void CancelCurrent(bool announce)
    {
        if (_operation is null) return;
        _operation.Cancel(); _operation = null; ++_generation; _busyKind = ""; IsBusy = false;
        if (announce) { Status = "已取消。媒体文件未被更改。"; AddLog("取消", Status); }
    }

    private async Task<bool> PersistAsync(Func<Task> action, CancellationToken token)
    {
        if (_store is null) return false;
        try { await action(); return true; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            token.ThrowIfCancellationRequested();
            IndexStatus = "索引保存失败 · 结果保留于本次会话";
            AddLog("警告", $"本地索引保存失败：{ex.Message}");
            return false;
        }
    }

    private void LoadEditor(string identifier, string title, int? year)
    {
        _updatingFields = true;
        try { EditorIdentifier = identifier; EditorTitle = title; EditorYear = year?.ToString(CultureInfo.InvariantCulture) ?? ""; }
        finally { _updatingFields = false; }
    }

    private void InvalidateEditedReview()
    {
        if (_updatingFields) return;
        if (_busyKind is "review" or "plan" or "match") CancelCurrent(false);
        if (SelectedAsset is not null) SelectedAsset.Review = null;
        ClearPlan(); RefreshSummary(); RefreshCommands();
    }

    private void ApplyFilter()
    {
        var selected = SelectedAsset;
        var filtered = _allAssets.Where(a => (SelectedFilter == "全部文件" || a.StatusText == SelectedFilter) &&
            (string.IsNullOrWhiteSpace(Search) || a.RelativePath.Contains(Search, StringComparison.OrdinalIgnoreCase) || a.Identifier.Contains(Search, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (!Assets.SequenceEqual(filtered))
        {
            _refreshingAssets = true;
            try { Assets.Clear(); foreach (var row in filtered) Assets.Add(row); }
            finally { _refreshingAssets = false; }
        }
        _applyingFilter = true;
        try { SelectedAsset = selected is not null && filtered.Contains(selected) ? selected : filtered.FirstOrDefault(); }
        finally { _applyingFilter = false; }
        Raise(nameof(SelectedAsset));
        Raise(nameof(FileSummary));
    }

    private void ClearPlan()
    {
        PlanOperations.Clear(); PlanIssues = ""; PlanStatus = "审核文件并设置目标目录后，生成预览计划。"; Raise(nameof(HasPlan));
    }

    private void RefreshSummary()
    {
        Raise(nameof(FileSummary)); Raise(nameof(ReviewSummary)); Raise(nameof(ScanSummary)); Raise(nameof(IsEmpty)); RefreshCommands();
    }

    private void RefreshCommands()
    {
        ScanCommand.Refresh(); MatchCommand.Refresh(); AcceptCandidateCommand.Refresh(); SaveReviewCommand.Refresh(); BuildPlanCommand.Refresh(); CancelCommand.Refresh();
    }

    private void AddLog(string level, string message)
    {
        Logs.Insert(0, new(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), level, message));
        if (Logs.Count > 250) Logs.RemoveAt(Logs.Count - 1);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; CancelCurrent(false); _store?.Dispose();
    }
}

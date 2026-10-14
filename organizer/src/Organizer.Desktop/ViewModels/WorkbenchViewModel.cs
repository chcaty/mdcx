using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Organizer.Core;
using Organizer.Infrastructure;

namespace Organizer.Desktop.ViewModels;

/// <summary>Single-user preview workbench. No filesystem mutation service is exposed here.</summary>
public sealed class WorkbenchViewModel : ObservableObject, IDisposable
{
    public const int MaximumBatchSize = 50;
    private const string AutoSource = "自动（xcity → TPDB）";
    private const string SnapshotKey = MediaSnapshot.ProvenanceKey;
    private readonly StartupOptions _options;
    private readonly MediaScanner _scanner = new();
    private readonly PathPlanner _planner = new();
    private readonly PythonScraperClient _scraper;
    private readonly List<AssetRow> _allAssets = [];
    private readonly List<string> _reviewQueue = [];
    private readonly HashSet<string> _invalidatedReviews = [];
    private readonly IndexStore? _store;
    private Task _pendingInvalidations = Task.CompletedTask;
    private readonly ConcurrentQueue<string> _persistenceWarnings = new();
    private readonly ConcurrentDictionary<Guid, PendingReviewWrite> _pendingReviewWrites = new();
    private sealed record PendingReviewWrite(Guid Id, AssetRow Row, ReviewedMetadata Review, Task<bool> Completion)
    {
        private readonly object _cleanupGate = new();
        private Task? _cleanup;
        public Task CleanupOnce(Func<Task> cleanup) { lock (_cleanupGate) return _cleanup ??= cleanup(); }
    }
    private CancellationTokenSource? _operation;
    private long _generation;
    private long _queueGeneration;
    private bool _disposed, _isBusy, _updatingFields, _refreshingAssets, _applyingFilter, _loadingSelection, _updatingChecks;
    private string _busyKind = "", _sourcePath = "", _targetPath = "", _search = "";
    private string _selectedFilter = "全部文件", _selectedSearchMode = "自动", _selectedMatchSource = AutoSource;
    private AssetRow? _selectedAsset;
    private CandidateRow? _selectedCandidate;
    private string _identifier = "", _title = "", _year = "", _matchQuery = "";
    private string _status = "选择一个源文件夹，开始只读扫描。";
    private string _planStatus = "尚未生成计划", _candidateStatus = "选中文件后识别，或手动填写资料。";
    private string _indexStatus = "本地索引", _planIssues = "", _draftStatus = "选中文件后查看命名草案。", _draftIssues = "";

    public WorkbenchViewModel(StartupOptions options)
    {
        _options = options;
        _scraper = new PythonScraperClient(options.PythonExecutable, options.ResolveWorkerPath());
        ScanCommand = new(ScanAsync, () => IsIdle && !string.IsNullOrWhiteSpace(SourcePath));
        MatchCommand = new(MatchAsync, () => IsIdle && HasSelection);
        MatchCheckedCommand = new(MatchCheckedAsync, () => IsIdle && HasCheckedAssets);
        AcceptCandidateCommand = new(AcceptCandidateAsync, () => IsIdle && SelectedCandidate is not null && HasSelection);
        SaveReviewCommand = new(SaveReviewAsync, () => IsIdle && HasSelection);
        ConfirmAndNextCommand = new(ConfirmAndNextAsync, () => CanConfirmCurrent);
        BuildPlanCommand = new(BuildPlanAsync, () => IsIdle && ReviewedCount > 0 && !string.IsNullOrWhiteSpace(TargetPath));
        UndoEditsCommand = new(UndoEditsAsync, () => IsIdle && HasUnsavedEdits);
        CancelCommand = new(Cancel, () => IsBusy || IsReviewing);
        ClearFilterCommand = new(() => { Search = ""; SelectedFilter = "全部文件"; });
        ClearCheckedCommand = new(() => SetChecked(_allAssets, false));
        SelectVisibleCommand = new(() => SetChecked(Assets, true));
        StartReviewCommand = new(StartReview, () => IsIdle && _allAssets.Any(a => a.IsChecked && a.Review is null));
        EndReviewCommand = new(EndReview, () => IsReviewing);
        PreviousAssetCommand = new(() => NavigateAsset(-1), () => IsIdle && CanNavigate(-1));
        NextAssetCommand = new(() => NavigateAsset(1), () => IsIdle && CanNavigate(1));
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local)) local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            _store = new IndexStore(options.IndexPath ?? Path.Combine(local, "MDCxOrganizer", "index.db"));
        }
        catch (Exception ex)
        {
            _indexStatus = "索引不可用 · 本次会话可继续";
            AddLog("警告", $"无法打开本地索引：{ex.Message}");
        }
        AddLog("就绪", "只读预览。扫描、识别与审核不会移动、改名或覆盖媒体文件。");
        AddLog("来源", "候选来自虚构离线样例；未连接实时站点。每个条目都需要人工确认。");
    }

    public ObservableCollection<AssetRow> Assets { get; } = [];
    public ObservableCollection<CandidateRow> Candidates { get; } = [];
    public ObservableCollection<PlanRow> PlanOperations { get; } = [];
    public ObservableCollection<PlanRow> DraftOperations { get; } = [];
    public ObservableCollection<LogEntry> Logs { get; } = [];
    public IReadOnlyList<string> Filters { get; } = ["全部文件", "待审核", "已审核", "需检查"];
    public IReadOnlyList<string> SearchModes { get; } = ["自动", "标题", "番号"];
    public IReadOnlyList<string> MatchSources { get; } = [AutoSource, "xcity", "TPDB（影片）"];
    public AsyncCommand ScanCommand { get; }
    public AsyncCommand MatchCommand { get; }
    public AsyncCommand MatchCheckedCommand { get; }
    public AsyncCommand AcceptCandidateCommand { get; }
    public AsyncCommand SaveReviewCommand { get; }
    public AsyncCommand ConfirmAndNextCommand { get; }
    public AsyncCommand BuildPlanCommand { get; }
    public AsyncCommand UndoEditsCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearFilterCommand { get; }
    public RelayCommand ClearCheckedCommand { get; }
    public RelayCommand SelectVisibleCommand { get; }
    public RelayCommand StartReviewCommand { get; }
    public RelayCommand EndReviewCommand { get; }
    public RelayCommand PreviousAssetCommand { get; }
    public RelayCommand NextAssetCommand { get; }

    public string SourcePath
    {
        get => _sourcePath;
        set
        {
            if (!Set(ref _sourcePath, value)) return;
            CancelCurrent(false); EndReview();
            foreach (var row in _allAssets) row.PropertyChanged -= OnRowChanged;
            _allAssets.Clear(); Assets.Clear(); SelectedAsset = null;
            ClearPlan(); RefreshSummary(); RefreshDraft(); Raise(nameof(SourceFolderName));
        }
    }
    public string TargetPath
    {
        get => _targetPath;
        set
        {
            if (!Set(ref _targetPath, value)) return;
            if (_busyKind == "plan") CancelCurrent(false);
            ClearPlan(); RefreshDraft(); RefreshCommands(); Raise(nameof(TargetFolderName));
        }
    }
    public string SourceFolderName => string.IsNullOrWhiteSpace(SourcePath) ? "尚未选择" : Path.GetFileName(Path.TrimEndingDirectorySeparator(SourcePath));
    public string TargetFolderName => string.IsNullOrWhiteSpace(TargetPath) ? "尚未选择" : Path.GetFileName(Path.TrimEndingDirectorySeparator(TargetPath));
    public string Search { get => _search; set { if (Set(ref _search, value)) ApplyFilter(); } }
    public string SelectedFilter { get => _selectedFilter; set { if (Set(ref _selectedFilter, value)) ApplyFilter(); } }
    public string SelectedSearchMode
    {
        get => _selectedSearchMode;
        set
        {
            if (!Set(ref _selectedSearchMode, value)) return;
            InvalidateSearchInputs(allRows: true);
            if (SelectedAsset is { } row) { row.QueryOverride = ""; Set(ref _matchQuery, GetQuery(row), nameof(MatchQuery)); }
        }
    }
    public string SelectedMatchSource { get => _selectedMatchSource; set { if (Set(ref _selectedMatchSource, value)) InvalidateSearchInputs(allRows: true); } }
    public string MatchQuery
    {
        get => _matchQuery;
        set
        {
            if (!Set(ref _matchQuery, value)) return;
            if (SelectedAsset is { } row) row.QueryOverride = value;
            if (!_loadingSelection) InvalidateSearchInputs();
        }
    }
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) { Raise(nameof(IsIdle)); RefreshCommands(); } } }
    public bool IsIdle => !IsBusy;
    public bool HasSelection => SelectedAsset is not null;
    public bool HasCandidates => Candidates.Count > 0;
    public bool HasEvidence => SelectedCandidate is not null || SelectedAsset?.Review is not null;
    public bool HasPlan => PlanOperations.Count > 0;
    public bool HasPlanIssues => !string.IsNullOrEmpty(PlanIssues);
    public bool HasDraft => DraftOperations.Count > 0;
    public bool HasDraftIssues => !string.IsNullOrEmpty(DraftIssues);
    public bool IsEmpty => _allAssets.Count == 0;
    public bool HasUnsavedEdits => SelectedAsset?.HasUnsavedEdits == true;
    public bool HasCheckedAssets => CheckedCount > 0;
    public int CheckedCount => _allAssets.Count(a => a.IsChecked);
    public int AllCount => _allAssets.Count;
    public int PendingCount => _allAssets.Count(a => a.StatusText == "待审核");
    public int ReviewedCount => _allAssets.Count(a => a.Review is not null);
    public int NeedsAttentionCount => _allAssets.Count(a => a.StatusText == "需检查");
    public string CheckedSummary => $"已勾选 {CheckedCount} 项";
    public string BatchWarning => _allAssets.Count(a => a.IsChecked && a.CachedCandidates.Count > 1) is > 0 and var count
        ? $"含 {count} 项候选歧义，需逐项核对" : CheckedCount > MaximumBatchSize ? $"每批最多识别 {MaximumBatchSize} 项" : "勾选用于批量操作 · 点击行查看资料";
    public bool? VisibleCheckState
    {
        get => Assets.Count == 0 || Assets.All(a => !a.IsChecked) ? false : Assets.All(a => a.IsChecked) ? true : null;
        set => SetChecked(Assets, value ?? true);
    }
    public bool IsReviewing => _reviewQueue.Count > 0;
    public string ReviewQueueSummary => IsReviewing
        ? SelectedAsset is not null && !_reviewQueue.Contains(SelectedAsset.Asset.Id)
            ? "当前文件不在审核队列中。点击下一项返回队列，或退出逐项审核。"
            : $"逐项审核 · {_reviewQueue.Count(id => _allAssets.Any(a => a.Asset.Id == id && a.Review is null))} 项待确认"
        : "确认后，才会加入整理预览。";
    public string SelectionPosition
    {
        get { var rows = NavigationRows(); var index = SelectedAsset is null ? -1 : rows.IndexOf(SelectedAsset); return index < 0 ? IsReviewing ? $"队列外 / {rows.Count}" : "0 / 0" : $"{index + 1} / {rows.Count}"; }
    }
    public bool CanConfirmCurrent => IsIdle && SelectedAsset is { SnapshotInvalidated: false } row &&
        (!IsReviewing || _reviewQueue.Contains(row.Asset.Id)) &&
        !string.IsNullOrWhiteSpace(EditorIdentifier) && !string.IsNullOrWhiteSpace(EditorTitle) &&
        (row.CachedCandidates.Count <= 1 || SelectedCandidate is not null || row.HasManualEdits) &&
        TryEditorMetadata(out _, out _, out _, out _);
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string CandidateStatus { get => _candidateStatus; private set => Set(ref _candidateStatus, value); }
    public string PlanStatus { get => _planStatus; private set => Set(ref _planStatus, value); }
    public string PlanIssues { get => _planIssues; private set { if (Set(ref _planIssues, value)) Raise(nameof(HasPlanIssues)); } }
    public string DraftStatus { get => _draftStatus; private set => Set(ref _draftStatus, value); }
    public string DraftIssues { get => _draftIssues; private set { if (Set(ref _draftIssues, value)) Raise(nameof(HasDraftIssues)); } }
    public string DraftLabel => SelectedAsset?.Review is null ? "命名草案" : "已审核命名预览";
    public string IndexStatus { get => _indexStatus; private set => Set(ref _indexStatus, value); }
    public string FileSummary => $"{Assets.Count} / {_allAssets.Count} 个文件";
    public string ReviewSummary => $"{ReviewedCount} 个已审核";
    public string ScanSummary => $"{AllCount} 个媒体文件 · {_allAssets.Sum(a => a.Asset.Sidecars.Count)} 个伴随文件";
    public string SelectedFilename => SelectedAsset?.Filename ?? "未选择文件";
    public string SelectedDisplayTitle => SelectedAsset?.DisplayTitle ?? "选择一个媒体文件";
    public string SelectedMetadataSummary => SelectedAsset?.MetadataSummary ?? "";
    public string SelectedEvidenceSummary => SelectedAsset?.EvidenceSummary ?? "尚未识别";
    public string SelectedPath => SelectedAsset?.SourcePath ?? "从中间的文件列表选择一个条目";
    public int SelectedSidecarCount => SelectedAsset?.Asset.Sidecars.Count ?? 0;
    public string SelectedSidecars => SelectedAsset is null ? "—" : SelectedSidecarCount == 0 ? "无伴随文件" : string.Join("\n", SelectedAsset.Asset.Sidecars.Select(s => Path.GetFileName(s.Path)));
    public string SelectedWarnings => SelectedAsset?.WarningText ?? "";
    public string SelectedCandidateProvenance => SelectedCandidate?.Provenance ?? ((SelectedAsset?.Review ?? SelectedAsset?.SavedReview) is { } review ? string.Join("\n", review.Provenance.Select(x => $"{x.Key}: {x.Value}")) : "暂无来源信息");
    public string SelectedCandidateUrl => SelectedCandidate?.Candidate.SourceUrl ?? ReviewEvidence("review.candidate.sourceUrl", "field.sourceId.url");
    public string SelectedCatalogIdentifier
    {
        get
        {
            if (SelectedCandidate?.Candidate.CatalogIdentifier is { } catalog) return $"{catalog.Namespace} · {catalog.Value}";
            var catalogNamespace = ReviewEvidence("review.candidate.catalog.namespace", "field.catalogIdentifier.namespace");
            var value = ReviewEvidence("review.candidate.catalog.value", "field.id.rawValue");
            if (string.IsNullOrWhiteSpace(catalogNamespace) || string.IsNullOrWhiteSpace(value)) return "无来源番号";
            return $"{((SelectedAsset?.HasUnsavedEdits == true || SelectedAsset?.Review?.Origin == "manual") ? "原来源证据 · " : "")}{catalogNamespace} · {value}";
        }
    }
    public string SelectedSourceId
    {
        get { var value = SelectedCandidate?.Candidate.SourceId ?? ReviewEvidence("review.candidate.sourceId", "providerIdentity.value"); return string.IsNullOrWhiteSpace(value) ? "—" : value; }
    }
    private string ReviewEvidence(string primaryKey, string fallbackKey) => (SelectedAsset?.Review ?? SelectedAsset?.SavedReview)?.Provenance is { } evidence
        ? evidence.GetValueOrDefault(primaryKey) ?? evidence.GetValueOrDefault(fallbackKey) ?? "" : "";
    public string SelectedCandidatePosition => SelectedCandidate is null ? $"待选择 · {Candidates.Count} 个候选" : $"候选资料 {Candidates.IndexOf(SelectedCandidate) + 1} / {Candidates.Count}";
    public bool HasCandidateReviewReasons => SelectedCandidate?.Candidate.ReviewReasons.Count > 0;
    public string SelectedCandidateReviewReasons => SelectedCandidate is null ? "" : string.Join("\n", SelectedCandidate.Candidate.ReviewReasons);

    public AssetRow? SelectedAsset
    {
        get => _selectedAsset;
        set
        {
            if (_refreshingAssets || !Set(ref _selectedAsset, value)) return;
            if (_busyKind == "match" || (!_applyingFilter && _busyKind is "review" or "undo")) CancelCurrent(false);
            LoadSelection();
        }
    }
    public CandidateRow? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (value is not null && !Candidates.Contains(value)) return;
            if (!Set(ref _selectedCandidate, value)) return;
            if (!_loadingSelection && _busyKind is "review" or "undo" or "plan") CancelCurrent(false);
            if (SelectedAsset is { } row)
            {
                row.CachedSelection = value;
                if (!_loadingSelection && value is not null) LoadCandidateDraft(row, value.Candidate);
            }
            RefreshSelection(); RefreshDraft(); RefreshCommands();
        }
    }
    public string EditorIdentifier { get => _identifier; set { if (Set(ref _identifier, value)) OnEditorChanged(); } }
    public string EditorTitle { get => _title; set { if (Set(ref _title, value)) OnEditorChanged(); } }
    public string EditorYear { get => _year; set { if (Set(ref _year, value)) OnEditorChanged(); } }

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_options.DemoPath)) return;
        SourcePath = Path.GetFullPath(_options.DemoPath);
        TargetPath = Path.Combine(Path.GetDirectoryName(SourcePath) ?? SourcePath, "organized-preview");
        await ScanAsync();
    }

    public Task ScanAsync() => RunAsync("scan", "正在扫描文件…", async (generation, token) =>
    {
        await _pendingInvalidations;
        await FlushUnappliedWritesAsync();
        token.ThrowIfCancellationRequested();
        var source = SourcePath;
        var previous = _allAssets.ToDictionary(a => a.Asset.Id);
        var focusedId = SelectedAsset?.Asset.Id;
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
        foreach (var row in _allAssets) row.PropertyChanged -= OnRowChanged;
        _allAssets.Clear();
        var legacyReviewCount = 0;
        foreach (var asset in result.Assets)
        {
            AssetRow row;
            if (previous.TryGetValue(asset.Id, out var existing) && !existing.SnapshotInvalidated && SameSnapshot(existing.Asset, asset))
            { row = existing; row.UpdateScannedAsset(asset); }
            else
            {
                row = new AssetRow(asset);
                if (!_invalidatedReviews.Contains(asset.Id) && reviews.TryGetValue(asset.Id, out var review))
                {
                    if (ReviewMatchesSnapshot(review, asset)) row.RestoreReview(review);
                    else if (!review.Provenance.ContainsKey(SnapshotKey)) legacyReviewCount++;
                }
            }
            row.PropertyChanged += OnRowChanged;
            _allAssets.Add(row);
        }
        _reviewQueue.RemoveAll(id => !_allAssets.Any(a => a.Asset.Id == id));
        ClearPlan(); ApplyFilter();
        SelectedAsset = Assets.FirstOrDefault(a => a.Asset.Id == focusedId) ?? Assets.FirstOrDefault();
        // An unchanged focused row still needs notifications after collection replacement.
        LoadSelection();
        foreach (var warning in result.Warnings) AddLog("警告", warning);
        Status = !result.IsComplete ? $"扫描部分完成 · {ScanSummary} · 部分路径无法读取，请查看日志。"
            : result.Assets.Count == 0 ? "扫描完成，未找到支持的媒体文件。" : $"扫描完成 · {ScanSummary}";
        if (legacyReviewCount > 0)
        {
            var warning = $"{legacyReviewCount} 项旧版审核缺少媒体组快照，需要重新核对。原始记录仍保留。";
            Status += $" · {legacyReviewCount} 项旧版审核需复核";
            AddLog("提示", warning);
        }
        AddLog("扫描", Status); RefreshSummary();
    });

    public Task MatchAsync()
    {
        var selected = SelectedAsset;
        if (selected is null || IsBusy) return Task.CompletedTask;
        var query = GetQuery(selected);
        var sources = GetSources();
        ClearCandidates(selected);
        return RunAsync("match", "正在读取离线样例候选…", async (generation, token) =>
        {
            if (!EnsureSnapshot(selected)) return;
            var results = await _scraper.MatchAsync(query, token, sources: sources);
            if (!IsCurrent(generation, token) || SelectedAsset != selected || !EnsureSnapshot(selected)) return;
            ApplyCandidates(selected, results);
            Status = CandidateStatus; AddLog("识别", $"{selected.Filename}：{CandidateStatus}");
        });
    }

    public Task MatchCheckedAsync()
    {
        if (IsBusy) return Task.CompletedTask;
        var selected = _allAssets.Where(a => a.IsChecked).ToArray();
        if (selected.Length == 0) return Task.CompletedTask;
        if (selected.Length > MaximumBatchSize)
        { Status = $"每批最多识别 {MaximumBatchSize} 项；请减少勾选数量后重试。"; return Task.CompletedTask; }
        var jobs = selected.Select(a => (Row: a, Query: GetQuery(a))).ToArray();
        var sources = GetSources();
        return RunAsync("batch-match", $"正在识别勾选项 · 0 / {jobs.Length}", async (generation, token) =>
        {
            var completed = 0;
            foreach (var (row, query) in jobs)
            {
                if (!IsCurrent(generation, token)) return;
                if (!EnsureSnapshot(row)) continue;
                ClearCandidates(row);
                try
                {
                    var results = await _scraper.MatchAsync(query, token, sources: sources);
                    if (!IsCurrent(generation, token)) return;
                    if (EnsureSnapshot(row)) ApplyCandidates(row, results);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (!IsCurrent(generation, token)) return;
                    row.MatchAttempted = true; row.MatchFailure = ex.Message; row.RefreshMetadata();
                    if (SelectedAsset == row) CandidateStatus = "识别失败。可以重试或手动审核。";
                    AddLog("警告", $"{row.Filename}：{ex.Message}");
                }
                completed++;
                Status = $"正在识别勾选项 · {completed} / {jobs.Length}";
            }
            Status = $"已完成 {completed} 项离线识别 · 尚未自动确认任何资料";
            AddLog("识别", Status); ApplyFilter(); RefreshSummary();
        });
    }

    public async Task AcceptCandidateAsync()
    {
        if (SelectedCandidate is not { } selected || SelectedAsset is not { } row || IsBusy) return;
        LoadCandidateDraft(row, selected.Candidate);
        await SaveReviewCoreAsync(selected.Candidate);
    }

    public async Task SaveReviewAsync() => await SaveReviewCoreAsync(null);

    private async Task<bool> SaveReviewCoreAsync(MetadataCandidate? candidate)
    {
        var selected = SelectedAsset;
        if (selected is null || IsBusy || !EnsureSnapshot(selected)) return false;
        if (!TryEditorMetadata(out var identifier, out var title, out var year, out var error))
        { Status = error; AddLog("提示", error); return false; }
        var provenance = candidate is not null ? new Dictionary<string, string>(candidate.Provenance) :
            SelectedCandidate is { } evidence ? new Dictionary<string, string>(evidence.Candidate.Provenance) :
            selected.SavedReview is { } priorReview ? new Dictionary<string, string>(priorReview.Provenance) : new Dictionary<string, string>();
        if ((candidate ?? SelectedCandidate?.Candidate) is { } sourceEvidence)
        {
            provenance["review.candidate.source"] = sourceEvidence.Source;
            provenance["review.candidate.sourceId"] = sourceEvidence.SourceId;
            provenance["review.candidate.sourceUrl"] = sourceEvidence.SourceUrl;
            if (sourceEvidence.CatalogIdentifier is { } catalog)
            {
                provenance["review.candidate.catalog.namespace"] = catalog.Namespace;
                provenance["review.candidate.catalog.value"] = catalog.Value;
            }
        }
        if (candidate is null)
        {
            provenance["identifier"] = "manual"; provenance["title"] = "manual"; provenance["year"] = "manual";
            provenance["review.override.identifier"] = "manual"; provenance["review.override.title"] = "manual"; provenance["review.override.year"] = "manual";
            provenance["review.override"] = SelectedCandidate is null ? "manual-entry" : "manual-correction";
        }
        provenance[SnapshotKey] = Snapshot(selected.Asset);
        provenance["review.confirmationId"] = Guid.NewGuid().ToString("N");
        var review = new ReviewedMetadata(identifier, title, year)
        { Origin = candidate is null ? "manual" : "reviewed-candidate", Locked = true, Provenance = provenance };
        var accepted = false;
        await RunAsync("review", "正在保存人工审核…", async (generation, token) =>
        {
            await _pendingInvalidations;
            await FlushUnappliedWritesAsync();
            if (!IsCurrent(generation, token) || SelectedAsset != selected || !EnsureSnapshot(selected)) return;
            var write = BeginReviewWrite(selected, review, token);
            var saved = await write.Completion;
            if (!IsCurrent(generation, token) || SelectedAsset != selected || !EnsureSnapshot(selected))
            { await DiscardReviewWriteAsync(write); return; }
            _pendingReviewWrites.TryRemove(write.Id, out _);
            if (_store is not null && !saved)
            {
                selected.Review = null; ClearPlan(); RefreshSummary();
                Status = "审核未保存。索引可能已更新或无法写入，请重新扫描后重试。"; AddLog("警告", Status); return;
            }
            _invalidatedReviews.Remove(selected.Asset.Id);
            selected.RestoreReview(review); LoadEditor(selected); accepted = true;
            ClearPlan(); RefreshSummary(); RefreshSelection(); RefreshDraft();
            Status = $"已审核：{review.Identifier} · {review.Title}"; AddLog("审核", Status);
            ApplyFilter();
        });
        return accepted;
    }

    public void StartReview()
    {
        if (IsBusy) return;
        var rows = _allAssets.Where(a => a.IsChecked && a.Review is null).ToArray();
        if (rows.Length == 0) { Status = "请先勾选需要审核的文件。"; return; }
        if (rows.Length > MaximumBatchSize) { Status = $"每次逐项审核最多 {MaximumBatchSize} 项，请减少勾选数量。"; return; }
        _reviewQueue.Clear(); _reviewQueue.AddRange(rows.Select(a => a.Asset.Id)); ++_queueGeneration;
        Search = ""; SelectedFilter = "全部文件"; SelectedAsset = rows[0];
        Status = $"开始逐项审核 · {rows.Length} 项 · 每次只确认当前文件";
        RefreshSummary(); RefreshSelection();
    }

    public void EndReview()
    {
        if (_busyKind == "review") CancelCurrent(false);
        _reviewQueue.Clear(); ++_queueGeneration;
        Raise(nameof(IsReviewing)); Raise(nameof(ReviewQueueSummary)); Raise(nameof(SelectionPosition)); RefreshCommands();
    }

    public async Task ConfirmAndNextAsync()
    {
        if (!CanConfirmCurrent || SelectedAsset is not { } current) return;
        var sequence = _queueGeneration;
        var rows = NavigationRows();
        var index = rows.IndexOf(current);
        var evidence = SelectedCandidate?.Candidate;
        if (current.HasUnsavedEdits || evidence is not null && !EditorMatches(evidence)) evidence = null;
        if (!await SaveReviewCoreAsync(evidence) || sequence != _queueGeneration || _disposed) return;
        var next = rows.Skip(index + 1).FirstOrDefault(a => a.Review is null && _allAssets.Contains(a));
        if (next is not null) FocusReviewRow(next);
        else if (IsReviewing)
        {
            var remaining = rows.FirstOrDefault(a => a.Review is null && _allAssets.Contains(a));
            if (remaining is not null) FocusReviewRow(remaining);
            else { EndReview(); Status = "本批逐项审核已完成。可生成已审核文件的整理预览。"; }
        }
        RefreshSummary(); RefreshSelection();
    }

    public async Task UndoEditsAsync()
    {
        if (IsBusy || SelectedAsset is not { HasUnsavedEdits: true } row || !EnsureSnapshot(row)) return;
        await RunAsync("undo", "正在撤销尚未确认的资料修改…", async (generation, token) =>
        {
            await _pendingInvalidations;
            await FlushUnappliedWritesAsync();
            if (!IsCurrent(generation, token) || SelectedAsset != row || !EnsureSnapshot(row)) return;
            var baseline = row.SavedReview;
            if (baseline is not null)
                baseline = baseline with { Provenance = new Dictionary<string, string>(baseline.Provenance) { ["review.confirmationId"] = Guid.NewGuid().ToString("N") } };
            if (baseline is not null)
            {
                var write = BeginReviewWrite(row, baseline, token);
                var saved = await write.Completion;
                if (!IsCurrent(generation, token) || SelectedAsset != row || !EnsureSnapshot(row))
                { await DiscardReviewWriteAsync(write); return; }
                _pendingReviewWrites.TryRemove(write.Id, out _);
                if (_store is not null && !saved) { Status = "未能恢复原审核，请重新扫描后核对。"; return; }
                _invalidatedReviews.Remove(row.Asset.Id);
            }
            row.DraftIdentifier = row.BaselineIdentifier; row.DraftTitle = row.BaselineTitle; row.DraftYear = row.BaselineYear;
            row.SavedReview = baseline; row.Review = baseline; row.HasUnsavedEdits = false; row.HasManualEdits = false;
            // A different candidate must not keep supplying evidence for the restored values.
            row.CachedSelection = row.CachedCandidates.FirstOrDefault(c =>
                c.Candidate.Id == row.DraftIdentifier && c.Title == row.DraftTitle && (c.Candidate.Year?.ToString(CultureInfo.InvariantCulture) ?? "") == row.DraftYear);
            row.RefreshMetadata(); LoadSelection(); ClearPlan(); RefreshSummary();
            Status = baseline is null ? "已撤销尚未确认的资料修改。" : "已恢复原先确认的资料。";
        });
    }

    public Task BuildPlanAsync()
    {
        if (IsBusy) return Task.CompletedTask;
        foreach (var row in _allAssets.Where(a => a.Review is not null).ToArray()) EnsureSnapshot(row);
        var selections = _allAssets.Where(a => a.Review is not null && !a.HasUnsavedEdits && !a.SnapshotInvalidated).Select(a => (a.Asset, a.Review!)).ToArray();
        if (selections.Length == 0 || string.IsNullOrWhiteSpace(TargetPath)) return Task.CompletedTask;
        var target = TargetPath;
        return RunAsync("plan", "正在检查已审核文件的路径与冲突…", async (generation, token) =>
        {
            await _pendingInvalidations;
            await FlushUnappliedWritesAsync();
            var plan = await Task.Run(() => _planner.CreatePlan(target, selections), token);
            if (!IsCurrent(generation, token)) return;
            await PersistAsync(() => _store!.SavePlanAsync(plan, token), token);
            if (!IsCurrent(generation, token)) return;
            PlanOperations.Clear();
            foreach (var op in plan.Operations) PlanOperations.Add(new(op.Kind, op.Source, op.Target));
            PlanIssues = string.Join("\n", plan.Issues.Select(i => $"{i.Code} · {i.Message}"));
            PlanStatus = plan.IsSafe ? $"检查通过 · {plan.Operations.Count} 项操作 · 仅预览" : $"发现 {plan.Issues.Count} 个问题 · 仅预览，存在冲突的文件组不可执行";
            Status = PlanStatus; Raise(nameof(HasPlan)); AddLog(plan.IsSafe ? "计划" : "警告", PlanStatus);
        });
    }

    public void Cancel() { CancelCurrent(true); if (IsReviewing) { EndReview(); Status = "已结束逐项审核。未确认的条目仍未审核。"; } }

    private async Task RunAsync(string kind, string message, Func<long, CancellationToken, Task> action)
    {
        if (_disposed || IsBusy) return;
        var source = new CancellationTokenSource(); _operation = source;
        var generation = ++_generation;
        _busyKind = kind; IsBusy = true; Status = message;
        var taskId = Guid.NewGuid().ToString("N"); var state = "completed"; var detail = message;
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
                if (kind == "match" && SelectedAsset is { } row)
                { row.MatchAttempted = true; row.MatchFailure = ex.Message; row.RefreshMetadata(); CandidateStatus = "样例识别失败。检查 Python 与 worker 路径；也可手动审核。"; }
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
        catch (Exception ex) { if (!_disposed) { IndexStatus = "任务历史保存失败 · 本次会话可继续"; AddLog("警告", $"任务历史保存失败：{ex.Message}"); } }
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
            token.ThrowIfCancellationRequested(); IndexStatus = "索引保存失败 · 请查看日志";
            AddLog("警告", $"本地索引保存失败：{ex.Message}"); return false;
        }
    }

    private void QueueReviewInvalidation(AssetRow row)
    {
        var prior = row.Review;
        row.Review = null;
        if (prior is null) return;
        _invalidatedReviews.Add(row.Asset.Id);
        if (_store is null) return;
        var preceding = _pendingInvalidations;
        _pendingInvalidations = InvalidateAsync();
        async Task InvalidateAsync()
        {
            await preceding.ConfigureAwait(false);
            try { await _store.InvalidateReviewAsync(row.Asset.Id, row.Asset.Fingerprint, prior).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _persistenceWarnings.Enqueue($"无法清除旧审核：{ex.Message}。重新打开后请勿沿用旧资料。");
            }
        }
    }

    private PendingReviewWrite BeginReviewWrite(AssetRow row, ReviewedMetadata review, CancellationToken token)
    {
        var write = new PendingReviewWrite(Guid.NewGuid(), row, review, WriteAsync());
        _pendingReviewWrites.TryAdd(write.Id, write);
        return write;
        async Task<bool> WriteAsync()
        {
            if (_store is null) return false;
            try
            {
                await _store.SaveReviewAsync(row.Asset.Id, review, row.Asset.Fingerprint, token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex) { _persistenceWarnings.Enqueue($"审核保存失败：{ex.Message}"); return false; }
        }
    }
    private async Task DiscardReviewWriteAsync(PendingReviewWrite write)
    {
        await CleanupReviewWriteAsync(write);
        _pendingReviewWrites.TryRemove(write.Id, out _);
    }
    private Task CleanupReviewWriteAsync(PendingReviewWrite write) => write.CleanupOnce(async () =>
    {
        if (!await write.Completion.ConfigureAwait(false) || _store is null) return;
        try { await _store.InvalidateReviewAsync(write.Row.Asset.Id, write.Row.Asset.Fingerprint, write.Review).ConfigureAwait(false); }
        catch (Exception ex) { _persistenceWarnings.Enqueue($"未能清除已取消操作的审核：{ex.Message}"); }
    });
    private async Task FlushUnappliedWritesAsync()
    {
        foreach (var write in _pendingReviewWrites.Values.ToArray()) await DiscardReviewWriteAsync(write);
    }

    private void LoadSelection()
    {
        _loadingSelection = true;
        try
        {
            Candidates.Clear();
            if (SelectedAsset is { } row)
            {
                foreach (var candidate in row.CachedCandidates) Candidates.Add(candidate);
                SelectedCandidate = row.CachedSelection;
                LoadEditor(row);
                Set(ref _matchQuery, GetQuery(row), nameof(MatchQuery));
                CandidateStatus = CandidateMessage(row);
            }
            else { SelectedCandidate = null; LoadEditor(null); Set(ref _matchQuery, "", nameof(MatchQuery)); CandidateStatus = "选中文件后识别，或手动填写资料。"; }
        }
        finally { _loadingSelection = false; }
        RefreshSelection(); RefreshDraft(); RefreshCommands();
    }
    private void LoadEditor(AssetRow? row)
    {
        _updatingFields = true;
        try { EditorIdentifier = row?.DraftIdentifier ?? ""; EditorTitle = row?.DraftTitle ?? ""; EditorYear = row?.DraftYear ?? ""; }
        finally { _updatingFields = false; }
    }
    private void LoadCandidateDraft(AssetRow row, MetadataCandidate candidate)
    {
        if (row.Review is { } review && (review.Identifier != candidate.Id || review.Title != candidate.Title || review.Year != candidate.Year)) QueueReviewInvalidation(row);
        row.HasManualEdits = false;
        row.DraftIdentifier = candidate.Id; row.DraftTitle = candidate.Title; row.DraftYear = candidate.Year?.ToString(CultureInfo.InvariantCulture) ?? "";
        if (row.SavedReview is null)
        { row.BaselineIdentifier = row.DraftIdentifier; row.BaselineTitle = row.DraftTitle; row.BaselineYear = row.DraftYear; row.HasUnsavedEdits = false; }
        else row.HasUnsavedEdits = row.Review is null;
        row.RefreshMetadata(); if (SelectedAsset == row) LoadEditor(row);
        ClearPlan(); RefreshSelection(); RefreshSummary(); RefreshDraft();
    }
    private void OnEditorChanged()
    {
        if (_updatingFields || SelectedAsset is not { } row) return;
        if (_busyKind is "review" or "plan" or "match" or "batch-match" or "undo") CancelCurrent(false);
        QueueReviewInvalidation(row);
        row.DraftIdentifier = EditorIdentifier; row.DraftTitle = EditorTitle; row.DraftYear = EditorYear;
        row.HasUnsavedEdits = true; row.HasManualEdits = true; row.RefreshMetadata();
        ClearPlan(); RefreshSummary(); RefreshSelection(); RefreshDraft();
    }
    private void InvalidateSearchInputs(bool allRows = false)
    {
        if (_busyKind is "match" or "batch-match" or "review" or "undo" or "plan") CancelCurrent(false);
        if (allRows) { foreach (var item in _allAssets) ClearCandidates(item); }
        else if (SelectedAsset is { } row) ClearCandidates(row);
        RefreshCommands();
    }
    private void ClearCandidates(AssetRow row)
    {
        row.CachedCandidates = []; row.CachedSelection = null; row.MatchAttempted = false; row.MatchFailure = "";
        // Candidate-derived draft fields cannot be used as evidence after a query/source change.
        if (row.Review is null && !row.HasManualEdits)
        {
            row.DraftIdentifier = row.Asset.Identifier ?? ""; row.DraftTitle = ""; row.DraftYear = "";
            if (row.SavedReview is null) { row.BaselineIdentifier = row.DraftIdentifier; row.BaselineTitle = ""; row.BaselineYear = ""; row.HasUnsavedEdits = false; }
        }
        row.RefreshMetadata();
        if (SelectedAsset == row) LoadSelection();
        RefreshSummary();
    }
    private void ApplyCandidates(AssetRow row, IReadOnlyList<MetadataCandidate> results)
    {
        row.CachedCandidates = results.Select(c => new CandidateRow(c)).ToArray();
        row.CachedSelection = row.Review is { } reviewed
            ? row.CachedCandidates.FirstOrDefault(c => c.Candidate.Id == reviewed.Identifier && c.Title == reviewed.Title && c.Candidate.Year == reviewed.Year)
            : results.Count == 1 ? row.CachedCandidates[0] : null;
        row.MatchAttempted = true; row.MatchFailure = "";
        if (row.Review is null && row.CachedSelection is { } choice && !row.HasUnsavedEdits) LoadCandidateDraft(row, choice.Candidate);
        row.RefreshMetadata();
        if (SelectedAsset == row) LoadSelection();
        RefreshSummary();
    }
    private static string CandidateMessage(AssetRow row) => row.SnapshotInvalidated ? "源文件已变化，请重新扫描。" :
        !string.IsNullOrEmpty(row.MatchFailure) ? "识别失败。可重试或手动审核。" : !row.MatchAttempted ? "离线样例 · 可用番号或标题检索。" :
        row.CachedCandidates.Count == 0 ? "没有匹配的离线候选。请手动填写并核对。" :
        row.CachedCandidates.Count > 1 ? $"找到 {row.CachedCandidates.Count} 个候选 · 请明确选择后逐项审核" : "找到 1 个离线候选 · 仍需人工核对";
    private string GetQuery(AssetRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.QueryOverride)) return row.QueryOverride.Trim();
        if (SelectedSearchMode == "标题")
            return !string.IsNullOrWhiteSpace(row.DraftTitle) ? row.DraftTitle.Trim() : Regex.Replace(row.Asset.Stem, @"(?i)[. _-]+(2160p|1080p|720p|4k)$", "").Replace('.', ' ').Trim();
        if (SelectedSearchMode == "番号") return row.Asset.Identifier ?? row.DraftIdentifier;
        return row.Asset.Identifier ?? row.Asset.Stem;
    }
    private IReadOnlyList<string>? GetSources() => SelectedMatchSource switch { "xcity" => ["xcity"], "TPDB（影片）" => ["theporndb_movies"], _ => null };
    private bool EditorMatches(MetadataCandidate candidate) => EditorIdentifier.Trim() == candidate.Id && EditorTitle.Trim() == candidate.Title && EditorYear.Trim() == (candidate.Year?.ToString(CultureInfo.InvariantCulture) ?? "");
    private bool TryEditorMetadata(out string identifier, out string title, out int? year, out string error)
    {
        identifier = EditorIdentifier.Trim(); title = EditorTitle.Trim(); year = null; error = "";
        if (string.IsNullOrWhiteSpace(identifier) || string.IsNullOrWhiteSpace(title)) { error = "请填写标识和标题后再确认审核。"; return false; }
        if (!string.IsNullOrWhiteSpace(EditorYear))
        {
            if (EditorYear.Trim().Length != 4 || !int.TryParse(EditorYear.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 1888 || parsed > DateTime.Now.Year + 5)
            { error = "年份需为有效的四位年份，或留空。"; return false; }
            year = parsed;
        }
        return true;
    }

    private static string Snapshot(MediaAsset asset) => MediaSnapshot.Fingerprint(asset);
    private static bool SameSnapshot(MediaAsset first, MediaAsset second) => Snapshot(first) == Snapshot(second);
    private static bool ReviewMatchesSnapshot(ReviewedMetadata review, MediaAsset asset) =>
        review.Provenance.TryGetValue(SnapshotKey, out var snapshot) && snapshot == Snapshot(asset);
    private bool EnsureSnapshot(AssetRow row)
    {
        if (!row.SnapshotInvalidated && FileSnapshotMatches(row.Asset.SourcePath, row.Asset.Fingerprint) &&
            row.Asset.Sidecars.All(sidecar => FileSnapshotMatches(sidecar.Path, sidecar.Fingerprint))) return true;
        QueueReviewInvalidation(row);
        row.SnapshotInvalidated = true; row.IsChecked = false; row.CachedCandidates = []; row.CachedSelection = null;
        row.SavedReview = null; row.HasUnsavedEdits = false; row.DraftIdentifier = row.Asset.Identifier ?? ""; row.DraftTitle = ""; row.DraftYear = "";
        row.RefreshMetadata(); ClearPlan(); RefreshSummary();
        if (SelectedAsset == row) LoadSelection();
        Status = $"源文件或伴随文件已变化：{row.Filename}。请重新扫描后再审核。";
        return false;
    }
    private static bool FileSnapshotMatches(string path, string fingerprint)
    {
        try
        {
            if (!File.Exists(path) || PathSafety.HasLinkComponent(path)) return false;
            var info = new FileInfo(path);
            return fingerprint == $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
    private List<AssetRow> NavigationRows() => IsReviewing ? _reviewQueue.Select(id => _allAssets.FirstOrDefault(a => a.Asset.Id == id)).OfType<AssetRow>().ToList() : Assets.ToList();
    private bool CanNavigate(int step) { var rows = NavigationRows(); var index = SelectedAsset is null ? -1 : rows.IndexOf(SelectedAsset); return index < 0 ? IsReviewing && rows.Count > 0 : index + step >= 0 && index + step < rows.Count; }
    private void NavigateAsset(int step) { if (!CanNavigate(step)) return; var rows = NavigationRows(); var index = rows.IndexOf(SelectedAsset!); FocusReviewRow(rows[index < 0 ? step > 0 ? 0 : rows.Count - 1 : index + step]); }

    private void FocusReviewRow(AssetRow row)
    {
        if (IsReviewing && !Assets.Contains(row)) { Search = ""; SelectedFilter = "全部文件"; }
        SelectedAsset = row;
    }

    private void RefreshDraft()
    {
        DraftOperations.Clear(); DraftIssues = "";
        if (SelectedAsset is null) DraftStatus = "选中文件后查看命名草案。";
        else if (SelectedAsset.SnapshotInvalidated) DraftStatus = "源文件已变化，请重新扫描。";
        else if (string.IsNullOrWhiteSpace(TargetPath)) DraftStatus = "选择目标目录后检查命名草案。";
        else if (!TryEditorMetadata(out var identifier, out var title, out var year, out _)) DraftStatus = "资料尚不完整 · 未审核，未加入整理预览";
        else
        {
            var draft = _planner.CreatePlan(TargetPath, [(SelectedAsset.Asset, new ReviewedMetadata(identifier, title, year))]);
            foreach (var operation in draft.Operations) DraftOperations.Add(new(operation.Kind, operation.Source, operation.Target));
            DraftIssues = string.Join("\n", draft.Issues.Select(i => $"{i.Code} · {i.Message}"));
            DraftStatus = !draft.IsSafe ? $"发现 {draft.Issues.Count} 个问题 · 保留现有文件" : SelectedAsset.Review is null
                ? "路径检查通过 · 未审核，未加入整理预览" : "路径检查通过 · 已审核，仅预览";
        }
        Raise(nameof(HasDraft)); Raise(nameof(DraftLabel));
    }
    private void ApplyFilter()
    {
        var selected = SelectedAsset;
        var filtered = _allAssets.Where(a => (SelectedFilter == "全部文件" || a.StatusText == SelectedFilter || SelectedFilter == "待匹配" && a.StatusText == "待审核") &&
            (string.IsNullOrWhiteSpace(Search) || a.RelativePath.Contains(Search, StringComparison.OrdinalIgnoreCase) || a.Identifier.Contains(Search, StringComparison.OrdinalIgnoreCase) || a.DisplayTitle.Contains(Search, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (!Assets.SequenceEqual(filtered))
        {
            _refreshingAssets = true;
            try { Assets.Clear(); foreach (var row in filtered) Assets.Add(row); }
            finally { _refreshingAssets = false; }
        }
        _applyingFilter = true;
        try { SelectedAsset = selected is not null && filtered.Contains(selected) ? selected : filtered.FirstOrDefault(); }
        finally { _applyingFilter = false; }
        Raise(nameof(SelectedAsset)); Raise(nameof(FileSummary)); Raise(nameof(VisibleCheckState)); Raise(nameof(SelectionPosition)); RefreshCommands();
    }
    private void SetChecked(IEnumerable<AssetRow> rows, bool value)
    {
        _updatingChecks = true;
        try { foreach (var row in rows) row.IsChecked = value; }
        finally { _updatingChecks = false; }
        RefreshSummary();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AssetRow.IsChecked)) { if (!_updatingChecks) RefreshSummary(); }
        else if (args.PropertyName is nameof(AssetRow.Review) or nameof(AssetRow.HasUnsavedEdits))
        { RefreshSummary(); if (sender == SelectedAsset) RefreshSelection(); }
    }
    private void ClearPlan()
    {
        PlanOperations.Clear(); PlanIssues = ""; PlanStatus = "仅已审核的文件可生成整理预览。"; Raise(nameof(HasPlan));
    }
    private void RefreshSelection()
    {
        foreach (var property in new[] { nameof(HasSelection), nameof(HasCandidates), nameof(HasEvidence), nameof(SelectedFilename), nameof(SelectedDisplayTitle), nameof(SelectedMetadataSummary), nameof(SelectedEvidenceSummary), nameof(SelectedPath), nameof(SelectedSidecars), nameof(SelectedSidecarCount), nameof(SelectedWarnings), nameof(SelectedCandidateProvenance), nameof(SelectedCandidateUrl), nameof(SelectedCatalogIdentifier), nameof(SelectedSourceId), nameof(SelectedCandidatePosition), nameof(SelectedCandidateReviewReasons), nameof(HasCandidateReviewReasons), nameof(HasUnsavedEdits), nameof(SelectionPosition), nameof(ReviewQueueSummary), nameof(CanConfirmCurrent) }) Raise(property);
        RefreshCommands();
    }
    private void RefreshSummary()
    {
        while (_persistenceWarnings.TryDequeue(out var warning)) { IndexStatus = "旧审核清除失败 · 请查看日志"; AddLog("警告", warning); }
        foreach (var property in new[] { nameof(FileSummary), nameof(ReviewSummary), nameof(ScanSummary), nameof(IsEmpty), nameof(AllCount), nameof(PendingCount), nameof(ReviewedCount), nameof(NeedsAttentionCount), nameof(CheckedCount), nameof(CheckedSummary), nameof(HasCheckedAssets), nameof(BatchWarning), nameof(VisibleCheckState), nameof(IsReviewing), nameof(ReviewQueueSummary), nameof(CanConfirmCurrent) }) Raise(property);
        RefreshCommands();
    }
    private void RefreshCommands()
    {
        ScanCommand.Refresh(); MatchCommand.Refresh(); MatchCheckedCommand.Refresh(); AcceptCandidateCommand.Refresh(); SaveReviewCommand.Refresh(); ConfirmAndNextCommand.Refresh(); BuildPlanCommand.Refresh(); CancelCommand.Refresh(); UndoEditsCommand.Refresh(); StartReviewCommand.Refresh(); EndReviewCommand.Refresh(); PreviousAssetCommand.Refresh(); NextAssetCommand.Refresh();
    }
    private void AddLog(string level, string message)
    {
        Logs.Insert(0, new(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), level, message));
        if (Logs.Count > 250) Logs.RemoveAt(Logs.Count - 1);
    }
    public void Dispose()
    {
        if (_disposed) return;
        CancelCurrent(false); _disposed = true;
        foreach (var row in _allAssets) row.PropertyChanged -= OnRowChanged;
        // Both tasks are context-free. A committed write remains tracked until its UI
        // continuation accepts it, so closing cannot strand a cancelled review in SQLite.
        var flush = Task.WhenAll(_pendingReviewWrites.Values.Select(CleanupReviewWriteAsync).Append(_pendingInvalidations));
        if (flush.Wait(TimeSpan.FromSeconds(5))) _store?.Dispose();
        else _ = DisposeStoreAfterWritesAsync(flush);
    }
    private async Task DisposeStoreAfterWritesAsync(Task flush)
    { try { await flush.ConfigureAwait(false); } finally { _store?.Dispose(); } }
}

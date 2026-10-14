using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Organizer.Desktop.ViewModels;

namespace Organizer.Desktop.Diagnostics;

/// <summary>Executable MVVM interaction checks using only temporary synthetic media and offline fixtures.</summary>
internal static class WorkbenchSmoke
{
    public static async Task<int> RunAsync(StartupOptions options)
    {
        var root = Path.Combine(Path.GetTempPath(), $"mdcx-ui-smoke-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(source);
        options = options with { IndexPath = Path.Combine(root, "index.db") };
        var assertions = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            Console.WriteLine($"PASS {++assertions}: {message}");
        }
        try
        {
            StartupOptionsSmoke.Run(root, Check);
            await File.WriteAllTextAsync(Path.Combine(source, "DEMO-001.mkv"), "Synthetic media fixture. Not playable.");
            await File.WriteAllTextAsync(Path.Combine(source, "DEMO-001.zh-CN.srt"), "Synthetic subtitle fixture.");
            await File.WriteAllTextAsync(Path.Combine(source, "DEMO-002.mp4"), "Second synthetic media fixture.");
            var before = Directory.GetFiles(source).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllText);
            using (var vm = new WorkbenchViewModel(options with { DemoPath = source }))
            {
                await vm.InitializeAsync();
                Check(vm.Assets.Count == 2 && !vm.IsBusy, "Scan populates media and restores idle state");
                vm.SelectedAsset = vm.Assets.First(a => a.Identifier == "DEMO-001");
                Check(vm.SelectedAsset.Asset.Sidecars.Count == 1, "Sidecar stays attached to its media");
                var firstMatch = vm.MatchAsync();
                await vm.MatchAsync();
                await firstMatch;
                Check(vm.Candidates.Count == 1 && !vm.IsBusy, "Repeated match does not duplicate candidates");
                await vm.AcceptCandidateAsync();
                Check(vm.SelectedAsset.Review?.Title == "The Lantern Map", "Explicit acceptance saves reviewed metadata");
                Check(vm.SelectedAsset.Review is { Origin: "reviewed-candidate", Locked: true } && vm.SelectedAsset.Review.Provenance.Count > 0, "Candidate evidence and review lock are persisted");
                Check(vm.Candidates.Count == 1, "Review preserves candidate evidence for inspection");
                vm.TargetPath = target;
                await vm.BuildPlanAsync();
                Check(vm.PlanOperations.Count == 2 && !vm.HasPlanIssues, "Reviewed media and sidecar produce a safe dry-run plan");
                Check(!Directory.Exists(target), "Dry-run never creates the target directory");
                vm.EditorTitle = "Corrected Lantern Map";
                Check(vm.SelectedAsset.Review is null && !vm.HasPlan, "Editing invalidates accepted metadata and previous plan");
                vm.EditorYear = "not a year";
                await vm.SaveReviewAsync();
                Check(vm.SelectedAsset.Review is null && !vm.IsBusy, "Invalid manual year cannot be accepted");
                vm.EditorYear = "2024";
                await vm.SaveReviewAsync();
                Check(vm.SelectedAsset.Review is { Origin: "manual", Locked: true }, "Manual correction records manual provenance");
                await vm.ScanAsync();
                Check(vm.Assets.First(a => a.Identifier == "DEMO-001").Review?.Title == "Corrected Lantern Map", "Valid reviews restore from SQLite after rescan");
                vm.SelectedFilter = "已审核";
                Check(vm.Assets.Count == 1, "Review status filter displays only accepted files");
                vm.Search = "no matching file";
                Check(vm.Assets.Count == 0 && vm.SelectedAsset is null, "Search clears hidden selection");
                vm.ClearFilterCommand.Execute(null);
                Check(vm.Assets.Count == 2, "Clearing filters restores all files");
            }
            var batchSource = Path.Combine(root, "batch-source");
            Directory.CreateDirectory(batchSource);
            foreach (var name in new[] { "DEMO-001.mkv", "DEMO-002.1080p.mp4", "DEMO-002.2160p.mp4", "旅行日记.mkv" })
                await File.WriteAllTextAsync(Path.Combine(batchSource, name), "Synthetic batch fixture. Not playable.");
            await File.WriteAllTextAsync(Path.Combine(batchSource, "DEMO-001.zh-Hans.srt"), "Synthetic grouped subtitle.");
            using (var vm = new WorkbenchViewModel(options with { DemoPath = batchSource }))
            {
                await vm.InitializeAsync();
                var first = vm.Assets.First(a => a.Filename == "DEMO-001.mkv");
                var second = vm.Assets.First(a => a.Filename == "DEMO-002.1080p.mp4");
                var third = vm.Assets.First(a => a.Filename == "DEMO-002.2160p.mp4");
                first.IsChecked = true; second.IsChecked = true;
                vm.SelectedAsset = third;
                Check(vm.CheckedCount == 2 && !third.IsChecked, "Focused row and checked batch are independent");
                vm.Search = "2160p";
                Check(vm.Assets.Count == 1 && vm.CheckedCount == 2, "Filtering preserves hidden checked rows");
                vm.ClearFilterCommand.Execute(null);
                vm.SelectedAsset = first;
                await vm.MatchCheckedAsync();
                Check(first.CachedCandidates.Count == 1 && second.CachedCandidates.Count == 1 && vm.Assets.All(a => a.Review is null),
                    "Batch identification caches candidates without accepting any row");
                vm.SelectedAsset = second;
                vm.SelectedAsset = first;
                Check(vm.Candidates.Count == 1 && vm.Candidates[0].Candidate.CatalogIdentifier?.Namespace == "xcity:hinban",
                    "Refocusing restores candidate evidence without a new request");
                await vm.ScanAsync();
                first = vm.Assets.First(a => a.Filename == "DEMO-001.mkv");
                second = vm.Assets.First(a => a.Filename == "DEMO-002.1080p.mp4");
                Check(vm.CheckedCount == 2 && first.CachedCandidates.Count == 1 && second.CachedCandidates.Count == 1,
                    "Unchanged rescan preserves checked rows and cached candidates");
                vm.SelectedAsset = first;
                Check(vm.DraftOperations.Count == 2 && vm.PlanOperations.Count == 0 && first.Review is null,
                    "Unreviewed naming draft groups video and subtitle outside the reviewed plan");
                vm.StartReview();
                Check(vm.IsReviewing && vm.SelectedAsset == first && first.Review is null && second.Review is null,
                    "Sequential review starts without implicit acceptance");
                var outsideQueue = vm.Assets.First(a => a.Filename == "DEMO-002.2160p.mp4");
                vm.SelectedAsset = outsideQueue;
                vm.EditorTitle = "Manual draft outside checked review queue";
                await vm.ConfirmAndNextAsync();
                Check(outsideQueue.Review is null && vm.SelectedAsset == outsideQueue && vm.IsReviewing,
                    "Focus outside the checked review queue cannot be accepted implicitly");
                vm.SelectedAsset = first;
                await vm.ConfirmAndNextAsync();
                Check(first.Review is { Origin: "reviewed-candidate" } && second.Review is null && vm.SelectedAsset == second,
                    "Confirm and next accepts only the current row");
                await vm.BuildPlanAsync();
                Check(vm.PlanOperations.Count == 2 && vm.PlanOperations.All(p => !p.Source.Contains("DEMO-002", StringComparison.Ordinal)),
                    "Reviewed-only preview excludes the next unreviewed draft");
                vm.EditorTitle = "A deliberate manual correction";
                vm.EditorYear = "invalid";
                await vm.ConfirmAndNextAsync();
                Check(vm.SelectedAsset == second && second.Review is null && vm.IsReviewing,
                    "Invalid edits prevent confirmation and retain review position");
                vm.EditorYear = "2023";
                await vm.ConfirmAndNextAsync();
                Check(second.Review is { Origin: "manual", Title: "A deliberate manual correction" },
                    "Sequential confirmation preserves manual candidate overrides");
                vm.EndReview();
                vm.SelectedAsset = second;
                await vm.MatchAsync();
                Check(second.Review?.Title == "A deliberate manual correction" && vm.EditorTitle == "A deliberate manual correction",
                    "Rematching cannot overwrite locked manual review fields");
                await vm.MatchCheckedAsync();
                Check(second.Review?.Title == "A deliberate manual correction" && first.Review is not null,
                    "Batch rematching preserves previously reviewed good metadata");
                vm.SelectedAsset = first;
                var acceptedTitle = first.Review!.Title;
                vm.EditorTitle = "Unsaved edit";
                Check(first.HasUnsavedEdits && first.Review is null && !vm.HasPlan,
                    "Editing withdraws accepted state and invalidates previous plan");
                await vm.UndoEditsAsync();
                Check(!first.HasUnsavedEdits && first.Review?.Title == acceptedTitle && vm.EditorTitle == acceptedTitle,
                    "Undo restores unchanged reviewed fields and acceptance explicitly");
                vm.EditorTitle = "Dirty draft survives same-session rescan";
                await vm.ScanAsync();
                first = vm.Assets.First(a => a.Filename == "DEMO-001.mkv");
                vm.SelectedAsset = first;
                Check(first.Review is null && first.HasUnsavedEdits && vm.EditorTitle == "Dirty draft survives same-session rescan",
                    "Rescan does not resurrect an edited persisted review");
                await vm.UndoEditsAsync();
                vm.ClearCheckedCommand.Execute(null);
                var titleRow = vm.Assets.First(a => a.Filename == "旅行日记.mkv");
                vm.SelectedAsset = titleRow;
                vm.SelectedSearchMode = "标题";
                vm.SelectedMatchSource = "TPDB（影片）";
                vm.MatchQuery = "Harbor of Stars";
                await vm.MatchAsync();
                Check(vm.Candidates.Count == 1 && vm.Candidates[0].Candidate.SourceId == "harbor-of-stars" &&
                    vm.Candidates[0].Candidate.CatalogIdentifier is null && titleRow.Review is null,
                    "Title-provider search keeps TPDB source identity separate from catalog identity");
                vm.MatchQuery = "The Last Beacon";
                Check(vm.Candidates.Count == 0 && vm.SelectedCandidate is null && titleRow.CachedCandidates.Count == 0,
                    "Changing the query immediately invalidates earlier candidate selection");
                await vm.MatchAsync();
                Check(vm.Candidates.Count == 2 && vm.Candidates.All(c => c.Candidate.RequiresReview) && vm.SelectedCandidate is null,
                    "Ambiguous title candidates require an explicit candidate selection");
                titleRow.IsChecked = true;
                vm.StartReview();
                await vm.ConfirmAndNextAsync();
                Check(titleRow.Review is null && vm.SelectedAsset == titleRow,
                    "Sequential review never accepts the first ambiguous candidate implicitly");
                vm.EndReview();
                Check(titleRow.Review is null && !vm.IsReviewing, "Leaving review does not accept remaining rows");
                vm.SelectedSearchMode = "番号";
                Check(vm.Candidates.Count == 0 && titleRow.CachedCandidates.Count == 0,
                    "Changing search mode invalidates title-only candidate evidence");
                vm.SelectedAsset = first;
                first.IsChecked = true;
                await File.AppendAllTextAsync(first.Asset.SourcePath, " Source changed.");
                await vm.ScanAsync();
                first = vm.Assets.First(a => a.Filename == "DEMO-001.mkv");
                Check(!first.IsChecked && first.CachedCandidates.Count == 0 && first.Review is null,
                    "Changed fingerprint clears selection cache and accepted metadata");
                Check(!Directory.Exists(vm.TargetPath), "Draft and sequential review never create target directories");
            }
            using (var vm = new WorkbenchViewModel(options with { DemoPath = batchSource }))
            {
                await vm.InitializeAsync();
                var second = vm.Assets.First(a => a.Filename == "DEMO-002.1080p.mp4");
                vm.SelectedAsset = second;
                vm.EditorTitle = "Withdraw this review before closing";
            }
            using (var vm = new WorkbenchViewModel(options with { DemoPath = batchSource }))
            {
                await vm.InitializeAsync();
                Check(vm.Assets.First(a => a.Filename == "DEMO-002.1080p.mp4").Review is null,
                    "Closing after an edit cannot revive the withdrawn review on restart");
            }
            var largeSource = Path.Combine(root, "large-queue");
            Directory.CreateDirectory(largeSource);
            for (var index = 1; index <= 60; index++)
                await File.WriteAllTextAsync(Path.Combine(largeSource, $"DEMO-{index:D3}.mkv"), "Synthetic large queue fixture.");
            using (var vm = new WorkbenchViewModel(options with { DemoPath = largeSource }))
            {
                await vm.InitializeAsync();
                vm.SelectVisibleCommand.Execute(null);
                Check(vm.CheckedCount == 60 && vm.Assets.Count == 60, "Large queue selection counts every visible row");
                await vm.MatchCheckedAsync();
                Check(!vm.IsBusy && vm.Assets.All(a => a.CachedCandidates.Count == 0 && a.Review is null),
                    "Over-limit batch is rejected without partial matching or acceptance");
                vm.Search = "DEMO-001";
                vm.ClearCheckedCommand.Execute(null);
                Check(vm.CheckedCount == 0, "Clear batch selection also clears hidden rows");
            }
            var slowWorker = Path.Combine(root, "slow_worker.py");
            var realWorker = options.ResolveWorkerPath();
            await File.WriteAllTextAsync(slowWorker,
                $"import sys, time, runpy\nsys.path.insert(0, {JsonSerializer.Serialize(Path.GetDirectoryName(realWorker))})\ntime.sleep(0.5)\nrunpy.run_path({JsonSerializer.Serialize(realWorker)}, run_name='__main__')\n");
            using (var vm = new WorkbenchViewModel(options with { DemoPath = source, WorkerPath = slowWorker }))
            {
                await vm.InitializeAsync();
                var first = vm.Assets.First(a => a.Identifier == "DEMO-001");
                var second = vm.Assets.First(a => a.Identifier == "DEMO-002");
                vm.SelectedAsset = first;
                var oldMatch = vm.MatchAsync();
                Check(vm.IsBusy, "Slow matching sets busy immediately");
                vm.SelectedAsset = second;
                Check(!vm.IsBusy && vm.Candidates.Count == 0, "Changing selection cancels the previous match");
                var newMatch = vm.MatchAsync();
                await Task.WhenAll(oldMatch, newMatch);
                Check(vm.SelectedAsset == second && vm.Candidates.All(c => c.Candidate.Id == "DEMO-002") && vm.Candidates.Count > 0,
                    "Late results never overwrite a newer selection");
                var cancelled = vm.MatchAsync();
                vm.Cancel();
                await cancelled;
                Check(!vm.IsBusy && vm.Candidates.Count == 0, "Cancel discards pending candidates and re-enables actions");
                await vm.MatchAsync();
                Check(!vm.IsBusy && vm.Candidates.Count > 0, "Matching can restart after cancellation");
                var stale = vm.MatchAsync();
                vm.SourcePath = Path.Combine(root, "absent");
                await stale;
                Check(vm.Assets.Count == 0 && vm.SelectedAsset is null && vm.Candidates.Count == 0,
                    "Changing source invalidates assets and pending responses");
                await vm.ScanAsync();
                Check(!vm.IsBusy, "Scan failure always returns to idle state");
            }
            using (var vm = new WorkbenchViewModel(options with { DemoPath = batchSource, WorkerPath = slowWorker }))
            {
                await vm.InitializeAsync();
                var first = vm.Assets.First(a => a.Filename == "DEMO-001.mkv");
                var second = vm.Assets.First(a => a.Filename == "DEMO-002.1080p.mp4");
                var other = vm.Assets.First(a => a.Filename == "旅行日记.mkv");
                first.IsChecked = true; second.IsChecked = true;
                var batch = vm.MatchCheckedAsync();
                await vm.MatchCheckedAsync();
                vm.SelectedAsset = other;
                await batch;
                Check(vm.SelectedAsset == other && vm.Candidates.Count == 0 && first.CachedCandidates.Count == 1 && second.CachedCandidates.Count == 1,
                    "Batch results stay on their checked rows while focus changes");
                Check(first.Review is null && second.Review is null, "Repeated batch action never confirms metadata");
                var cancelledBatch = vm.MatchCheckedAsync();
                vm.Cancel();
                await cancelledBatch;
                Check(!vm.IsBusy && first.Review is null && second.Review is null,
                    "Cancelling a repeated batch returns idle without accepting rows");
                var staleBatch = vm.MatchCheckedAsync();
                vm.SourcePath = Path.Combine(root, "new-source-selection");
                await staleBatch;
                Check(vm.Assets.Count == 0 && vm.CheckedCount == 0 && vm.Candidates.Count == 0,
                    "Source switch fences every pending batch result");
            }
            var emptySource = Path.Combine(root, "empty-source");
            Directory.CreateDirectory(emptySource);
            using (var vm = new WorkbenchViewModel(options with { DemoPath = emptySource }))
            {
                await vm.InitializeAsync();
                Check(vm.IsEmpty && vm.SelectedAsset is null && vm.CheckedCount == 0 && !vm.IsBusy,
                    "Empty scan has no hidden focus or checked selection");
                await vm.MatchCheckedAsync(); vm.StartReview(); await vm.ConfirmAndNextAsync(); await vm.BuildPlanAsync();
                Check(vm.PlanOperations.Count == 0 && !vm.IsReviewing && !vm.IsBusy,
                    "Repeated empty-state actions remain safe no-ops");
            }
            var sidecarSource = Path.Combine(root, "sidecar-review");
            Directory.CreateDirectory(sidecarSource);
            await File.WriteAllTextAsync(Path.Combine(sidecarSource, "DEMO-001.mkv"), "Synthetic sidecar review fixture.");
            var changedSubtitle = Path.Combine(sidecarSource, "DEMO-001.zh.srt");
            await File.WriteAllTextAsync(changedSubtitle, "Original synthetic subtitle.");
            using (var vm = new WorkbenchViewModel(options with { DemoPath = sidecarSource }))
            {
                await vm.InitializeAsync();
                await vm.MatchAsync();
                await vm.AcceptCandidateAsync();
                Check(vm.SelectedAsset?.Review is not null, "Sidecar-group review is initially accepted explicitly");
                vm.EditorTitle = "Pending sidecar-group edit";
                await File.AppendAllTextAsync(changedSubtitle, " A changed subtitle snapshot.");
                await vm.UndoEditsAsync();
                Check(vm.SelectedAsset?.Review is null && vm.PlanOperations.Count == 0,
                    "Undo cannot restore acceptance after a subtitle-only change");
            }
            using (var vm = new WorkbenchViewModel(options with { DemoPath = sidecarSource }))
            {
                await vm.InitializeAsync();
                Check(vm.SelectedAsset?.Review is null, "Subtitle-only changes stay unreviewed after restart");
            }
            var legacySource = Path.Combine(root, "legacy-review");
            Directory.CreateDirectory(legacySource);
            await File.WriteAllTextAsync(Path.Combine(legacySource, "DEMO-001.mkv"), "Synthetic legacy review fixture.");
            var legacyScan = await new Organizer.Core.MediaScanner().ScanAsync(legacySource);
            using (var store = new Organizer.Infrastructure.IndexStore(options.IndexPath!))
            {
                await store.SaveScanAsync(legacyScan);
                await store.SaveReviewAsync(legacyScan.Assets[0].Id, new("DEMO-001", "Legacy unstamped review", 2024), legacyScan.Assets[0].Fingerprint);
            }
            using (var vm = new WorkbenchViewModel(options with { DemoPath = legacySource }))
            {
                await vm.InitializeAsync();
                Check(vm.SelectedAsset?.Review is null && vm.Logs.Any(entry => entry.Message.Contains("旧版审核", StringComparison.Ordinal)),
                    "Legacy unstamped reviews visibly require rechecking instead of silent migration");
            }
            var evidenceSource = Path.Combine(root, "persisted-evidence");
            Directory.CreateDirectory(evidenceSource);
            await File.WriteAllTextAsync(Path.Combine(evidenceSource, "DEMO-001.mkv"), "Synthetic persisted evidence fixture.");
            using (var vm = new WorkbenchViewModel(options with { DemoPath = evidenceSource }))
            {
                await vm.InitializeAsync();
                await vm.MatchAsync();
                await vm.AcceptCandidateAsync();
            }
            using (var vm = new WorkbenchViewModel(options with { DemoPath = evidenceSource }))
            {
                await vm.InitializeAsync();
                Check(vm.Candidates.Count == 0 && vm.SelectedSourceId == "1001" && vm.SelectedCatalogIdentifier.Contains("xcity:hinban", StringComparison.Ordinal),
                    "Restart restores typed source evidence without inventing cached candidates");
                vm.EditorIdentifier = "MANUAL-777";
                await vm.SaveReviewAsync();
                Check(vm.SelectedAsset?.Review?.Identifier == "MANUAL-777" && vm.SelectedCatalogIdentifier.Contains("DEMO-001", StringComparison.Ordinal) &&
                    vm.SelectedCatalogIdentifier.Contains("原来源", StringComparison.Ordinal),
                    "Manual sorting identifiers stay distinct from retained original source catalog evidence");
            }
            var staleSource = Path.Combine(root, "stale-source");
            Directory.CreateDirectory(staleSource);
            var staleMedia = Path.Combine(staleSource, "DEMO-001.mkv");
            await File.WriteAllTextAsync(staleMedia, "Original synthetic snapshot.");
            await File.WriteAllTextAsync(Path.Combine(staleSource, "DEMO-002.mkv"), "Another synthetic queue item.");
            using (var older = new WorkbenchViewModel(options with { DemoPath = staleSource }))
            using (var newer = new WorkbenchViewModel(options with { DemoPath = staleSource }))
            {
                await older.InitializeAsync();
                await File.WriteAllTextAsync(staleMedia, "A different synthetic snapshot with a different size.");
                await newer.InitializeAsync();
                older.EditorTitle = "Review from the older scan";
                await older.SaveReviewAsync();
                Check(older.SelectedAsset?.Review is null && !older.HasPlan, "Stale index rejection cannot create a locally accepted review");
                var failedRow = older.SelectedAsset;
                foreach (var row in older.Assets) row.IsChecked = true;
                older.StartReview();
                await older.ConfirmAndNextAsync();
                Check(older.IsReviewing && older.SelectedAsset == failedRow && older.Assets.All(a => a.Review is null),
                    "Failed stale confirmation cannot advance the sequential review queue");
            }
            await CheckCloseAfterCommittedSaveAsync(options, root, Check);
            Check(Directory.GetFiles(source).Length == before.Count && before.All(pair => File.ReadAllText(Path.Combine(source, pair.Key!)) == pair.Value),
                "All synthetic source files remain byte-for-byte unchanged");
            Console.WriteLine($"UI SMOKE PASSED: {assertions} checks");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine($"UI SMOKE FAILED: {ex}"); return 1; }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    private static async Task CheckCloseAfterCommittedSaveAsync(StartupOptions options, string root, Action<bool, string> check)
    {
        var source = Path.Combine(root, "close-after-commit");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "DEMO-001.mkv"), "Synthetic close race fixture.");
        var database = Path.Combine(root, "close-race.db");
        using var vm = new WorkbenchViewModel(options with { DemoPath = source, IndexPath = database });
        await vm.InitializeAsync();
        vm.EditorTitle = "Explicit review whose UI continuation has not run";
        using var context = new QueuedContext();
        var previous = SynchronizationContext.Current;
        Task pending;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            pending = vm.SaveReviewAsync();
            if (!context.WaitAndRunOne(5000)) throw new InvalidOperationException("Expected initial task-record continuation.");
            var timeout = Stopwatch.StartNew();
            var committed = false;
            while (timeout.Elapsed < TimeSpan.FromSeconds(5))
            {
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database}");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM reviews";
                if (Convert.ToInt32(command.ExecuteScalar()) == 1) { committed = true; break; }
                Thread.Sleep(10);
            }
            check(committed && vm.SelectedAsset?.Review is null, "Close-race barrier reaches database commit before UI applies acceptance");
            vm.Dispose();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        var drain = Stopwatch.StartNew();
        while (!pending.IsCompleted && drain.Elapsed < TimeSpan.FromSeconds(5)) context.WaitAndRunOne(100);
        if (!pending.IsCompleted) throw new InvalidOperationException("Cancelled review did not finish after close.");
        await pending;
        using var store = new Organizer.Infrastructure.IndexStore(database);
        check((await store.LoadReviewsAsync()).Count == 0, "Closing before UI acceptance removes the committed-but-unapplied review");
    }

    private sealed class QueuedContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Add((callback, state));
        public bool WaitAndRunOne(int timeoutMilliseconds)
        {
            if (!_queue.TryTake(out var item, timeoutMilliseconds)) return false;
            var prior = Current;
            SetSynchronizationContext(this);
            try { item.Callback(item.State); }
            finally { SetSynchronizationContext(prior); }
            return true;
        }
        public void Dispose() => _queue.Dispose();
    }
}

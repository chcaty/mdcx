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
            var staleSource = Path.Combine(root, "stale-source");
            Directory.CreateDirectory(staleSource);
            var staleMedia = Path.Combine(staleSource, "DEMO-001.mkv");
            await File.WriteAllTextAsync(staleMedia, "Original synthetic snapshot.");
            using (var older = new WorkbenchViewModel(options with { DemoPath = staleSource }))
            using (var newer = new WorkbenchViewModel(options with { DemoPath = staleSource }))
            {
                await older.InitializeAsync();
                await File.WriteAllTextAsync(staleMedia, "A different synthetic snapshot with a different size.");
                await newer.InitializeAsync();
                older.EditorTitle = "Review from the older scan";
                await older.SaveReviewAsync();
                Check(older.SelectedAsset?.Review is null && !older.HasPlan, "Stale index rejection cannot create a locally accepted review");
            }
            Check(Directory.GetFiles(source).Length == before.Count && before.All(pair => File.ReadAllText(Path.Combine(source, pair.Key!)) == pair.Value),
                "All synthetic source files remain byte-for-byte unchanged");
            Console.WriteLine($"UI SMOKE PASSED: {assertions} checks");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine($"UI SMOKE FAILED: {ex}"); return 1; }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
}

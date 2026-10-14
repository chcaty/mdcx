using System.Text;
using Microsoft.Data.Sqlite;
using Organizer.Core;
using Organizer.Infrastructure;

var python = Environment.GetEnvironmentVariable("ORGANIZER_PYTHON") ?? (OperatingSystem.IsWindows() ? "python" : "python3");
var checks = new List<(string Name, Func<Task> Run)>();
void Test(string name, Action action) => checks.Add((name, () => { action(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> action) => checks.Add((name, action));
void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new InvalidOperationException(message); }
var sandbox = Path.Combine(Path.GetTempPath(), "organizer-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sandbox);
string Dir(string name) { var path = Path.Combine(sandbox, name); Directory.CreateDirectory(path); return path; }
string FileAt(string dir, string name, string content = "fixture") { var path = Path.Combine(dir, name); File.WriteAllText(path, content); return path; }
var scanner = new MediaScanner();
var planner = new PathPlanner();
ReviewedMetadata Review(string id = "DEMO-001", string title = "The Lantern Map") => new(id, title, 2024);
Task<ScanResult> Scan(string name, params string[] files)
{
    var dir = Dir(name); foreach (var file in files) FileAt(dir, file); return scanner.ScanAsync(dir);
}

Test("identifier case, Unicode compatibility and ambiguity", () =>
{
    Assert(MediaScanner.ExtractIdentifier("ＤＥＭＯ－００１ 1080p") == "DEMO-001");
    Assert(MediaScanner.ExtractIdentifier("demo_001.2160p") == "DEMO-001");
    Assert(MediaScanner.ExtractIdentifier("DEMO-001 SAMPLE-002") is null);
    Assert(MediaScanner.ExtractIdentifier("旅行日记") is null);
});
AsyncTest("recursive read-only scan + language sidecars", async () =>
{
    var dir = Dir("scan/nested"); FileAt(dir, "DEMO-001.mkv"); FileAt(dir, "DEMO-001.zh-Hans.forced.srt"); FileAt(dir, "poster.jpg");
    var result = await scanner.ScanAsync(Path.GetDirectoryName(dir)!);
    Assert(result.Assets.Count == 1 && result.Assets[0].Sidecars.Single().Suffix == ".zh-Hans.forced.srt");
    Assert(result.Warnings.Any(w => w.Contains("poster.jpg")));
    Assert(File.ReadAllText(result.Assets[0].SourcePath) == "fixture");
});
AsyncTest("same identifier retains versions and possible duplicates", async () =>
{
    var scan = await Scan("versions", "DEMO-001.1080p.mkv", "DEMO-001.2160p.mkv", "DEMO-001.copy.mp4");
    Assert(scan.Assets.Count == 3 && scan.Assets.All(a => a.Identifier == "DEMO-001"));
});
AsyncTest("ambiguous sidecars remain unassigned", async () =>
{
    var scan = await Scan("sidecar-ambiguity", "DEMO-001.mkv", "DEMO-001.mp4", "DEMO-001.srt");
    Assert(scan.Assets.All(a => a.Sidecars.Count == 0 && a.Warnings.Count == 1));
    Assert(!planner.CreatePlan(Dir("sidecar-target"), [(scan.Assets[0], Review())]).IsSafe);
});
AsyncTest("scanner cancellation is terminal and repeat scans stable", async () =>
{
    var scan = await Scan("repeat", "DEMO-001.mkv");
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    try { await scanner.ScanAsync(scan.Root, cancel.Token); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
    var next = await scanner.ScanAsync(scan.Root);
    Assert(scan.Assets[0].Id == next.Assets[0].Id && scan.Assets[0].Fingerprint == next.Assets[0].Fingerprint);
});
AsyncTest("symlink scanner never follows external files", async () =>
{
    if (OperatingSystem.IsWindows()) return;
    var outside = Dir("outside"); FileAt(outside, "DEMO-001.mkv");
    var root = Dir("links"); Directory.CreateSymbolicLink(Path.Combine(root, "escape"), outside);
    var scan = await scanner.ScanAsync(root);
    Assert(scan.Assets.Count == 0 && scan.Warnings.Any(w => w.Contains("Skipped link")));
    try { await scanner.ScanAsync(Path.Combine(root, "escape")); throw new Exception("Expected root rejection"); } catch (IOException) { }
});
AsyncTest("dry-run deterministic + subtitles + no mutation", async () =>
{
    var scan = await Scan("preview", "DEMO-001.mkv", "DEMO-001.zh.srt");
    var target = Path.Combine(sandbox, "not-created");
    var plan = planner.CreatePlan(target, [(scan.Assets[0], Review())]);
    Assert(plan.IsSafe && plan.DryRun && plan.Operations.Count == 2);
    Assert(plan.Id == planner.CreatePlan(target, [(scan.Assets[0], Review())]).Id);
    Assert(!Directory.Exists(target) && File.Exists(scan.Assets[0].SourcePath));
    Assert(plan.Operations[1].Target.EndsWith(".zh.srt", StringComparison.Ordinal));
});
AsyncTest("manual correction resolves missing filename identity", async () =>
{
    var scan = await Scan("manual", "旅行日记.mkv");
    Assert(planner.CreatePlan(Dir("manual-target"), [(scan.Assets[0], Review())]).IsSafe);
});
AsyncTest("metadata traversal and absolute injection blocked", async () =>
{
    var scan = await Scan("traversal", "DEMO-001.mkv");
    foreach (var title in new[] { "../escape", "..\\escape", "/etc/passwd", ".", ".." })
        Assert(!planner.CreatePlan(Dir("traversal-target"), [(scan.Assets[0], Review(title: title))]).IsSafe, title);
    Assert(!planner.CreatePlan("relative-root", [(scan.Assets[0], Review())]).IsSafe);
});
AsyncTest("reserved Windows names and unsafe punctuation normalized", async () =>
{
    var scan = await Scan("reserved", "DEMO-001.mkv");
    var plan = planner.CreatePlan(Dir("reserved-target"), [(scan.Assets[0], Review("CON", "a:b?c"))]);
    Assert(plan.IsSafe && Path.GetFileName(plan.Operations[0].Target).StartsWith("_CON - a_b_c", StringComparison.Ordinal));
});
AsyncTest("conflicting versions require review, never overwrite", async () =>
{
    var scan = await Scan("conflict", "DEMO-001.1080p.mkv", "DEMO-001.2160p.mkv");
    var plan = planner.CreatePlan(Dir("conflict-target"), scan.Assets.Select(a => (a, Review())).ToArray());
    Assert(plan.Issues.Any(i => i.Code == "collision"));
});
AsyncTest("case and canonical Unicode collisions on all OSes", async () =>
{
    var scan = await Scan("unicode", "DEMO-001.mkv", "DEMO-002.mkv");
    var plan = planner.CreatePlan(Dir("unicode-target"), [(scan.Assets[0], Review("DEMO-001", "CAFÉ")), (scan.Assets[1], Review("demo-001", "cafe\u0301"))]);
    Assert(plan.Issues.Any(i => i.Code == "collision"));
});
AsyncTest("existing target matching different case is blocked", async () =>
{
    var scan = await Scan("existing", "DEMO-001.mkv"); var target = Dir("existing-target");
    var planned = planner.CreatePlan(target, [(scan.Assets[0], Review())]).Operations[0].Target;
    Directory.CreateDirectory(Path.GetDirectoryName(planned)!.ToLowerInvariant());
    File.WriteAllText(planned.ToLowerInvariant(), "keep");
    Assert(planner.CreatePlan(target, [(scan.Assets[0], Review())]).Issues.Any(i => i.Code == "existing-target"));
    Assert(File.ReadAllText(planned.ToLowerInvariant()) == "keep");
});
AsyncTest("file parent and linked target ancestors are blocked", async () =>
{
    var scan = await Scan("parents", "DEMO-001.mkv"); var target = Dir("parent-target");
    FileAt(target, "DEMO-001 - The Lantern Map (2024)");
    Assert(planner.CreatePlan(target, [(scan.Assets[0], Review())]).Issues.Any(i => i.Code == "unsafe-ancestor"));
    if (OperatingSystem.IsWindows()) return;
    var linkedRoot = Dir("linked-target"); Directory.CreateSymbolicLink(Path.Combine(linkedRoot, "DEMO-001 - THE LANTERN MAP (2024)"), Dir("linked-destination"));
    Assert(planner.CreatePlan(linkedRoot, [(scan.Assets[0], Review())]).Issues.Any(i => i.Code == "unsafe-ancestor"));
});
AsyncTest("source changes invalidate dry-run", async () =>
{
    var scan = await Scan("stale", "DEMO-001.mkv"); File.AppendAllText(scan.Assets[0].SourcePath, "changed");
    Assert(planner.CreatePlan(Dir("stale-target"), [(scan.Assets[0], Review())]).Issues.Any(i => i.Code == "stale-source"));
});
AsyncTest("repeated source selections are rejected", async () =>
{
    var scan = await Scan("repeated-plan", "DEMO-001.mkv");
    var plan = planner.CreatePlan(Dir("repeated-target"), [(scan.Assets[0], Review()), (scan.Assets[0], Review(title: "different"))]);
    Assert(plan.Issues.Any(i => i.Code == "repeated-source"));
});
AsyncTest("SQLite review persistence and stale review invalidation", async () =>
{
    var scan = await Scan("index-source", "DEMO-001.mkv"); var db = Path.Combine(Dir("index"), "index.db");
    using (var store = new IndexStore(db)) { await store.SaveScanAsync(scan); await store.SaveReviewAsync(scan.Assets[0].Id, Review(), scan.Assets[0].Fingerprint); }
    using (var store = new IndexStore(db))
    {
        Assert((await store.LoadReviewsAsync()).Count == 1);
        await store.SaveScanAsync(scan); Assert((await store.LoadReviewsAsync()).Count == 1);
        File.AppendAllText(scan.Assets[0].SourcePath, "changed"); await store.SaveScanAsync(await scanner.ScanAsync(scan.Root));
        Assert((await store.LoadReviewsAsync()).Count == 0);
        await store.RecordTaskAsync("task1", "scan", "completed", "fixture");
        var plan = planner.CreatePlan(Dir("db-target"), [(scan.Assets[0], Review())]); await store.SavePlanAsync(plan); await store.SavePlanAsync(plan);
    }
    using var connection = new SqliteConnection($"Data Source={db}"); connection.Open();
    using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM plans";
    Assert(Convert.ToInt32(command.ExecuteScalar()) == 1);
    command.CommandText = "SELECT COUNT(*) FROM tasks"; Assert(Convert.ToInt32(command.ExecuteScalar()) == 1);
    command.CommandText = "PRAGMA integrity_check"; Assert((string?)command.ExecuteScalar() == "ok");
});
AsyncTest("SQLite cancellation does not replace previous scan", async () =>
{
    var scan = await Scan("cancel-index", "DEMO-001.mkv"); using var store = new IndexStore(Path.Combine(Dir("cancel-db"), "index.db"));
    await store.SaveScanAsync(scan); await store.SaveReviewAsync(scan.Assets[0].Id, Review(), scan.Assets[0].Fingerprint);
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    try { await store.SaveScanAsync(new(scan.Root, [], []), cancel.Token); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
    Assert((await store.LoadReviewsAsync()).Count == 1);
});
AsyncTest("SQLite edit invalidation survives reopen and cannot erase newer review", async () =>
{
    var scan = await Scan("edit-index", "DEMO-001.mkv");
    var asset = scan.Assets[0];
    var db = Path.Combine(Dir("edit-db"), "index.db");
    var original = Review();
    var newer = Review(title: "Newer accepted title");
    using (var store = new IndexStore(db))
    {
        await store.SaveScanAsync(scan);
        await store.SaveReviewAsync(asset.Id, original, asset.Fingerprint);
        Assert(await store.InvalidateReviewAsync(asset.Id, asset.Fingerprint, original));
        Assert(!await store.InvalidateReviewAsync(asset.Id, asset.Fingerprint, original));
    }
    using (var store = new IndexStore(db))
    {
        Assert((await store.LoadReviewsAsync()).Count == 0);
        await store.SaveReviewAsync(asset.Id, newer, asset.Fingerprint);
        Assert(!await store.InvalidateReviewAsync(asset.Id, asset.Fingerprint, original));
        Assert((await store.LoadReviewsAsync())[asset.Id].Title == newer.Title);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await store.InvalidateReviewAsync(asset.Id, asset.Fingerprint, newer, cancel.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
        Assert((await store.LoadReviewsAsync())[asset.Id].Title == newer.Title);
        File.AppendAllText(asset.SourcePath, "different fingerprint");
        var changed = await scanner.ScanAsync(scan.Root);
        await store.SaveScanAsync(changed);
        await store.SaveReviewAsync(asset.Id, newer, changed.Assets[0].Fingerprint);
        Assert(!await store.InvalidateReviewAsync(asset.Id, asset.Fingerprint, newer));
        Assert((await store.LoadReviewsAsync())[asset.Id].Title == newer.Title);
    }
});
AsyncTest("review snapshot includes sidecars and rejects a stale sidecar-only save", async () =>
{
    var scan = await Scan("snapshot-source", "DEMO-001.mkv", "DEMO-001.zh.srt");
    var asset = scan.Assets[0];
    var stamp = MediaSnapshot.Fingerprint(asset);
    Assert(stamp == MediaSnapshot.Fingerprint(asset with { Sidecars = asset.Sidecars.Reverse().ToArray() }));
    var metadata = Review() with { Provenance = new Dictionary<string, string> { [MediaSnapshot.ProvenanceKey] = stamp } };
    using var store = new IndexStore(Path.Combine(Dir("snapshot-db"), "index.db"));
    await store.SaveScanAsync(scan);
    await store.SaveReviewAsync(asset.Id, metadata, asset.Fingerprint);
    var originalSubtitle = File.ReadAllText(asset.Sidecars[0].Path);
    var originalSubtitleTime = File.GetLastWriteTimeUtc(asset.Sidecars[0].Path);
    File.AppendAllText(asset.Sidecars[0].Path, "changed subtitle");
    var changed = await scanner.ScanAsync(scan.Root);
    Assert(changed.Assets[0].Fingerprint == asset.Fingerprint);
    Assert(MediaSnapshot.Fingerprint(changed.Assets[0]) != stamp);
    await store.SaveScanAsync(changed);
    Assert((await store.LoadReviewsAsync()).Count == 0, "Changed sidecar must not restore a stamped review");
    try { await store.SaveReviewAsync(asset.Id, metadata, asset.Fingerprint); throw new Exception("Expected group snapshot rejection"); }
    catch (InvalidOperationException ex) { Assert(ex.Message.Contains("media group changed", StringComparison.Ordinal)); }
    File.WriteAllText(asset.Sidecars[0].Path, originalSubtitle);
    File.SetLastWriteTimeUtc(asset.Sidecars[0].Path, originalSubtitleTime);
    var restored = await scanner.ScanAsync(scan.Root);
    Assert(MediaSnapshot.Fingerprint(restored.Assets[0]) == stamp);
    await store.SaveScanAsync(restored);
    Assert((await store.LoadReviewsAsync()).Count == 0, "Reappearing old sidecar snapshot cannot resurrect a withdrawn review");
});
Test("future SQLite schema rejected without destructive migration", () =>
{
    var db = Path.Combine(Dir("future-db"), "index.db");
    using (var c = new SqliteConnection($"Data Source={db}")) { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA user_version=99"; cmd.ExecuteNonQuery(); }
    try { using var store = new IndexStore(db); throw new Exception("Expected rejection"); } catch (InvalidOperationException e) { Assert(e.Message.Contains("newer")); }
});

string Worker(string name, string body) => FileAt(Dir("fake-workers"), name + ".py", "import sys,json,time\nr=json.loads(sys.stdin.readline())\n" + body);
AsyncTest("bridge drains noisy stderr and ignores stale events", async () =>
{
    var script = Worker("good", "sys.stderr.write('diagnostic'*50000);sys.stderr.flush()\nprint(json.dumps({'version':1,'requestId':'old','type':'candidate','candidate':{}}),flush=True)\nprint(json.dumps({'version':1,'requestId':r['requestId'],'type':'candidate','candidate':{'id':'DEMO-001','title':'Safe fixture','year':2024,'source':'xcity','sourceId':'1001','catalogIdentifier':{'namespace':'xcity:hinban','value':'DEMO-001'},'sourceUrl':'https://xcity.jp/avod/detail/?id=1001','confidence':1.0,'provenance':{'fixture':'true','identitySchema':'2'}}}),flush=True)\nprint(json.dumps({'version':1,'requestId':r['requestId'],'type':'completed'}),flush=True)\ntime.sleep(30)\n");
    var result = await new PythonScraperClient(python, script).MatchAsync("DEMO-001"); Assert(result.Count == 1 && result[0].Id == "DEMO-001");
});
AsyncTest("bridge malformed or oversized output bounded", async () =>
{
    foreach (var (name, body, expected) in new[] { ("malformed", "print('bad json',flush=True)\n", "protocol_invalid"), ("oversized", "print('a'*70000,flush=True)\n", "protocol_limit") })
    {
        try { await new PythonScraperClient(python, Worker(name, body)).MatchAsync("DEMO-001"); throw new Exception("Expected failure"); }
        catch (ScraperException e) { Assert(e.Code == expected, e.Code); }
    }
});
AsyncTest("bridge timeout terminates subprocess", async () =>
{
    var script = Worker("timeout", "time.sleep(30)\n");
    try { await new PythonScraperClient(python, script) { RequestTimeout = TimeSpan.FromMilliseconds(150) }.MatchAsync("DEMO-001"); throw new Exception("Expected timeout"); }
    catch (ScraperException e) { Assert(e.Code == "timeout"); }
});
AsyncTest("bridge caller cancellation preserved", async () =>
{
    using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
    try { await new PythonScraperClient(python, Worker("cancel", "time.sleep(30)\n")).MatchAsync("DEMO-001", cancel.Token); throw new Exception("Expected cancellation"); }
    catch (OperationCanceledException) { Assert(cancel.IsCancellationRequested); }
});
if (args.Length > 0)
{
    AsyncTest("offline provider-contract worker end-to-end", async () =>
    {
        var result = await new PythonScraperClient(python, args[0]).MatchAsync("DEMO-001");
        Assert(result.Any(c => c.Id == "DEMO-001" && c.Title.Length > 0));
        Assert(result[0].SourceId == "1001" && result[0].CatalogIdentifier is { Namespace: "xcity:hinban", Value: "DEMO-001" });
        var tpdb = await new PythonScraperClient(python, args[0]).MatchAsync("Harbor of Stars", sources: ["theporndb_movies"]);
        Assert(tpdb.Any(c => c.Source == "theporndb_movies" && c.Id == "theporndb_movies:harbor-of-stars" && c.SourceId == "harbor-of-stars" && c.CatalogIdentifier is null && c.RequiresReview));
        var ambiguous = await new PythonScraperClient(python, args[0]).MatchAsync("The Last Beacon", sources: ["theporndb_movies"]);
        Assert(ambiguous.Count == 2 && ambiguous.All(c => c.RequiresReview && c.ReviewReasons.Count > 0));
        Assert(result.All(c => c.Provenance.TryGetValue("fixture", out var fixture) && fixture == "true"));
    });
}
AsyncTest("sidecar portability and stale sidecar guard", async () =>
{
    var scan = await Scan("unsafe-sidecar", "DEMO-001.mkv");
    // Construct the invalid metadata explicitly; never create a Windows ADS even in tests.
    scan = scan with { Assets = [scan.Assets[0] with { Sidecars = [new SidecarFile(scan.Assets[0].SourcePath + ".en:comment.srt", ".en:comment.srt")] }] };
    Assert(planner.CreatePlan(Dir("unsafe-sidecar-target"), [(scan.Assets[0], Review())]).Issues.Any(i => i.Code == "unsafe-sidecar"));
    var clean = await Scan("stale-sidecar", "DEMO-001.mkv", "DEMO-001.en.srt");
    File.AppendAllText(clean.Assets[0].Sidecars[0].Path, "changed");
    Assert(planner.CreatePlan(Dir("stale-sidecar-target"), [(clean.Assets[0], Review())]).Issues.Any(i => i.Code == "stale-source"));
});
AsyncTest("component byte limits and truncation remain portable", async () =>
{
    var scan = await Scan("long-names", "DEMO-001.mkv");
    Assert(!planner.CreatePlan(Dir("long-name-target"), [(scan.Assets[0], Review(title: new string('旅', 90)))]).IsSafe);
    var plan = planner.CreatePlan(Dir("truncated-target"), [(scan.Assets[0], new ReviewedMetadata("DEMO-001", new string('x', 89) + ".z", null))]);
    Assert(plan.Operations.All(o => !Path.GetFileName(Path.GetDirectoryName(o.Target)!).EndsWith('.')));
    Assert(!planner.CreatePlan(Dir("empty-truncated-target"), [(scan.Assets[0], new ReviewedMetadata("DEMO-001", new string('.', 90) + "z", null))]).IsSafe);
});
AsyncTest("incomplete scan preserves human review", async () =>
{
    var scan = await Scan("partial-source", "DEMO-001.mkv");
    using var store = new IndexStore(Path.Combine(Dir("partial-db"), "index.db"));
    await store.SaveScanAsync(scan); await store.SaveReviewAsync(scan.Assets[0].Id, Review(), scan.Assets[0].Fingerprint);
    await store.SaveScanAsync(new ScanResult(scan.Root, [], ["Unreadable directory: transient failure"]) { IsComplete = false });
    Assert((await store.LoadReviewsAsync()).Count == 1);
    await store.SaveScanAsync(scan); Assert((await store.LoadReviewsAsync()).Count == 1);
    await store.SaveScanAsync(new(scan.Root, [], [])); Assert((await store.LoadReviewsAsync()).Count == 0);
});
AsyncTest("bridge source error allows valid fallback", async () =>
{
    var script = Worker("fallback", "print(json.dumps({'version':1,'requestId':r['requestId'],'type':'error','terminal':False,'error':{'code':'identity_mismatch','message':'Wrong ID'}}),flush=True)\nprint(json.dumps({'version':1,'requestId':r['requestId'],'type':'completed','status':'no_match'}),flush=True)\n");
    Assert((await new PythonScraperClient(python, script).MatchAsync("DEMO-001")).Count == 0);
    var terminal = Worker("terminal", "print(json.dumps({'version':1,'requestId':r['requestId'],'type':'error','terminal':True,'error':{'code':'invalid_request','message':'Bad request'}}),flush=True)\n");
    try { await new PythonScraperClient(python, terminal).MatchAsync("DEMO-001"); throw new Exception("Expected failure"); }
    catch (ScraperException e) { Assert(e.Code == "invalid_request"); }
});
AsyncTest("bridge teardown bounded if an exited worker child holds pipes", async () =>
{
    var code = "import sys,json,time;print(json.dumps({'version':1,'requestId':sys.argv[1],'type':'completed'}),flush=True);time.sleep(3)";
    var script = Worker("inherited-pipe", "import subprocess\nsubprocess.Popen([sys.executable,'-c'," + System.Text.Json.JsonSerializer.Serialize(code) + ",r['requestId']])\n");
    var clock = System.Diagnostics.Stopwatch.StartNew();
    await new PythonScraperClient(python, script) { RequestTimeout = TimeSpan.FromSeconds(1) }.MatchAsync("DEMO-001");
    Assert(clock.Elapsed < TimeSpan.FromSeconds(2.5), clock.Elapsed.ToString());
});

AsyncTest("old selection cannot review a newer indexed fingerprint", async () =>
{
    var scan = await Scan("review-race", "DEMO-001.mkv");
    using var store = new IndexStore(Path.Combine(Dir("review-race-db"), "index.db"));
    await store.SaveScanAsync(scan);
    File.AppendAllText(scan.Assets[0].SourcePath, "new version");
    await store.SaveScanAsync(await scanner.ScanAsync(scan.Root));
    try { await store.SaveReviewAsync(scan.Assets[0].Id, Review(), scan.Assets[0].Fingerprint); throw new Exception("Expected stale review rejection"); }
    catch (InvalidOperationException e) { Assert(e.Message.Contains("rescanned")); }
    Assert((await store.LoadReviewsAsync()).Count == 0);
});
AsyncTest("bridge rejects structurally incomplete candidate", async () =>
{
    var script = Worker("incomplete-candidate", "print(json.dumps({'version':1,'requestId':r['requestId'],'type':'candidate','candidate':{'id':'DEMO-001','title':'Fixture','confidence':1.0}}),flush=True)\n");
    try { await new PythonScraperClient(python, script).MatchAsync("DEMO-001"); throw new Exception("Expected invalid candidate rejection"); }
    catch (ScraperException e) { Assert(e.Code == "invalid_candidate"); }
});
AsyncTest("bridge keeps provider IDs separate from catalog identity", async () =>
{
    async Task Reject(object candidate)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(candidate);
        var script = Worker("identity-" + Guid.NewGuid().ToString("N"), "c=json.loads(" + System.Text.Json.JsonSerializer.Serialize(body) + ")\nprint(json.dumps({'version':1,'requestId':r['requestId'],'type':'candidate','candidate':c}),flush=True)\n");
        try { await new PythonScraperClient(python, script).MatchAsync("DEMO-001"); throw new Exception("Expected identity rejection"); }
        catch (ScraperException e) { Assert(e.Code == "invalid_identity", e.Code); }
    }
    var candidate = new MetadataCandidate("theporndb_movies:demo-001", "Neutral", 2024, "theporndb_movies", "https://api.theporndb.net/movies/demo-001", 0.8, new Dictionary<string, string> { ["fixture"] = "true", ["identitySchema"] = "2" })
    { SourceId = "demo-001", RequiresReview = true, ReviewReasons = ["missing_catalog_identifier"] };
    await Reject(candidate with { Provenance = new Dictionary<string, string> { ["fixture"] = "true" } });
    await Reject(candidate with { Id = "DEMO-001" });
    await Reject(candidate with { CatalogIdentifier = new("xcity:hinban", "DEMO-001") });
    await Reject(candidate with { SourceUrl = "https://example.invalid/movies/demo-001" });
    await Reject(candidate with { SourceUrl = candidate.SourceUrl + "?token=not-a-real-token" });
    await Reject(candidate with { SourceUrl = candidate.SourceUrl + "#fragment" });
    await Reject(candidate with { SourceUrl = "https://api.theporndb.net:444/movies/demo-001" });
    await Reject(candidate with { RequiresReview = false });
    await Reject(candidate with { SourceId = "../demo-001" });
    var xcity = candidate with { Id = "DEMO-001", Source = "xcity", SourceId = "1001", CatalogIdentifier = new("xcity:hinban", "DEMO-001"), SourceUrl = "https://xcity.jp/avod/detail/?id=1001" };
    await Reject(xcity with { CatalogIdentifier = new("other:catalog", "DEMO-001") });
    await Reject(xcity with { SourceId = "1002" });
    await Reject(xcity with { Id = "DEMO-002" });
    foreach (var badId in new[] { "0", "01001" })
        await Reject(xcity with { SourceId = badId, SourceUrl = $"https://xcity.jp/avod/detail/?id={badId}" });
    foreach (var badSlug in new[] { ".abc", "-abc", "_abc", "a..b" })
        await Reject(candidate with { SourceId = badSlug, Id = $"theporndb_movies:{badSlug}", SourceUrl = $"https://api.theporndb.net/movies/{badSlug}" });
    var longSlug = new string('a', 128);
    var valid = candidate with { SourceId = longSlug, Id = $"theporndb_movies:{longSlug}", SourceUrl = $"https://api.theporndb.net/movies/{longSlug}" };
    var body = System.Text.Json.JsonSerializer.Serialize(valid);
    var script = Worker("long-valid-identity", "c=json.loads(" + System.Text.Json.JsonSerializer.Serialize(body) + ")\nprint(json.dumps({'version':1,'requestId':r['requestId'],'type':'candidate','candidate':c}),flush=True)\nprint(json.dumps({'version':1,'requestId':r['requestId'],'type':'completed'}),flush=True)\n");
    Assert((await new PythonScraperClient(python, script).MatchAsync("A title")).Single().SourceId == longSlug);
});
var failures = 0;
try
{
    foreach (var (name, run) in checks)
    {
        try { await run(); Console.WriteLine($"PASS {name}"); }
        catch (Exception e) { failures++; Console.WriteLine($"FAIL {name}: {e}"); }
    }
    Console.WriteLine($"RESULT {checks.Count - failures}/{checks.Count} passed; sandbox-only checks, OS={System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
}
finally { SqliteConnection.ClearAllPools(); Directory.Delete(sandbox, recursive: true); }
return failures == 0 ? 0 : 1;

using System.Text.Json;
using Organizer.Core;
using Organizer.Desktop.ViewModels;
using Organizer.Infrastructure;

namespace Organizer.Desktop.Diagnostics;

/// <summary>Settings lifecycle uses synthetic files and in-memory clients; the real CLI/parser integration uses HTTP MockTransport with external socket/DNS guards.</summary>
internal static class SourceSettingsSmoke
{
    public static async Task RunAsync(string root, StartupOptions options, Action<bool, string> check)
    {
        var folder = Path.Combine(root, "source-settings");
        var media = Path.Combine(folder, "media");
        Directory.CreateDirectory(media);
        var filename = Path.Combine(media, "DEMO-001.mkv");
        await File.WriteAllTextAsync(filename, "Synthetic metadata settings fixture.");
        var database = Path.Combine(folder, "index.db");
        var configPath = Path.Combine(folder, "metadata-source.json");
        var store = new MetadataSettingsStore(configPath);
        options = options with { IndexPath = database, DemoPath = media };
        var clients = new List<FakeClient>();
        IMetadataClient Create(string? config) { var client = new FakeClient(config); clients.Add(client); return client; }
        using (var vm = new WorkbenchViewModel(options, Create))
        {
            await vm.InitializeAsync();
            check(!vm.IsRealSource && vm.SourceStatus == "未配置" && clients.Single().Config is null && clients.Sum(c => c.Calls) == 0 && !File.Exists(configPath),
                "Missing source config defaults to demo without saving settings or querying during startup/scan");
            var abandoned = vm.BeginSourceSettings();
            abandoned.Mode = SourceSettingsViewModel.RealMode; abandoned.Proxy = "http://localhost:7890";
            check(!vm.IsRealSource && !File.Exists(configPath) && clients.Count == 1,
                "Detached configuration edits and cancellation cannot enable or persist a real source");
            var invalid = vm.BeginSourceSettings(); invalid.Mode = SourceSettingsViewModel.RealMode;
            invalid.Proxy = "http://user:private-secret@localhost:7890";
            check(!vm.SaveSourceSettings(invalid) && invalid.HasError && !invalid.Error.Contains("private-secret", StringComparison.Ordinal) && !File.Exists(configPath),
                "Credential-bearing proxy is rejected before persistence without echoing secrets");
            invalid.Proxy = "http://localhost:7890"; invalid.ConnectTimeout = "NaN";
            check(!vm.SaveSourceSettings(invalid) && !vm.IsRealSource, "Nonfinite timeouts cannot enable networking");
            invalid.ConnectTimeout = "5";
            check(vm.SaveSourceSettings(invalid) && vm.IsRealSource && clients.Last().Config == configPath && vm.MatchSources.SequenceEqual(["JavBus"]),
                "Explicit save activates the fixed real-source client and exact JavBus source selection");
            check(store.Load() is { NetworkEnabled: true, Proxy: "http://localhost:7890" } && clients.Sum(c => c.Calls) == 0 && clients[0].Disposed,
                "Saving switches/disposes the client and persists restart settings without making a request");
            using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(configPath)))
                check(json.RootElement.GetProperty("network_enabled").GetBoolean() && json.RootElement.GetProperty("origin").GetString() == MetadataSourceSettings.AllowedOrigin &&
                    !json.RootElement.TryGetProperty("token", out _) && !json.RootElement.TryGetProperty("cookie", out _),
                    "Persisted settings use the Python contract and contain no credential fields");
            await vm.ScanAsync();
            check(clients.Sum(c => c.Calls) == 0, "Scanning after explicit enable still performs no metadata request");
            var active = clients.Last();
            await vm.MatchAsync();
            check(active.Calls == 1 && active.LastSources!.SequenceEqual(["javbus"]) && vm.SourceStatus == "无结果" && vm.CandidateStatus.Contains("无结果", StringComparison.Ordinal),
                "Only explicit match requests the selected real source; valid empty response is localized as no result");
            foreach (var (code, status) in new[] { ("network", "网络失败"), ("parse", "解析失败"), ("invalid_config", "未配置"), ("sources_failed", "查询失败"), ("auth_required", "需要授权"), ("challenge", "需要人工验证"), ("policy_blocked", "访问已阻止") })
            {
                active.Error = new ScraperException(code, "RAW_PRIVATE_DIAGNOSTIC");
                await vm.MatchAsync();
                check(vm.SourceStatus == status && vm.CandidateStatus.StartsWith(status, StringComparison.Ordinal) &&
                    !vm.Logs.Any(entry => entry.Message.Contains("RAW_PRIVATE_DIAGNOSTIC", StringComparison.Ordinal)),
                    $"{code} is localized safely and is not mislabeled as no results");
            }
            foreach (var code in new[] { "session_faulted", "session_full", "cancel_unacknowledged", "worker_exit", "worker_io", "protocol_invalid", "protocol_limit", "protocol_version", "invalid_candidate", "invalid_identity" })
            {
                active.Error = new ScraperException(code, "RAW_PRIVATE_DIAGNOSTIC");
                await vm.MatchAsync();
                check(vm.CandidateStatus.Contains("会话已停止", StringComparison.Ordinal) && vm.CandidateStatus.Contains("配置来源", StringComparison.Ordinal) &&
                    vm.CandidateStatus.Contains("重新保存", StringComparison.Ordinal) && vm.Candidates.Count == 0,
                    $"{code} explains explicit session replacement rather than an ineffective retry");
            }
            var callsBeforeRecovery = clients.Sum(client => client.Calls);
            check(vm.SaveSourceSettings(vm.BeginSourceSettings()) && clients.Sum(client => client.Calls) == callsBeforeRecovery && clients.Last() != active && active.Disposed,
                "Explicit re-save replaces a faulted source session without automatically retrying the query");
            active = clients.Last();
            active.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = vm.MatchAsync();
            await active.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var changes = vm.BeginSourceSettings(); changes.Interval = "2";
            check(vm.SaveSourceSettings(changes), "Settings can be saved while a stale query is pending");
            active.Pending.TrySetResult([Candidate()]);
            await pending;
            check(!vm.IsBusy && vm.Candidates.Count == 0 && vm.ReviewedCount == 0 && vm.SourceStatus == "已配置 · 尚未查询" && active.Disposed,
                "Settings replacement cancels/disposes the old client and fences even cancellation-ignoring late candidates");
            check(clients.Last().Calls == 0, "Settings replacement does not automatically re-query");
        }
        using (var restored = new WorkbenchViewModel(options, Create))
        {
            await restored.InitializeAsync();
            check(restored.IsRealSource && restored.BeginSourceSettings().Interval == "2" && clients.Last().Calls == 0,
                "Restart restores explicitly saved source settings without starting metadata work");
            check(restored.ResetSourceSettings(restored.BeginSourceSettings()) && !restored.IsRealSource && store.Load() == new MetadataSourceSettings() && clients.Last().Config is null,
                "Explicit clear resets every setting to the disabled demo defaults");
            check(clients.Last().Calls == 0, "Reset performs no match and does not automatically create reviews");
        }
        using (var restarted = new WorkbenchViewModel(options, Create))
            check(!restarted.IsRealSource && clients.Last().Config is null, "Reset remains disabled on a later restart");
        var badConfigs = new[] { "{\"network_enabled\":true,\"token\":\"secret\"}", "{\"network_enabled\":false,\"network_enabled\":true}", "{\"origin\":\"https://other.example\"}", "{\"network_enabled\":\"true\"}", "{\"proxy\":\"http://user:secret@localhost:7890\"}", new string(' ', 16385) };
        foreach (var bad in badConfigs)
        {
            await File.WriteAllTextAsync(configPath, bad);
            using var invalid = new WorkbenchViewModel(options, Create);
            check(!invalid.IsRealSource && clients.Last().Config is null && clients.Last().Calls == 0,
                "Invalid/duplicate/credential/oversize saved config fails closed without a query");
        }
        check(await File.ReadAllTextAsync(filename) == "Synthetic metadata settings fixture.", "Source-settings checks never modify synthetic media contents");
        await RunConfiguredHttpPipelineAsync(root, options, check);
    }

    private static async Task RunConfiguredHttpPipelineAsync(string root, StartupOptions options, Action<bool, string> check)
    {
        var folder = Path.Combine(root, "source-settings-http");
        var media = Path.Combine(folder, "media");
        Directory.CreateDirectory(media);
        foreach (var name in new[] { "DEMO-001.mkv", "BF-658.mp4" })
            await File.WriteAllTextAsync(Path.Combine(media, name), "Synthetic HTTP pipeline fixture. Not playable.");
        var worker = options.ResolveWorkerPath();
        var harness = Path.Combine(folder, "mock-http-entrypoint.py");
        var calls = Path.Combine(folder, "mock-http-calls.jsonl");
        var forbidden = Path.Combine(folder, "forbidden-network-attempt");
        var guard = Path.Combine(folder, "network-guard-active");
        await File.WriteAllTextAsync(harness, $$"""
            import asyncio
            import json
            import os
            import runpy
            import socket
            import sys
            from pathlib import Path
            from unittest.mock import patch
            import httpx

            entrypoint = {{JsonSerializer.Serialize(worker)}}
            calls = Path({{JsonSerializer.Serialize(calls)}})
            forbidden_path = Path({{JsonSerializer.Serialize(forbidden)}})
            guard_path = Path({{JsonSerializer.Serialize(guard)}})
            sys.path.insert(0, str(Path(entrypoint).parent))
            import mdcx_worker.runtime as runtime
            original_build = runtime.build_runtime
            original_run = asyncio.run
            detail = (Path(entrypoint).parent / "fixtures/javbus/detail-demo-001.html").read_bytes()

            def respond(request):
                with calls.open("a", encoding="utf-8") as stream:
                    stream.write(json.dumps({"url": str(request.url), "pid": os.getpid()}) + "\n")
                if str(request.url) == "https://www.javbus.com/DEMO-001":
                    body = detail
                else:
                    body = b"<html><title>Neutral test error</title></html>"
                return httpx.Response(200, content=body, headers={"Content-Type": "text/html"})

            def build_mock_runtime(settings=None):
                assert settings is not None and settings.network_enabled and settings.proxy is None
                return original_build(settings, http_backend=httpx.MockTransport(respond))

            async def guarded(coro):
                def forbidden(*args, **kwargs):
                    forbidden_path.write_text("Unexpected external network operation", encoding="utf-8")
                    raise AssertionError("Unexpected external network operation")
                with (patch.object(socket.socket, "__new__", side_effect=forbidden) as sockets,
                      patch("socket.create_connection", side_effect=forbidden) as connections,
                      patch("socket.getaddrinfo", side_effect=forbidden) as dns,
                      patch("urllib.request.urlopen", side_effect=forbidden) as http):
                    guard_path.write_text("socket/DNS guards active", encoding="utf-8")
                    await coro
                    for item in (sockets, connections, dns, http):
                        item.assert_not_called()

            runtime.build_runtime = build_mock_runtime
            asyncio.run = lambda coro, **kwargs: original_run(guarded(coro), **kwargs)
            sys.argv = [entrypoint, *sys.argv[1:]]
            runpy.run_path(entrypoint, run_name="__main__")
            """);
        await using (var vm = new WorkbenchViewModel(options with { DemoPath = media, IndexPath = Path.Combine(folder, "index.db"), WorkerPath = harness }))
        {
            await vm.InitializeAsync();
            var settings = vm.BeginSourceSettings(); settings.Mode = SourceSettingsViewModel.RealMode;
            check(vm.SaveSourceSettings(settings) && !File.Exists(calls) && !File.Exists(guard),
                "Actual desktop config save does not start the persistent Python process or HTTP transport");
            vm.SelectedAsset = vm.Assets.First(a => a.Filename == "DEMO-001.mkv");
            await vm.MatchAsync();
            check(vm.Candidates.Count == 1 && vm.SelectedCandidate is { Candidate: { Source: "javbus", SourceId: "DEMO-001", Title: "The Lantern Map", Year: 2024 } } &&
                vm.SelectedCandidate.Candidate.CatalogIdentifier is { Namespace: "javbus:catalog", Value: "DEMO-001" } &&
                vm.SelectedCandidate.Candidate.Provenance.GetValueOrDefault("fixture") == "true" && vm.ReviewedCount == 0,
                "Desktop settings -> persistent client -> actual CLI/config -> injected HTTP transport -> parser returns an unreviewed synthetic candidate");
            check(vm.SelectedCatalogIdentifier == "javbus:catalog · DEMO-001" && vm.SelectedCandidate!.Confidence == "番号一致 · javbus",
                "Real-source candidate displays namespaced catalog evidence distinctly in the desktop");
            vm.SelectedAsset = vm.Assets.First(a => a.Filename == "BF-658.mp4");
            await vm.MatchAsync();
            check(vm.Candidates.Count == 0 && vm.SourceStatus == "解析失败" && vm.SelectedAsset.MatchFailureCode == "schema_drift" && vm.ReviewedCount == 0,
                "Neutral BF-658 mock error cannot fabricate a match and displays a parser failure");
            var entries = (await File.ReadAllLinesAsync(calls)).Select(line => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line)!).ToArray();
            check(entries.Length == 2 && entries[0]["url"].GetString() == "https://www.javbus.com/DEMO-001" &&
                entries[1]["url"].GetString() == "https://www.javbus.com/BF-658" && entries.Select(entry => entry["pid"].GetInt32()).Distinct().Count() == 1,
                "Two explicit desktop queries share one configured worker with exact bounded source routes");
        }
        check(File.Exists(guard) && !File.Exists(forbidden), "Configured desktop end-to-end HTTP pipeline made zero external socket/DNS/HTTP calls");
        check(Directory.GetFiles(media).All(path => File.ReadAllText(path) == "Synthetic HTTP pipeline fixture. Not playable."),
            "Configured desktop pipeline leaves every synthetic media byte unchanged");
    }

    private static MetadataCandidate Candidate() => new("DEMO-001", "Synthetic late candidate", 2024, "javbus", "https://www.javbus.com/DEMO-001", .95,
        new Dictionary<string, string> { ["identitySchema"] = "2", ["matchMethod"] = "exact_identifier" })
        { SourceId = "DEMO-001", CatalogIdentifier = new("javbus:catalog", "DEMO-001") };

    private sealed class FakeClient(string? config) : IMetadataClient
    {
        public string? Config { get; } = config;
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }
        public Exception? Error { get; set; }
        public IReadOnlyList<string>? LastSources { get; private set; }
        public TaskCompletionSource<IReadOnlyList<MetadataCandidate>>? Pending { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<MetadataCandidate>> MatchAsync(string query, CancellationToken cancellationToken = default, IReadOnlyList<string>? sources = null)
        {
            Calls++; LastSources = sources;
            if (Error is not null) return Task.FromException<IReadOnlyList<MetadataCandidate>>(Error);
            if (Pending is not null) { Entered.TrySetResult(); return Pending.Task; }
            return Task.FromResult<IReadOnlyList<MetadataCandidate>>([]);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}

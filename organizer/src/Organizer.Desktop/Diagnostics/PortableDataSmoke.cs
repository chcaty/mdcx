using Organizer.Core;
using Organizer.Desktop.Configuration;
using Organizer.Desktop.ViewModels;
using Organizer.Infrastructure;

namespace Organizer.Desktop.Diagnostics;

/// <summary>Portable startup checks own only temporary synthetic files; no network client may execute.</summary>
internal static class PortableDataSmoke
{
    internal static async Task RunAsync(string temporaryRoot, StartupOptions options, Action<bool, string> check)
    {
        var root = Path.Combine(temporaryRoot, "portable 数据 checks");
        var application = Path.Combine(root, "工具 Portable App");
        var local = Path.Combine(root, "unrelated-installed-data");
        var installed = Path.Combine(local, "MDCxOrganizer");
        Directory.CreateDirectory(application);
        Directory.CreateDirectory(installed);
        var oldIndex = Path.Combine(installed, "index.db");
        var oldConfig = Path.Combine(installed, "metadata-source.json");
        File.WriteAllText(oldIndex, "Existing installed index is never portable input.");
        File.WriteAllText(oldConfig, "{\"network_enabled\":true}");
        var oldFiles = Directory.GetFiles(installed).ToDictionary(path => path, File.ReadAllBytes);
        var marker = Path.Combine(application, ApplicationDataLocation.MarkerFileName);
        var manifest = Path.Combine(application, "package-manifest.json");
        var data = Path.Combine(application, "data");

        var legacy = ApplicationDataLocation.Resolve(application, localApplicationData: local);
        check(legacy.Mode == "installed" && legacy.IndexPath == oldIndex && !Directory.Exists(data),
            "Absent marker preserves installed/development data resolution without creating portable data");
        File.WriteAllText(manifest, "{\"schemaVersion\":2,\"packageKind\":\"installer\"}");
        check(ApplicationDataLocation.Resolve(application, localApplicationData: local).IndexPath == oldIndex,
            "Installer manifest without portable marker preserves existing installed storage");
        File.WriteAllText(manifest, "{\"schemaVersion\":2}");
        check(ApplicationDataLocation.Resolve(application, localApplicationData: local).IndexPath == oldIndex,
            "Historical manifests without mode fields preserve installed storage");
        File.WriteAllText(manifest, "{\"dataMode\":\"portable\"}");
        ExpectFailure(() => ApplicationDataLocation.Resolve(application, localApplicationData: local), "portable_marker_invalid", check,
            "Portable dataMode evidence alone rejects a missing marker without becoming a second opt-in");
        File.WriteAllText(manifest, "{\"packageKind\":\"portable\"}");
        ExpectFailure(() => ApplicationDataLocation.Resolve(application, localApplicationData: local), "portable_marker_invalid", check,
            "Known portable package missing its marker cannot open installed data");
        foreach (var invalidManifest in new[] { "invalid", "{\"packageKind\":\"portable\",\"packageKind\":\"installer\"}", "{\"packageKind\":true}", "{\"packageKind\":\"Portable\"}", "{\"packageKind\":\"unknown\"}", "{\"packageKind\":\"installer\",\"dataMode\":\"portable\"}" })
        {
            File.WriteAllText(manifest, invalidManifest);
            ExpectFailure(() => ApplicationDataLocation.Resolve(application, localApplicationData: local), "package_manifest_invalid", check,
                "Unreadable manifest semantics cannot silently fall back to installed storage");
        }
        File.WriteAllText(manifest, new string(' ', 4 * 1024 * 1024 + 1));
        ExpectFailure(() => ApplicationDataLocation.Resolve(application, localApplicationData: local), "package_manifest_invalid", check,
            "Oversized package manifest is rejected before unbounded parsing");
        File.Delete(manifest);
        Directory.CreateDirectory(marker);
        ExpectFailure(() => ApplicationDataLocation.Resolve(application, localApplicationData: local), "portable_marker_invalid", check,
            "Directory in place of portable marker produces a readable startup failure");
        Directory.Delete(marker);
        foreach (var invalid in new[] { "", "{}", "[]", "null", "{", "{\"formatVersion\":2}", "{\"formatVersion\":\"1\"}",
                     "{\"formatVersion\":1,\"formatVersion\":1}", "{\"formatVersion\":1,\"dataDirectory\":\"elsewhere\"}", new string(' ', 4097) })
        {
            File.WriteAllText(marker, invalid);
            ExpectFailure(() => ApplicationDataLocation.Resolve(application, localApplicationData: local), "portable_marker_invalid", check,
                "Invalid, unsupported, duplicate, extra-field or oversized portable marker fails closed");
        }
        File.WriteAllText(marker, ApplicationDataLocation.MarkerContents);
        var location = ApplicationDataLocation.Resolve(Path.Combine(application, "."), localApplicationData: local);
        check(location.IsPortable && location.DirectoryPath == data && location.IndexPath == Path.Combine(data, "index.db") &&
              location.SettingsPath == Path.Combine(data, "metadata-source.json") && !Directory.Exists(data),
            "Unicode/space portable path resolves adjacent fixed data paths without touching storage");
        location.EnsureWritable();
        check(Directory.Exists(data) && !Directory.GetFiles(application, ".mdcx-write-check-*", SearchOption.AllDirectories).Any() &&
              !File.Exists(location.IndexPath) && !File.Exists(location.SettingsPath),
            "Real portable write probes are cleaned up and do not invent database or source settings");
        File.WriteAllText(marker, "\uFEFF { \"formatVersion\" : 1 }\n");
        check(ApplicationDataLocation.Resolve(application).IsPortable, "UTF-8 BOM and whitespace in a valid portable marker are accepted");
        File.WriteAllText(marker, ApplicationDataLocation.MarkerContents);

        var attempts = new List<string>();
        ExpectFailure(() => location.EnsureWritable(path => { attempts.Add(path); throw new UnauthorizedAccessException("Synthetic app directory ACL denial."); }),
            "portable_data_unwritable", check, "Denied application-root write probe fails closed without a fallback");
        check(attempts.SequenceEqual([application]), "Failed root permission probe does not proceed to the data directory");
        attempts.Clear();
        ExpectFailure(() => location.EnsureWritable(path => { attempts.Add(path); if (path == data) throw new UnauthorizedAccessException("Synthetic data directory ACL denial."); }),
            "portable_data_unwritable", check, "Denied data-directory probe fails closed without a fallback");
        check(attempts.SequenceEqual([application, data]), "Portable startup verifies both app and data directory permissions");
        if (!OperatingSystem.IsWindows())
        {
            var permissions = File.GetUnixFileMode(data);
            try
            {
                File.SetUnixFileMode(data, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                ExpectFailure(() => location.EnsureWritable(), "portable_data_unwritable", check, "Real OS read-only data directory fails the write probe");
            }
            finally { File.SetUnixFileMode(data, permissions); }
        }

        var explicitIndex = Path.Combine(root, "isolated diagnostics", "index.db");
        File.WriteAllText(marker, "invalid marker");
        var isolated = ApplicationDataLocation.Resolve(application, explicitIndex, local);
        isolated.EnsureWritable(_ => throw new InvalidOperationException("Explicit diagnostics must not probe portable storage."));
        check(isolated.Mode == "explicit" && isolated.IndexPath == explicitIndex && !Directory.Exists(isolated.DirectoryPath),
            "Existing explicit diagnostic IndexPath intentionally bypasses all default data and malformed marker paths");
        foreach (var invalidPath in new[] { "", " ", Path.GetPathRoot(root)!, application, "invalid\0index" })
            ExpectFailure(() => ApplicationDataLocation.Resolve(application, invalidPath), "startup_arguments_invalid", check,
                "Invalid internal diagnostic index paths fail with a typed startup error");
        File.WriteAllText(marker, ApplicationDataLocation.MarkerContents);
        var rendering = StartupOptions.Parse(["--screenshot", Path.Combine(root, "unused.png")]) with { ApplicationDirectory = application };
        check(rendering.ResolveDataLocation().Mode == "explicit" && rendering.IndexPath != location.IndexPath,
            "Existing screenshot diagnostics keep a fresh isolated index inside portable packages");
        check(StartupOptions.ResolveDefaultPythonExecutable(application, true) == Path.Combine(application, "python", "python.exe") &&
              StartupOptions.ResolveDefaultWorkerPath(application, root) == Path.Combine(application, "worker", "main.py"),
            "Portable marker prevents PATH or unrelated-checkout fallback even when bundled runtimes are missing");

        CheckLinks(root, application, location, check);
        await CheckPersistenceAsync(root, application, options, location, check);
        check(oldFiles.All(file => File.ReadAllBytes(file.Key).SequenceEqual(file.Value)) && Directory.GetFiles(installed).Length == oldFiles.Count,
            "Portable resolution, permission failures and repeated startups leave installed index/config untouched");
    }

    private static void CheckLinks(string root, string application, ApplicationDataLocation location, Action<bool, string> check)
    {
        // Windows file links require developer mode/admin. Native packaging checks
        // exercise junction/ACL behavior separately; Unix covers file links here.
        if (OperatingSystem.IsWindows()) return;
        var outside = Path.Combine(root, "external settings");
        Directory.CreateDirectory(outside);
        var external = Path.Combine(outside, "external.json");
        File.WriteAllText(external, "{\"network_enabled\":true}");
        foreach (var path in new[] { location.IndexPath, location.SettingsPath, location.IndexPath + "-wal", location.IndexPath + "-shm", location.IndexPath + "-journal" })
        {
            File.CreateSymbolicLink(path, external);
            try
            {
                ExpectFailure(() => ApplicationDataLocation.Resolve(application), "portable_data_unwritable", check,
                    "Linked config/database/SQLite companion path is rejected before any external file is opened");
            }
            finally { File.Delete(path); }
        }
        Directory.Delete(location.DirectoryPath);
        Directory.CreateSymbolicLink(location.DirectoryPath, outside);
        try { ExpectFailure(() => ApplicationDataLocation.Resolve(application), "portable_data_unwritable", check, "Linked portable data directory cannot redirect storage outside the app"); }
        finally { Directory.Delete(location.DirectoryPath); Directory.CreateDirectory(location.DirectoryPath); }
        var store = new MetadataSettingsStore(location.SettingsPath);
        // Replacing a path after resolving startup must still be detected when saving.
        File.CreateSymbolicLink(location.SettingsPath, external);
        try
        {
            var blockedSave = false;
            try { store.Save(new()); } catch (IOException) { blockedSave = true; }
            check(blockedSave && File.ReadAllText(external) == "{\"network_enabled\":true}", "Late settings link replacement cannot redirect a later explicit save");
            var blockedLoad = false;
            try { _ = store.Load(); } catch (InvalidDataException) { blockedLoad = true; }
            check(blockedLoad, "Settings load revalidates path links instead of importing external settings");
        }
        finally { File.Delete(location.SettingsPath); }
    }

    private static async Task CheckPersistenceAsync(string root, string application, StartupOptions options, ApplicationDataLocation location, Action<bool, string> check)
    {
        var media = Path.Combine(root, "fictional media");
        Directory.CreateDirectory(media);
        var mediaPath = Path.Combine(media, "DEMO-001.mkv");
        await File.WriteAllTextAsync(mediaPath, "Synthetic portable persistence fixture, not playable media.");
        options = options with { ApplicationDirectory = application, IndexPath = null, DemoPath = media, WorkerPath = options.ResolveWorkerPath() };
        var clients = new List<NoNetworkClient>();
        IMetadataClient Client(string? config) { var client = new NoNetworkClient(); clients.Add(client); return client; }
        await using (var vm = new WorkbenchViewModel(options, Client))
        {
            await vm.InitializeAsync();
            check(vm.DataLocation == location && !vm.IsRealSource && File.Exists(location.IndexPath) && !File.Exists(location.SettingsPath),
                "Normal portable workbench creates only adjacent SQLite data and defaults to offline without installed config import");
            vm.EditorTitle = "Preserved portable review";
            vm.EditorYear = "2024";
            await vm.SaveReviewAsync();
            var settings = vm.BeginSourceSettings(); settings.Interval = "2";
            check(vm.SelectedAsset?.Review?.Title == "Preserved portable review" && vm.SaveSourceSettings(settings),
                "Explicit portable review and settings saves use the adjacent database/config");
        }
        File.WriteAllText(Path.Combine(application, "replacement-build.txt"), "Synthetic replacement program file; existing data is untouched.");
        File.WriteAllText(Path.Combine(application, ApplicationDataLocation.MarkerFileName), ApplicationDataLocation.MarkerContents);
        await using (var restored = new WorkbenchViewModel(options, Client))
        {
            await restored.InitializeAsync();
            check(restored.SelectedAsset?.Review?.Title == "Preserved portable review" && restored.BeginSourceSettings().Interval == "2" && !restored.IsRealSource,
                "Restart/replacement program files preserve reviewed SQLite data and explicitly saved source settings");
        }
        check(clients.All(client => client.Calls == 0) && File.ReadAllText(mediaPath) == "Synthetic portable persistence fixture, not playable media.",
            "Portable persistence checks never query sources or mutate synthetic media");
        var configAttributes = File.GetAttributes(location.SettingsPath);
        try
        {
            File.SetAttributes(location.SettingsPath, configAttributes | FileAttributes.ReadOnly);
            ExpectFailure(() => location.EnsureWritable(), "portable_data_unwritable", check,
                "An existing read-only settings file blocks portable startup even with a writable parent directory");
        }
        finally { File.SetAttributes(location.SettingsPath, configAttributes); }
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={location.IndexPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=999;";
            command.ExecuteNonQuery();
        }
        ExpectFailure(() => { using var ignored = new WorkbenchViewModel(options, Client); }, "portable_index_unavailable", check,
            "Future SQLite schema is refused with preserve/backup/version guidance rather than a permission workaround");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={location.IndexPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            check(Convert.ToInt32(command.ExecuteScalar()) == 999, "Failed startup never resets or downgrades a future database schema");
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.WriteAllText(location.IndexPath, "Invalid SQLite database");
        ExpectFailure(() => { using var ignored = new WorkbenchViewModel(options, Client); }, "portable_index_unavailable", check,
            "Portable database-open failure is fatal instead of continuing an unsaved session");
    }

    private static void ExpectFailure(Action action, string code, Action<bool, string> check, string message)
    {
        ApplicationStartupException? failure = null;
        try { action(); } catch (ApplicationStartupException exception) { failure = exception; }
        check(failure?.Code == code && !string.IsNullOrWhiteSpace(failure.Message), message);
    }

    private sealed class NoNetworkClient : IMetadataClient
    {
        internal int Calls { get; private set; }
        public Task<IReadOnlyList<MetadataCandidate>> MatchAsync(string query, CancellationToken cancellationToken = default, IReadOnlyList<string>? sources = null)
        { Calls++; throw new InvalidOperationException("Portable storage checks must never query metadata."); }
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

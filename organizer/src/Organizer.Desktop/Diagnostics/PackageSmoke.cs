using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using Organizer.Core;
using Organizer.Desktop.ViewModels;
using Organizer.Infrastructure;

namespace Organizer.Desktop.Diagnostics;

/// <summary>
/// Installed Windows package verification through a real native window. All
/// media and index writes are confined to a freshly owned temporary directory.
/// No externally supplied media, database, interpreter or worker is accepted.
/// </summary>
internal sealed class PackageSmoke
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true
    };
    private readonly object _reportLock = new();
    private readonly Report _report;
    private readonly string _reportPath;
    private readonly string _root;
    private readonly Dictionary<string, byte[]> _sourceHashes = [];
    private bool _completed;
    private bool _attached;

    internal static PackageSmoke? Current { get; private set; }
    private StartupOptions Options { get; }

    private PackageSmoke(string reportPath)
    {
        _reportPath = reportPath;
        _root = Directory.CreateTempSubdirectory("mdcx-package-smoke-").FullName;
        var source = Path.Combine(_root, "source");
        Options = StartupOptions.Parse([]) with { DemoPath = source, IndexPath = Path.Combine(_root, "index.db") };
        _report = new Report
        {
            AppDirectory = AppContext.BaseDirectory,
            WorkingDirectory = Environment.CurrentDirectory,
            PythonExecutable = Options.PythonExecutable,
            WorkerPath = Options.ResolveWorkerPath(),
            TemporaryRoot = _root,
            IndexPath = Options.IndexPath,
            ScreenshotPath = Path.ChangeExtension(reportPath, ".png")
        };
    }

    internal static int Run(string[] args, Func<StartupOptions, int> startNativeApplication)
    {
        PackageSmoke? smoke = null;
        Timer? watchdog = null;
        var result = 1;
        try
        {
            var reportPath = ReadReportPath(args);
            // CreateNew reserves a fresh output and cannot truncate an existing
            // user file. A report must be placed below system or CI runner temp.
            using (new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
            smoke = new PackageSmoke(reportPath);
            Current = smoke;
            smoke.WriteReport();
            watchdog = new Timer(_ => smoke.AbortForTimeout(), null, TimeSpan.FromSeconds(75), Timeout.InfiniteTimeSpan);
            smoke.Check(OperatingSystem.IsWindows(), "Windows native backend is required; Headless is not a package test.");
            smoke.PrepareFixture();
            var lifetimeExitCode = startNativeApplication(smoke.Options);
            smoke._report.ApplicationLifetimeExitCode = lifetimeExitCode;
            smoke.Check(smoke._completed && lifetimeExitCode == 0, "Native desktop lifetime completed the package checks successfully.");
            smoke.Check(smoke._report.WindowOpened && smoke._report.WindowClosed, "Native main window opened and closed before process exit.");
            result = 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"PACKAGE SMOKE FAILED: {exception}");
            if (smoke is not null) smoke._report.Error ??= exception.ToString();
        }
        finally
        {
            watchdog?.Dispose();
            Current = null;
            if (smoke is not null)
            {
                try
                {
                    SqliteConnection.ClearAllPools();
                    Directory.Delete(smoke._root, recursive: true);
                    smoke._report.TemporaryRootRemoved = !Directory.Exists(smoke._root);
                }
                catch (Exception exception)
                {
                    smoke._report.Error = $"{smoke._report.Error}\nTemporary fixture cleanup failed: {exception.Message}".Trim();
                    result = 1;
                }
                smoke._report.Status = result == 0 ? "passed" : "failed";
                smoke._report.ExitCode = result;
                smoke._report.FinishedUtc = DateTimeOffset.UtcNow;
                try { smoke.WriteReport(); }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"Unable to write package smoke report: {exception.Message}");
                    result = 1;
                }
            }
        }
        return result;
    }

    internal static string ReadReportPath(string[] args, string modeFlag = "--smoke-test-package", string reportFlag = "--package-smoke-report")
    {
        string? path = null;
        var modeCount = 0;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == modeFlag) { modeCount++; continue; }
            if (args[i] == reportFlag && path is null && i + 1 < args.Length) { path = args[++i]; continue; }
            throw new ArgumentException($"Startup diagnostics accept only {modeFlag} {reportFlag} <new absolute .json path under system or runner temp>.");
        }
        if (modeCount != 1 || path is null || !Path.IsPathFullyQualified(path) || Path.GetExtension(path) != ".json")
            throw new ArgumentException("Package smoke requires one explicit new absolute .json report path under system or runner temp.");
        path = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var temporaryDirectories = new[] { Path.GetTempPath(), Environment.GetEnvironmentVariable("RUNNER_TEMP") }
            .Where(directory => directory is not null && Path.IsPathFullyQualified(directory))
            .Select(directory => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory!)) + Path.DirectorySeparatorChar);
        if (!temporaryDirectories.Any(directory => path.StartsWith(directory, comparison)))
            throw new ArgumentException("Package smoke reports must be under system or runner temp.");
        var parent = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The report directory must already exist.");
        for (var directory = new DirectoryInfo(parent); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Package smoke report directories must not contain links or reparse points.");
        if (File.Exists(path) || Directory.Exists(path) || File.Exists(Path.ChangeExtension(path, ".png")) || Directory.Exists(Path.ChangeExtension(path, ".png")))
            throw new IOException("Package smoke report and PNG paths must both be new files.");
        return path;
    }

    private void PrepareFixture()
    {
        Check(Path.IsPathFullyQualified(Options.PythonExecutable) && SamePath(Options.PythonExecutable, StartupOptions.BundledPythonPath)
              && File.Exists(Options.PythonExecutable), "Default startup selects the adjacent bundled python/python.exe without PATH lookup.");
        Check(SamePath(Options.ResolveWorkerPath(), Path.Combine(AppContext.BaseDirectory, "worker", "main.py"))
              && File.Exists(Options.ResolveWorkerPath()), "Default startup selects the installed worker/main.py independently of the working directory.");
        Check(Options.DemoPath == Path.Combine(_root, "source") && Options.IndexPath == Path.Combine(_root, "index.db")
              && Options.ScreenshotPath is null && !Options.SmokeTestView, "Diagnostic media and index paths are isolated before application startup.");
        _report.IndexIsolated = true;
        Directory.CreateDirectory(Options.DemoPath!);
        foreach (var (name, content) in new[]
                 {
                     ("DEMO-001.mkv", "Synthetic package verification fixture. Not playable media.\n"),
                     ("DEMO-001.zh-CN.srt", "Synthetic package verification subtitle.\n"),
                     ("DEMO-002.mp4", "Second synthetic package verification fixture. Not playable media.\n")
                 })
        {
            var path = Path.Combine(Options.DemoPath!, name);
            File.WriteAllText(path, content);
            _sourceHashes[path] = SHA256.HashData(File.ReadAllBytes(path));
        }
    }

    internal void Attach(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, WorkbenchViewModel viewModel)
    {
        if (_attached || Program.Options != Options) throw new InvalidOperationException("Package smoke may only attach once to its isolated application options.");
        _attached = true;
        window.Closed += (_, _) => _report.WindowClosed = true;
        window.Opened += async (_, _) =>
        {
            var exitCode = 1;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(55));
                await ExerciseAsync(window, viewModel, timeout.Token);
                _completed = true;
                exitCode = 0;
            }
            catch (Exception exception)
            {
                _report.Error = exception.ToString();
                Console.Error.WriteLine($"PACKAGE SMOKE FAILED: {exception}");
            }
            finally { desktop.Shutdown(exitCode); }
        };
    }

    private async Task ExerciseAsync(MainWindow window, WorkbenchViewModel vm, CancellationToken token)
    {
        var handle = window.TryGetPlatformHandle();
        Check(handle is { Handle: not 0, HandleDescriptor: "HWND" } && window.IsVisible,
            "A visible real Win32 HWND was created by the native Avalonia backend.");
        _report.WindowOpened = true;
        _report.NativeBackend = "Win32";
        _report.NativeHandleDescriptor = handle!.HandleDescriptor;
        _report.NativeHandle = handle.Handle.ToString("X");
        // MainWindow's existing Opened handler owns initialization. Do not run
        // another overlapping scan or replace the normal UI startup path.
        while (vm.IsBusy || vm.AllCount != 2) await Task.Delay(25, token);
        Check(vm.Assets.Count == 2 && vm.Assets.All(row => row.Review is null) && File.Exists(Options.IndexPath),
            "Normal window startup scans only the isolated fixtures and creates the isolated SQLite index.");
        await NativeFrameAsync(window, token);
        var mediaList = window.FindControl<ListBox>("MediaGrid") ?? throw new InvalidOperationException("Native media list was not found.");
        Check(mediaList.Bounds.Height > 100 && mediaList.ContainerFromIndex(0) is not null,
            "Native main window realizes and lays out the bound media rows.");

        await CheckInterpreterAsync(token);
        var candidates = await new PythonScraperClient(Options.PythonExecutable, Options.ResolveWorkerPath())
            .MatchAsync("DEMO-001", token, ["xcity"]);
        Check(candidates.Count == 1 && IsExpectedFixture(candidates[0]),
            "C# PythonScraperClient receives the exact neutral fixture through installed embedded Python.");
        var fixture = candidates[0];
        _report.FixtureMatched = true;
        _report.FixtureIdentifier = fixture.Id;
        _report.FixtureTitle = fixture.Title;
        _report.FixtureSource = fixture.Source;
        _report.FixtureResponseOrigin = fixture.Provenance["responseOrigin"];

        vm.SelectedAsset = vm.Assets.Single(row => row.Identifier == "DEMO-001");
        await vm.MatchAsync().WaitAsync(token);
        Check(vm.Candidates.Count == 1 && IsExpectedFixture(vm.Candidates[0].Candidate) && vm.SelectedAsset.Review is null,
            "Normal view-model matching displays the fixture candidate without silently accepting it.");
        // Explicit diagnostic acceptance applies only to our owned synthetic
        // fixture. This path cannot be pointed at a user's folder or index.
        await vm.AcceptCandidateAsync().WaitAsync(token);
        await vm.BuildPlanAsync().WaitAsync(token);
        Check(vm.SelectedAsset.Review?.Title == "The Lantern Map" && vm.PlanOperations.Count == 2 && !vm.HasPlanIssues,
            "Synthetic fixture review and media-plus-subtitle preview complete through the installed UI services.");
        _report.PreviewOperations = vm.PlanOperations.Count;
        Check(!Directory.Exists(vm.TargetPath), "Preview does not create or mutate its target directory.");

        var search = window.FindControl<TextBox>("SearchBox") ?? throw new InvalidOperationException("Search input was not found.");
        search.SetCurrentValue(TextBox.TextProperty, "DEMO-002");
        await NativeFrameAsync(window, token);
        Check(vm.Assets.Count == 1 && vm.Assets[0].Identifier == "DEMO-002", "Rendered search control updates the bound media filter.");
        search.SetCurrentValue(TextBox.TextProperty, "");
        await NativeFrameAsync(window, token);
        Check(vm.Assets.Count == 2, "Clearing the native search restores both synthetic assets.");
        var planButton = window.FindControl<Button>("PlanNav") ?? throw new InvalidOperationException("Plan navigation was not found.");
        planButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await NativeFrameAsync(window, token);
        Check(window.FindControl<Grid>("PlanWorkspace")?.IsVisible == true && window.GetVisualDescendants().OfType<TextBlock>().Any(),
            "Native routed navigation displays the rendered preview workspace.");
        using (var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96)))
        {
            bitmap.Render(window);
            using var output = new FileStream(_report.ScreenshotPath, FileMode.CreateNew, FileAccess.Write);
            bitmap.Save(output);
        }
        Check(new FileInfo(_report.ScreenshotPath).Length > 4096, "Native window visual tree renders to a nonempty diagnostic PNG.");
        _report.WindowRendered = true;
        Check(Directory.GetFiles(Options.DemoPath!).Length == _sourceHashes.Count && _sourceHashes.All(pair =>
                File.Exists(pair.Key) && SHA256.HashData(File.ReadAllBytes(pair.Key)).SequenceEqual(pair.Value)),
            "Every synthetic source file remains byte-for-byte unchanged.");
        _report.SourceUnchanged = true;
    }

    private async Task CheckInterpreterAsync(CancellationToken token)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(Options.PythonExecutable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("-I");
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("import json,sys; print(json.dumps({'executable':sys.executable,'version':list(sys.version_info[:3])}))");
        if (!process.Start()) throw new InvalidOperationException("The installed interpreter did not start.");
        using var cancelled = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        Check(process.ExitCode == 0, $"Bundled interpreter startup exits successfully: {await error}");
        using var identity = JsonDocument.Parse(await output);
        var executable = identity.RootElement.GetProperty("executable").GetString()!;
        var version = identity.RootElement.GetProperty("version").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        Check(SamePath(executable, Options.PythonExecutable) && version.Length == 3 && version[0] == 3 && version[1] >= 12,
            "Python reports the exact bundled executable and a supported Python 3.12+ version.");
        _report.InterpreterReportedExecutable = executable;
        _report.PythonVersion = string.Join('.', version);
    }

    private static bool IsExpectedFixture(MetadataCandidate candidate) => candidate is
        { Id: "DEMO-001", Title: "The Lantern Map", Year: 2024, Source: "xcity", SourceId: "1001",
          CatalogIdentifier: { Namespace: "xcity:hinban", Value: "DEMO-001" } }
        && candidate.Provenance.GetValueOrDefault("fixture") == "true"
        && candidate.Provenance.GetValueOrDefault("responseOrigin") == "synthetic_uncaptured";

    private static async Task NativeFrameAsync(MainWindow window, CancellationToken token)
    {
        window.UpdateLayout();
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.RequestAnimationFrame(_ => frame.TrySetResult());
        await frame.Task.WaitAsync(token);
        await Task.Delay(50, token);
        window.UpdateLayout();
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        lock (_reportLock) _report.Checks.Add(message);
        Console.WriteLine($"PACKAGE PASS: {message}");
    }

    private void AbortForTimeout()
    {
        lock (_reportLock)
        {
            _report.Status = "failed";
            _report.ExitCode = 124;
            _report.Error = "Package smoke exceeded its 75-second process deadline.";
            _report.FinishedUtc = DateTimeOffset.UtcNow;
            try { WriteReport(); } catch (Exception exception) { Console.Error.WriteLine(exception.Message); }
        }
        Environment.Exit(124);
    }

    private void WriteReport()
    {
        lock (_reportLock) File.WriteAllText(_reportPath, JsonSerializer.Serialize(_report, JsonOptions));
    }

    private sealed class Report
    {
        public int SchemaVersion { get; init; } = 1;
        public string Status { get; set; } = "running";
        public int ExitCode { get; set; } = 1;
        public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? FinishedUtc { get; set; }
        public string OperatingSystem { get; init; } = RuntimeInformation.OSDescription;
        public string AppDirectory { get; init; } = "";
        public string WorkingDirectory { get; init; } = "";
        public string PythonExecutable { get; init; } = "";
        public string? InterpreterReportedExecutable { get; set; }
        public string? PythonVersion { get; set; }
        public string WorkerPath { get; init; } = "";
        public string? NativeBackend { get; set; }
        public string? NativeHandleDescriptor { get; set; }
        public string? NativeHandle { get; set; }
        public bool WindowOpened { get; set; }
        public bool WindowRendered { get; set; }
        public bool WindowClosed { get; set; }
        public int? ApplicationLifetimeExitCode { get; set; }
        public bool FixtureMatched { get; set; }
        public string? FixtureIdentifier { get; set; }
        public string? FixtureTitle { get; set; }
        public string? FixtureSource { get; set; }
        public string? FixtureResponseOrigin { get; set; }
        public int PreviewOperations { get; set; }
        public bool SourceUnchanged { get; set; }
        public bool IndexIsolated { get; set; }
        public string? IndexPath { get; init; }
        public string TemporaryRoot { get; init; } = "";
        public bool TemporaryRootRemoved { get; set; }
        public string ScreenshotPath { get; init; } = "";
        public List<string> Checks { get; } = [];
        public string? Error { get; set; }
    }
}

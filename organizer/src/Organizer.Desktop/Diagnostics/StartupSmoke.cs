using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Organizer.Desktop.Configuration;
using Organizer.Core;
using Organizer.Desktop.ViewModels;

namespace Organizer.Desktop.Diagnostics;

/// <summary>
/// Native launch verification of the real default data location and error UI.
/// Opens the app without a media path or query; existing saved settings are never changed.
/// Reports are explicit, new files outside the application under system/runner temp.
/// </summary>
internal sealed class StartupSmoke(string reportPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly object _reportGate = new();
    private readonly Report _report = new() { ScreenshotPath = Path.ChangeExtension(reportPath, ".png") };
    private bool _attached, _completed;
    internal static StartupSmoke? Current { get; private set; }

    internal static int Run(string[] args, Func<int> startNativeApplication)
    {
        StartupSmoke? smoke = null;
        Timer? watchdog = null;
        var result = 1;
        try
        {
            var path = PackageSmoke.ReadReportPath(args, "--smoke-test-startup", "--startup-smoke-report");
            if (PathSafety.IsWithin(AppContext.BaseDirectory, path))
                throw new ArgumentException("Startup diagnostic reports must be outside the application directory.");
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
            smoke = new StartupSmoke(path);
            Current = smoke;
            smoke.WriteReport();
            if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Native startup smoke requires the Windows backend.");
            RequireTemporaryPortablePackage();
            watchdog = new Timer(_ => smoke.AbortForTimeout(), null, TimeSpan.FromSeconds(40), Timeout.InfiniteTimeSpan);
            var exitCode = startNativeApplication();
            smoke._report.ApplicationLifetimeExitCode = exitCode;
            if (!smoke._completed || !smoke._report.WindowOpened || !smoke._report.WindowClosed)
                throw new InvalidOperationException("Native startup window did not complete its lifecycle.");
            if (smoke._report.StartupErrorCode is null && exitCode == 0) result = 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"STARTUP SMOKE FAILED: {exception}");
            if (smoke is not null) smoke._report.Error = exception.ToString();
        }
        finally
        {
            watchdog?.Dispose();
            Current = null;
            if (smoke is not null)
            {
                smoke._report.Status = result == 0 ? "passed" : "failed";
                smoke._report.ExitCode = result;
                smoke._report.FinishedUtc = DateTimeOffset.UtcNow;
                try { smoke.WriteReport(); }
                catch (Exception exception) { Console.Error.WriteLine(exception.Message); result = 1; }
            }
        }
        return result;
    }

    internal void Attach(IClassicDesktopStyleApplicationLifetime desktop, Window window, WorkbenchViewModel? viewModel, ApplicationStartupException? failure)
    {
        if (_attached) throw new InvalidOperationException("Startup diagnostics may attach to only one native window.");
        _attached = true;
        if (viewModel is not null)
        {
            _report.DataMode = viewModel.DataLocation.Mode;
            _report.UserDataDirectory = viewModel.DataLocation.DirectoryPath;
            _report.DatabasePath = viewModel.DataLocation.IndexPath;
            _report.SourceSettingsPath = viewModel.DataLocation.SettingsPath;
            _report.SourceNetworkingEnabled = viewModel.IsRealSource;
        }
        if (failure is not null)
        {
            _report.StartupErrorCode = failure.Code;
            _report.StartupError = failure.Message;
            _report.ErrorWindowShown = true;
            if (failure.Code.StartsWith("portable_", StringComparison.Ordinal))
            {
                _report.DataMode = "portable";
                _report.UserDataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
                _report.DatabasePath = Path.Combine(_report.UserDataDirectory, "index.db");
                _report.SourceSettingsPath = Path.Combine(_report.UserDataDirectory, "metadata-source.json");
            }
        }
        window.Closed += (_, _) => _report.WindowClosed = true;
        window.Opened += async (_, _) =>
        {
            var exitCode = 1;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var handle = window.TryGetPlatformHandle();
                if (handle is not { Handle: not 0, HandleDescriptor: "HWND" } || !window.IsVisible)
                    throw new InvalidOperationException("Startup diagnostics require a visible native Win32 HWND.");
                _report.NativeBackend = "Win32";
                _report.NativeHandleDescriptor = handle.HandleDescriptor;
                _report.WindowOpened = true;
                window.UpdateLayout();
                var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.RequestAnimationFrame(_ => rendered.TrySetResult());
                await rendered.Task.WaitAsync(timeout.Token);
                await Task.Delay(50, timeout.Token);
                window.UpdateLayout();
                using (var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96)))
                using (var stream = new FileStream(_report.ScreenshotPath, FileMode.CreateNew, FileAccess.Write))
                { bitmap.Render(window); bitmap.Save(stream); }
                _report.WindowRendered = true;
                if (viewModel is not null)
                {
                    if (viewModel.AllCount != 0 || viewModel.IsBusy || !File.Exists(viewModel.DataLocation.IndexPath))
                        throw new InvalidOperationException("Startup diagnostics must remain idle with an empty media selection and an opened index.");
                    await viewModel.DisposeAsync();
                }
                _completed = true;
                exitCode = failure is null ? 0 : 1;
            }
            catch (Exception exception) { _report.Error = exception.ToString(); }
            finally { desktop.Shutdown(exitCode); }
        };
    }

    private static void RequireTemporaryPortablePackage()
    {
        var roots = new[] { Path.GetTempPath(), Environment.GetEnvironmentVariable("RUNNER_TEMP") };
        if (!roots.Any(root => root is not null && Path.IsPathFullyQualified(root) && PathSafety.IsWithin(root, AppContext.BaseDirectory)))
            throw new InvalidOperationException("Default-data startup diagnostics require a disposable portable package under system/runner temp.");
        try
        {
            if (!ApplicationDataLocation.Resolve(AppContext.BaseDirectory).IsPortable)
                throw new InvalidOperationException("Default-data startup diagnostics cannot open installed/development data.");
        }
        catch (ApplicationStartupException failure) when (failure.Code.StartsWith("portable_", StringComparison.Ordinal))
        {
            // The package is explicitly portable but broken. Let normal app startup
            // render its real error window; this pass itself never opens the index.
        }
    }

    private void WriteReport()
    {
        lock (_reportGate) File.WriteAllText(reportPath, JsonSerializer.Serialize(_report, JsonOptions));
    }

    private void AbortForTimeout()
    {
        lock (_reportGate)
        {
            _report.Status = "failed";
            _report.ExitCode = 124;
            _report.Error = "Native startup diagnostics exceeded the 40-second deadline.";
            _report.FinishedUtc = DateTimeOffset.UtcNow;
            try { WriteReport(); } catch (Exception exception) { Console.Error.WriteLine(exception.Message); }
        }
        Environment.Exit(124);
    }

    private sealed class Report
    {
        public int SchemaVersion { get; init; } = 1;
        public string Status { get; set; } = "running";
        public int ExitCode { get; set; } = 1;
        public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? FinishedUtc { get; set; }
        public string AppDirectory { get; init; } = AppContext.BaseDirectory;
        public string WorkingDirectory { get; init; } = Environment.CurrentDirectory;
        public string? DataMode { get; set; }
        public string? UserDataDirectory { get; set; }
        public string? DatabasePath { get; set; }
        public string? SourceSettingsPath { get; set; }
        public bool? SourceNetworkingEnabled { get; set; }
        public string? StartupErrorCode { get; set; }
        public string? StartupError { get; set; }
        public bool ErrorWindowShown { get; set; }
        public string? NativeBackend { get; set; }
        public string? NativeHandleDescriptor { get; set; }
        public bool WindowOpened { get; set; }
        public bool WindowRendered { get; set; }
        public bool WindowClosed { get; set; }
        public int? ApplicationLifetimeExitCode { get; set; }
        public string ScreenshotPath { get; init; } = "";
        public string? Error { get; set; }
    }
}

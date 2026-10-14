using Avalonia;
using Avalonia.Headless;

namespace Organizer.Desktop;

internal static class Program
{
    public static StartupOptions Options { get; private set; } = new();

    [STAThread]
    public static void Main(string[] args)
    {
        // Package diagnostics must establish their isolated paths before any
        // normal application options or user index can be opened.
        if (args.Contains("--smoke-test-package", StringComparer.Ordinal))
        {
            Environment.ExitCode = Diagnostics.PackageSmoke.Run(args, options =>
            {
                Options = options;
                // Deliberately use the real platform backend, never Headless.
                return BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
            });
            return;
        }
        Options = StartupOptions.Parse(args);
        if (args.Contains("--smoke-test-ui", StringComparer.Ordinal))
        {
            Environment.ExitCode = Diagnostics.WorkbenchSmoke.RunAsync(Options).GetAwaiter().GetResult();
            return;
        }
        var app = BuildAvaloniaApp();
        if (Options.ScreenshotPath is not null || Options.SmokeTestView)
            app.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia();
        app.StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}

public sealed record StartupOptions(string? DemoPath = null, string? WorkerPath = null,
    string PythonExecutable = "python3", string? ScreenshotPath = null, bool ScreenshotPlan = false, string? IndexPath = null,
    bool ScreenshotFixture = false, int ScreenshotWidth = 1440, int ScreenshotHeight = 900, double FontScale = 1, bool SmokeTestView = false)
{
    public static StartupOptions Parse(string[] args)
    {
        string? Read(string flag)
        {
            var index = Array.IndexOf(args, flag);
            return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[index + 1] : null;
        }
        var screenshot = Read("--screenshot");
        var smokeView = args.Contains("--smoke-test-view", StringComparer.Ordinal);
        int Dimension(string flag, int fallback, int minimum) => int.TryParse(Read(flag), out var value) ? Math.Clamp(value, minimum, 4000) : fallback;
        var scale = double.TryParse(Read("--font-scale"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fontScale) && double.IsFinite(fontScale) ? Math.Clamp(fontScale, 0.85, 1.5) : 1;
        // Rendering always uses a fresh index, never the user's saved reviews.
        var index = screenshot is null && !smokeView ? null : Path.Combine(Path.GetTempPath(), $"mdcx-screenshot-{Guid.NewGuid():N}", "index.db");
        var demo = screenshot is not null && args.Contains("--screenshot-empty", StringComparer.Ordinal) ? null : Read("--demo");
        return new(demo, Read("--worker"), Read("--python") ?? ResolveDefaultPythonExecutable(AppContext.BaseDirectory, OperatingSystem.IsWindows()), screenshot,
            args.Contains("--screenshot-plan", StringComparer.Ordinal), index,
            args.Contains("--screenshot-fixture", StringComparer.Ordinal), Dimension("--screenshot-width", 1440, 1160), Dimension("--screenshot-height", 900, 740), scale, smokeView);
    }

    internal static string BundledPythonPath => Path.Combine(AppContext.BaseDirectory, "python", "python.exe");

    private static bool HasInstalledLayout(string baseDirectory) => Directory.Exists(Path.Combine(baseDirectory, "python"))
        || File.Exists(Path.Combine(baseDirectory, "worker", "main.py"));

    internal static string ResolveDefaultPythonExecutable(string baseDirectory, bool isWindows) =>
        isWindows && HasInstalledLayout(baseDirectory)
            ? Path.Combine(baseDirectory, "python", "python.exe")
            : isWindows ? "python" : "python3";

    public string ResolveWorkerPath()
    {
        if (!string.IsNullOrWhiteSpace(WorkerPath)) return Path.GetFullPath(WorkerPath);
        return ResolveDefaultWorkerPath(AppContext.BaseDirectory, Environment.CurrentDirectory);
    }

    internal static string ResolveDefaultWorkerPath(string baseDirectory, string workingDirectory)
    {
        var installedWorker = Path.GetFullPath(Path.Combine(baseDirectory, "worker", "main.py"));
        // An installed bundle must never accidentally run a worker from the
        // current directory or an unrelated checkout if its own worker is lost.
        if (HasInstalledLayout(baseDirectory)) return installedWorker;
        foreach (var start in new[] { baseDirectory, workingDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                foreach (var relative in new[] { "worker/main.py", "organizer/worker/main.py" })
                {
                    // GetFullPath normalizes the source-relative forward slashes
                    // to Windows separators as well as removing dot segments.
                    var candidate = Path.GetFullPath(Path.Combine(dir.FullName, relative));
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }
        return installedWorker;
    }
}

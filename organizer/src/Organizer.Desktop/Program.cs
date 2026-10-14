using Avalonia;
using Avalonia.Headless;

namespace Organizer.Desktop;

internal static class Program
{
    public static StartupOptions Options { get; private set; } = new();

    [STAThread]
    public static void Main(string[] args)
    {
        Options = StartupOptions.Parse(args);
        if (args.Contains("--smoke-test-ui", StringComparer.Ordinal))
        {
            Environment.ExitCode = Diagnostics.WorkbenchSmoke.RunAsync(Options).GetAwaiter().GetResult();
            return;
        }
        var app = BuildAvaloniaApp();
        if (Options.ScreenshotPath is not null)
            app.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia();
        app.StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}

public sealed record StartupOptions(string? DemoPath = null, string? WorkerPath = null,
    string PythonExecutable = "python3", string? ScreenshotPath = null, bool ScreenshotPlan = false, string? IndexPath = null)
{
    public static StartupOptions Parse(string[] args)
    {
        string? Read(string flag)
        {
            var index = Array.IndexOf(args, flag);
            return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[index + 1] : null;
        }
        return new(Read("--demo"), Read("--worker"), Read("--python") ?? (OperatingSystem.IsWindows() ? "python" : "python3"), Read("--screenshot"), args.Contains("--screenshot-plan", StringComparer.Ordinal));
    }

    public string ResolveWorkerPath()
    {
        if (!string.IsNullOrWhiteSpace(WorkerPath)) return Path.GetFullPath(WorkerPath);
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                foreach (var relative in new[] { "worker/main.py", "organizer/worker/main.py" })
                {
                    var candidate = Path.Combine(dir.FullName, relative);
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }
        return Path.Combine(AppContext.BaseDirectory, "worker", "main.py");
    }
}

using Avalonia;
using Avalonia.Headless;
using Organizer.Desktop.Configuration;

namespace Organizer.Desktop;

internal static class Program
{
    public static StartupOptions Options { get; private set; } = new();

    internal static int? DiagnosticExitCode { get; set; }

    internal static ApplicationStartupException? StartupFailure { get; private set; }

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
        if (args.Contains("--smoke-test-startup", StringComparer.Ordinal))
        {
            Environment.ExitCode = Diagnostics.StartupSmoke.Run(args, () =>
            {
                Options = StartupOptions.Parse([]);
                return BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
            });
            return;
        }
        try { Options = StartupOptions.Parse(args); }
        catch (ArgumentException exception)
        { StartupFailure = new ApplicationStartupException("startup_arguments_invalid", exception.Message, exception); }
        if (args.Contains("--smoke-test-ui", StringComparer.Ordinal))
        {
            Environment.ExitCode = Diagnostics.WorkbenchSmoke.RunAsync(Options).GetAwaiter().GetResult();
            return;
        }
        var app = BuildAvaloniaApp();
        if (Options.ScreenshotPath is not null || Options.SmokeTestView)
            app.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia();
        var exitCode = app.StartWithClassicDesktopLifetime(args);
        Environment.ExitCode = DiagnosticExitCode ?? exitCode;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}

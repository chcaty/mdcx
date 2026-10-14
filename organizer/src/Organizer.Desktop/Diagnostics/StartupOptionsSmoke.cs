namespace Organizer.Desktop.Diagnostics;

/// <summary>Cross-platform checks of Windows package resolution; placeholder executables are never run.</summary>
internal static class StartupOptionsSmoke
{
    internal static void Run(string ownedTemporaryRoot, Action<bool, string> check)
    {
        var root = Path.Combine(ownedTemporaryRoot, "runtime-paths");
        var application = Path.Combine(root, "installed");
        var checkout = Path.Combine(root, "checkout");
        var developmentWorker = Path.Combine(checkout, "organizer", "worker", "main.py");
        var bundledPython = Path.Combine(application, "python", "python.exe");
        var bundledWorker = Path.Combine(application, "worker", "main.py");
        Directory.CreateDirectory(Path.GetDirectoryName(developmentWorker)!);
        Directory.CreateDirectory(Path.GetDirectoryName(bundledPython)!);
        Directory.CreateDirectory(Path.GetDirectoryName(bundledWorker)!);
        File.WriteAllText(developmentWorker, "# Inert development lookup fixture.\n");
        File.WriteAllText(bundledPython, "Inert path-resolution fixture, never executable.\n");
        File.WriteAllText(bundledWorker, "# Inert installed lookup fixture.\n");
        check(StartupOptions.ResolveDefaultPythonExecutable(application, isWindows: true) == bundledPython,
            "Windows installed startup selects the absolute bundled interpreter without PATH lookup");
        check(StartupOptions.ResolveDefaultWorkerPath(application, checkout) == bundledWorker,
            "Installed worker takes precedence over an unrelated current-directory checkout");

        File.Delete(bundledWorker);
        check(StartupOptions.ResolveDefaultWorkerPath(application, checkout) == bundledWorker,
            "Missing installed worker cannot fall back to a different current-directory worker");
        File.Delete(bundledPython);
        check(StartupOptions.ResolveDefaultPythonExecutable(application, isWindows: true) == bundledPython,
            "Missing bundled interpreter fails closed while its runtime directory exists");
        File.WriteAllText(bundledWorker, "# Inert installed lookup fixture.\n");
        Directory.Delete(Path.GetDirectoryName(bundledPython)!);
        check(StartupOptions.ResolveDefaultPythonExecutable(application, isWindows: true) == bundledPython,
            "Installed worker identifies a broken package even when the entire runtime directory is missing");

        File.Delete(bundledWorker);
        check(StartupOptions.ResolveDefaultPythonExecutable(application, isWindows: true) == "python"
              && StartupOptions.ResolveDefaultPythonExecutable(application, isWindows: false) == "python3",
            "Unpackaged development startup keeps platform Python command defaults");
        check(StartupOptions.ResolveDefaultWorkerPath(application, checkout) == developmentWorker,
            "Unpackaged development startup can still find the current-directory checkout worker");
        var developmentOutput = Path.Combine(checkout, "organizer", "src", "Desktop", "bin");
        Directory.CreateDirectory(developmentOutput);
        check(StartupOptions.ResolveDefaultWorkerPath(developmentOutput, application) == developmentWorker,
            "Development worker resolves from application-directory ancestors independently of current directory");
        var overrides = StartupOptions.Parse(["--python", "explicit-development-python", "--worker", developmentWorker]);
        check(overrides.PythonExecutable == "explicit-development-python" && overrides.ResolveWorkerPath() == developmentWorker,
            "Explicit Python and worker overrides preserve intentional development support");
    }
}

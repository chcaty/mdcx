using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Organizer.Desktop.ViewModels;
using Organizer.Desktop.Configuration;

namespace Organizer.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                if (Program.StartupFailure is { } failure) throw failure;
                var viewModel = new WorkbenchViewModel(Program.Options);
                var window = new MainWindow { DataContext = viewModel };
                desktop.MainWindow = window;
                desktop.Exit += (_, _) => viewModel.Dispose();
                Diagnostics.PackageSmoke.Current?.Attach(desktop, window, viewModel);
                Diagnostics.StartupSmoke.Current?.Attach(desktop, window, viewModel, null);
            }
            catch (ApplicationStartupException failure)
            {
                Console.Error.WriteLine($"STARTUP FAILED ({failure.Code}): {failure.Message}");
                var window = new StartupErrorWindow(failure);
                desktop.MainWindow = window;
                desktop.Exit += (_, args) => args.ApplicationExitCode = 1;
                Diagnostics.StartupSmoke.Current?.Attach(desktop, window, null, failure);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}

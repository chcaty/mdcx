using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Organizer.Desktop.ViewModels;

namespace Organizer.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new WorkbenchViewModel(Program.Options);
            var window = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => viewModel.Dispose();
            Diagnostics.PackageSmoke.Current?.Attach(desktop, window, viewModel);
        }
        base.OnFrameworkInitializationCompleted();
    }
}

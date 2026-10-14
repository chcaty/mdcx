using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Organizer.Desktop.ViewModels;

namespace Organizer.Desktop;

public partial class SourceSettingsWindow : Window
{
    private readonly WorkbenchViewModel? _workbench;
    public SourceSettingsWindow()
    {
        InitializeComponent();
        Opened += (_, _) => SourceModePicker.Focus();
        AddHandler(KeyDownEvent, (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(); } }, RoutingStrategies.Tunnel);
    }
    public SourceSettingsWindow(WorkbenchViewModel workbench) : this()
    {
        _workbench = workbench;
        DataContext = workbench.BeginSourceSettings();
    }
    private void SaveSettings(object? sender, RoutedEventArgs args)
    {
        if (DataContext is SourceSettingsViewModel draft && _workbench?.SaveSourceSettings(draft) == true) Close();
    }
    private void ResetSettings(object? sender, RoutedEventArgs args)
    {
        if (DataContext is SourceSettingsViewModel draft && _workbench?.ResetSourceSettings(draft) == true) Close();
    }
    private void CancelSettings(object? sender, RoutedEventArgs args) => Close();
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Organizer.Desktop.ViewModels;

namespace Organizer.Desktop;

public partial class MainWindow : Window
{
    private bool _pickerOpen;
    private WorkbenchViewModel? ViewModel => DataContext as WorkbenchViewModel;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        KeyDown += OnKeyDown;
        Closed += (_, _) => ViewModel?.Cancel();
    }

    private async void OnOpened(object? sender, EventArgs args)
    {
        if (ViewModel is not { } vm) return;
        await vm.InitializeAsync();
        if (Program.Options.ScreenshotPath is not { } screenshot) return;
        try
        {
            vm.SelectedAsset = vm.Assets.FirstOrDefault(a => a.Asset.Identifier == "DEMO-001") ?? vm.SelectedAsset;
            if (vm.SelectedAsset is not null)
            {
                await vm.MatchAsync();
                if (vm.SelectedCandidate is not null) await vm.AcceptCandidateAsync();
                await vm.BuildPlanAsync();
            }
            if (Program.Options.ScreenshotPlan) InspectorTabs.SelectedIndex = 1;
            await Task.Delay(150);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                UpdateLayout();
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96));
                bitmap.Render(this);
                bitmap.Save(Path.GetFullPath(screenshot));
            }, DispatcherPriority.Render);
        }
        catch (Exception ex) { Console.Error.WriteLine($"Screenshot failed: {ex.Message}"); Environment.ExitCode = 1; }
        finally { Close(); }
    }

    private async void ChooseSource(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => await ChooseFolderAsync(false);
    private async void ChooseTarget(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => await ChooseFolderAsync(true);

    private async Task ChooseFolderAsync(bool target)
    {
        if (_pickerOpen || ViewModel is not { IsIdle: true } vm) return;
        _pickerOpen = true;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = target ? "选择预览目标目录" : "选择媒体源文件夹",
                AllowMultiple = false
            });
            var path = folders.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;
            if (target) vm.TargetPath = path;
            else
            {
                vm.SourcePath = path;
                if (string.IsNullOrWhiteSpace(vm.TargetPath)) vm.TargetPath = Path.Combine(Path.GetDirectoryName(path) ?? path, "organized-preview");
            }
        }
        catch (Exception ex)
        {
            var dialog = new Window
            {
                Title = "无法打开文件夹选择器",
                Width = 460,
                Height = 180,
                CanResize = false,
                Content = new TextBlock { Text = $"{ex.Message}\n\n可直接在路径输入框填写本地文件夹。", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(22) }
            };
            await dialog.ShowDialog(this);
        }
        finally { _pickerOpen = false; }
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (e.Key == Key.Escape) { vm.Cancel(); e.Handled = true; }
        else if (e.Key == Key.F5) { vm.ScanCommand.Execute(null); e.Handled = true; }
        else if (control && e.Key == Key.O) { e.Handled = true; await ChooseFolderAsync(false); }
        else if (control && e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (control && e.Key == Key.Enter) { vm.SaveReviewCommand.Execute(null); e.Handled = true; }
        else if (control && e.Key == Key.P) { InspectorTabs.SelectedIndex = 1; vm.BuildPlanCommand.Execute(null); e.Handled = true; }
    }
}

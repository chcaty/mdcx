using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Organizer.Desktop.ViewModels;

namespace Organizer.Desktop;

public partial class MainWindow : Window
{
    private bool _pickerOpen;
    private AssetRow? _mediaSelectionBeforePlan;
    private WorkbenchViewModel? ViewModel => DataContext as WorkbenchViewModel;

    public MainWindow()
    {
        ApplyFontScale();
        InitializeComponent();
        Width = Program.Options.ScreenshotWidth;
        Height = Program.Options.ScreenshotHeight;
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        CenterScroller.SizeChanged += (_, _) => UpdateResponsiveLayout();
        Opened += OnOpened;
        KeyDown += OnKeyDown;
        Closed += (_, _) => ViewModel?.Cancel();
    }

    private static void ApplyFontScale()
    {
        if (Application.Current is not { } app) return;
        var sizes = new Dictionary<string, double> { ["FontTiny"] = 11.5, ["FontSmall"] = 12, ["FontBody"] = 13, ["FontMedium"] = 14, ["FontTitle"] = 16, ["FontSection"] = 18, ["FontHero"] = 20, ["FontPage"] = 26 };
        foreach (var (key, size) in sizes) app.Resources[key] = size * Program.Options.FontScale;
    }

    private async void OnOpened(object? sender, EventArgs args)
    {
        if (ViewModel is not { } vm) return;
        vm.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) => vm.PropertyChanged -= OnViewModelChanged;
        await vm.InitializeAsync();
        UpdateResponsiveLayout();
        if (Program.Options.SmokeTestView)
        {
            try { Environment.ExitCode = await Diagnostics.ViewInteractionSmoke.RunAsync(this, vm); }
            catch (Exception ex) { Console.Error.WriteLine($"View smoke failed: {ex}"); Environment.ExitCode = 1; }
            finally { Close(); }
            return;
        }
        if (Program.Options.ScreenshotPath is not { } screenshot) return;
        try
        {
            // Screenshot seeding is opt-in and restricted to verified neutral fixtures.
            // Arbitrary --demo folders are only scanned, never auto-reviewed.
            if (Program.Options.ScreenshotFixture && IsVerifiedScreenshotFixture(vm)) await SeedScreenshotAsync(vm);
            if (Program.Options.ScreenshotPlan)
            {
                SetWorkspace(true);
                await vm.BuildPlanAsync();
            }
            await Task.Delay(200);
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

    private static bool IsVerifiedScreenshotFixture(WorkbenchViewModel vm)
    {
        const string marker = "MDCx Organizer synthetic screenshot fixtures v1\n";
        const string content = "Synthetic fixture. This is not playable media.\n";
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "DEMO-001.mkv", "DEMO-002.1080p.mp4", "DEMO-002.2160p.mp4", "旅行日记.mkv" };
        var markerPath = Path.Combine(vm.SourcePath, ".mdcx-synthetic-fixture");
        if (!File.Exists(markerPath) || new FileInfo(markerPath).Length > 128 || File.ReadAllText(markerPath) != marker) return false;
        return vm.Assets.Count > 0 && vm.Assets.All(row => allowed.Contains(row.Filename)
            && new FileInfo(row.Asset.SourcePath).Length == System.Text.Encoding.UTF8.GetByteCount(content)
            && File.ReadAllText(row.Asset.SourcePath) == content);
    }

    private static async Task SeedScreenshotAsync(WorkbenchViewModel vm)
    {
        var rows = vm.Assets.ToArray();
        var reviewed = rows.FirstOrDefault(row => row.Filename == "DEMO-002.2160p.mp4");
        if (reviewed is not null)
        {
            vm.SelectedAsset = reviewed;
            await vm.MatchAsync();
            if (vm.SelectedCandidate is not null) await vm.AcceptCandidateAsync();
        }
        var second = rows.FirstOrDefault(row => row.Filename == "DEMO-002.1080p.mp4");
        if (second is not null)
        {
            vm.SelectedAsset = second;
            await vm.MatchAsync();
            second.IsChecked = true;
        }
        var unknown = rows.FirstOrDefault(row => row.Filename == "旅行日记.mkv");
        if (unknown is not null) { vm.SelectedAsset = unknown; await vm.MatchAsync(); }
        var first = rows.FirstOrDefault(row => row.Filename == "DEMO-001.mkv");
        if (first is not null)
        {
            vm.SelectedAsset = first;
            first.IsChecked = true;
            await vm.MatchAsync();
        }
    }

    private void UpdateResponsiveLayout()
    {
        var wrap = Bounds.Width < 1300 && Program.Options.FontScale > 1.1;
        // Never squeeze fixed toolbars into the media rows. At small windows or
        // larger type, the workspace itself scrolls while the inspector stays usable.
        CenterWorkspace.Height = Math.Max(640 * Program.Options.FontScale + (wrap ? 40 : 0), CenterScroller.Bounds.Height - 50);
        FilterBar.RowDefinitions = new RowDefinitions(wrap ? "Auto,Auto" : "Auto");
        Grid.SetColumnSpan(FilterGroup, wrap ? 2 : 1);
        Grid.SetColumn(SearchBox, wrap ? 0 : 1);
        Grid.SetRow(SearchBox, wrap ? 1 : 0);
        Grid.SetColumnSpan(SearchBox, wrap ? 2 : 1);
        SearchBox.Margin = wrap ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        SearchBox.HorizontalAlignment = wrap ? Avalonia.Layout.HorizontalAlignment.Stretch : Avalonia.Layout.HorizontalAlignment.Right;
        SearchBox.Width = wrap ? double.NaN : 298;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (ViewModel is not { } vm) return;
        if (args.PropertyName == nameof(WorkbenchViewModel.SelectedFilter)) UpdateFilterButtons();
        if (args.PropertyName == nameof(WorkbenchViewModel.SelectedAsset)) UpdatePlanInspector();
        if (args.PropertyName == nameof(WorkbenchViewModel.SelectedAsset) && vm.SelectedAsset is { } selected)
            MediaGrid.ScrollIntoView(selected);
        if (args.PropertyName == nameof(WorkbenchViewModel.CandidateStatus) && vm.HasCandidates && vm.SelectedCandidate is null)
            SearchDetails.IsExpanded = true;
    }

    private void ToggleVisibleChecks(object? sender, RoutedEventArgs args)
    {
        // One-way checkbox display keeps the VM's pre-click mixed/all state intact.
        // This makes both pointer and Space deterministic: mixed/none → all → none.
        if (ViewModel is { } vm) vm.VisibleCheckState = vm.VisibleCheckState != true;
    }

    private void ChangeFilter(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string filter } && ViewModel is { } vm) vm.SelectedFilter = filter;
        UpdateFilterButtons();
    }
    private void UpdateFilterButtons()
    {
        foreach (var button in new[] { FilterAll, FilterPending, FilterReviewed, FilterAttention })
            button.Classes.Set("active", Equals(button.Tag, ViewModel?.SelectedFilter));
    }
    private void ShowMedia(object? sender, RoutedEventArgs args) => SetWorkspace(false);
    private void ShowPlan(object? sender, RoutedEventArgs args) => SetWorkspace(true);
    private void SetWorkspace(bool plan)
    {
        if (ViewModel is { } vm)
        {
            if (plan && !PlanWorkspace.IsVisible)
            {
                _mediaSelectionBeforePlan = vm.SelectedAsset;
                vm.SelectedAsset = vm.Assets.FirstOrDefault(row => row.Review is not null) ?? vm.SelectedAsset;
            }
            else if (!plan && PlanWorkspace.IsVisible && _mediaSelectionBeforePlan is { } previous && vm.Assets.Contains(previous))
                vm.SelectedAsset = previous;
        }
        MediaWorkspace.IsVisible = !plan;
        PlanWorkspace.IsVisible = plan;
        MediaNav.Classes.Set("active", !plan);
        PlanNav.Classes.Set("active", plan);
        ReviewActions.IsVisible = !plan;
        PlanInspectorFooter.IsVisible = plan;
        CandidatePositionText.IsVisible = !plan;
        PlanCandidatePositionText.IsVisible = plan;
        EditToggle.IsVisible = !plan;
        SearchDetails.IsVisible = !plan;
        if (plan) EditorPanel.IsVisible = false;
        UpdatePlanInspector();
    }
    private void UpdatePlanInspector()
    {
        var reviewed = ViewModel?.SelectedAsset?.Review is not null;
        EvidenceExplanation.Text = reviewed ? "已人工确认；资料来源与原始证据仍可展开核查。" : "候选仍需人工核对；来源证据不代表自动审核。";
        InspectorTitle.Text = !PlanWorkspace.IsVisible ? "资料核对" : reviewed ? "已审核资料" : "当前文件资料";
        PlanInspectorNote.Text = reviewed ? "此项已通过人工审核。计划仍以左侧路径检查结果为准。" : "当前文件尚未审核，未加入整理计划。返回媒体文件以继续核对。";
    }
    private void ToggleLogs(object? sender, RoutedEventArgs args) => LogsPanel.IsVisible = !LogsPanel.IsVisible;
    private void ToggleEditor(object? sender, RoutedEventArgs args) => EditorPanel.IsVisible = !EditorPanel.IsVisible;
    private void MinimizeWindow(object? sender, RoutedEventArgs args) => WindowState = WindowState.Minimized;
    private void MaximizeWindow(object? sender, RoutedEventArgs args) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow(object? sender, RoutedEventArgs args) => Close();
    private void DragTitleBar(object? sender, PointerPressedEventArgs args)
    {
        if (args.Source is Button || args.Source is Visual visual && visual.GetVisualAncestors().OfType<Button>().Any() || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (args.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else BeginMoveDrag(args);
    }

    private async void ChooseSource(object? sender, RoutedEventArgs args) => await ChooseFolderAsync(false);
    private async void ChooseTarget(object? sender, RoutedEventArgs args) => await ChooseFolderAsync(true);
    private async void ShowSourcePath(object? sender, RoutedEventArgs args) => await ShowPathDialogAsync(false, false);
    private async void ShowTargetPath(object? sender, RoutedEventArgs args) => await ShowPathDialogAsync(true, false);
    private async void ShowSelectedPath(object? sender, RoutedEventArgs args) => await ShowPathDialogAsync(false, true);

    private async Task ShowPathDialogAsync(bool target, bool selectedFile)
    {
        if (ViewModel is not { } vm) return;
        var path = selectedFile ? vm.SelectedPath : target ? vm.TargetPath : vm.SourcePath;
        var editor = new TextBox { Text = path, IsReadOnly = selectedFile, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, MinHeight = 76 };
        var title = selectedFile ? "原文件完整路径" : target ? "整理目标目录" : "媒体源目录";
        var dialog = new Window { Title = title, Width = 650, SizeToContent = SizeToContent.Height, MinWidth = 400, MaxHeight = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush.Parse("#F8F9F6") };
        var close = new Button { Content = "关闭" };
        close.Click += (_, _) => dialog.Close();
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 9, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        var copy = new Button { Content = "复制完整路径" };
        copy.Click += async (_, _) => { if (Clipboard is { } clipboard) await clipboard.SetTextAsync(editor.Text ?? ""); };
        buttons.Children.Add(copy);
        if (!selectedFile)
        {
            var apply = new Button { Content = "使用此目录", IsEnabled = vm.IsIdle };
            apply.Click += (_, _) => { if (target) vm.TargetPath = editor.Text?.Trim() ?? ""; else vm.SourcePath = editor.Text?.Trim() ?? ""; dialog.Close(); };
            buttons.Children.Add(apply);
        }
        buttons.Children.Add(close);
        var content = new StackPanel { Margin = new Thickness(22), Spacing = 14 };
        content.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold });
        content.Children.Add(editor);
        content.Children.Add(new TextBlock { Text = selectedFile ? "可选中并复制完整路径。原文件保持不变。" : target ? "只检查拟议路径，不创建目录或移动文件。" : "填写目录后点击“扫描文件”。扫描不会更改源文件。", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(buttons);
        dialog.Content = content;
        await dialog.ShowDialog(this);
    }

    private async Task ChooseFolderAsync(bool target)
    {
        if (_pickerOpen || ViewModel is not { IsIdle: true } vm) return;
        _pickerOpen = true;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = target ? "选择预览目标目录" : "选择媒体源文件夹", AllowMultiple = false });
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
            var dialog = new Window { Title = "无法打开文件夹选择器", Width = 460, Height = 190, CanResize = false, Content = new TextBlock { Text = $"{ex.Message}\n\n可通过“查看完整路径”直接填写本地目录。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22) } };
            await dialog.ShowDialog(this);
        }
        finally { _pickerOpen = false; }
    }

    private async void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (ViewModel is not { } vm) return;
        var control = args.KeyModifiers.HasFlag(KeyModifiers.Control) || args.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (args.Key == Key.Escape) { vm.Cancel(); args.Handled = true; }
        else if (args.Key == Key.F5) { vm.ScanCommand.Execute(null); args.Handled = true; }
        else if (control && args.Key == Key.O) { args.Handled = true; await ChooseFolderAsync(false); }
        else if (control && args.Key == Key.F) { SetWorkspace(false); SearchBox.Focus(); SearchBox.SelectAll(); args.Handled = true; }
        else if (control && args.Key == Key.Enter) { vm.ConfirmAndNextCommand.Execute(null); args.Handled = true; }
        else if (control && args.Key == Key.P) { SetWorkspace(true); vm.BuildPlanCommand.Execute(null); args.Handled = true; }
    }
}

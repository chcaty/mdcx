using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Media.Imaging;
using Organizer.Desktop.ViewModels;

namespace Organizer.Desktop.Diagnostics;

/// <summary>Real Avalonia controls and routed headless input; no metadata acceptance or media writes.</summary>
internal static class ViewInteractionSmoke
{
    public static async Task<int> RunAsync(MainWindow window, WorkbenchViewModel vm)
    {
        var checks = 0;
        void Check(bool result, string message)
        {
            if (!result) throw new InvalidOperationException(message);
            Console.WriteLine($"VIEW PASS {++checks}: {message}");
        }
        async Task Pump()
        {
            await Task.Delay(35);
            window.UpdateLayout();
        }
        async Task Click(Control control, double? x = null)
        {
            var point = control.TranslatePoint(new Point(x ?? control.Bounds.Width / 2, control.Bounds.Height / 2), window)
                ?? throw new InvalidOperationException("Control is not attached to the rendered window.");
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            await Pump();
        }
        void SendKey(Key key, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            window.KeyPress(key, modifiers, PhysicalKey.None, "");
            window.KeyRelease(key, modifiers, PhysicalKey.None, "");
        }
        try
        {
            await Pump();
            await StartupErrorViewSmoke.RunAsync(window, Check);
            var settingsButton = window.FindControl<Button>("SourceSettingsButton")!;
            await Click(settingsButton);
            var settingsWindow = window.ActiveSourceSettings ?? throw new InvalidOperationException("Source settings did not open.");
            await Pump(); settingsWindow.UpdateLayout();
            var settingsDraft = (SourceSettingsViewModel)settingsWindow.DataContext!;
            Check(settingsWindow.IsVisible && !vm.IsRealSource && settingsDraft.Mode == SourceSettingsViewModel.DemoMode,
                "Pointer opens a detached source configuration dialog in demo mode");
            settingsDraft.Mode = SourceSettingsViewModel.RealMode;
            settingsDraft.Proxy = "http://user:secret@localhost:7890";
            await Pump();
            var verificationNote = settingsWindow.FindControl<TextBlock>("LiveVerificationNote")!;
            var notePosition = verificationNote.TranslatePoint(new Point(0, 0), settingsWindow)!.Value;
            Check(verificationNote.Text == "尚未在线验收；可用性取决于来源响应。" && verificationNote.IsVisible && verificationNote.Bounds.Height > 0 &&
                notePosition.Y > 0 && notePosition.Y + verificationNote.Bounds.Height < settingsWindow.Bounds.Height,
                "Real-source settings visibly disclose that online acceptance remains unverified");
            async Task ClickSettings(string name)
            {
                var control = settingsWindow.FindControl<Button>(name)!;
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), settingsWindow)!.Value;
                settingsWindow.MouseMove(point); settingsWindow.MouseDown(point, MouseButton.Left); settingsWindow.MouseUp(point, MouseButton.Left);
                await Pump();
            }
            await ClickSettings("SaveSourceSettingsButton");
            Check(settingsWindow.IsVisible && settingsDraft.HasError && !vm.IsRealSource && vm.Assets.All(asset => !asset.MatchAttempted),
                "Actual save button rejects credential proxies without enabling or starting a match");
            settingsDraft.Proxy = "";
            settingsDraft.Error = "";
            settingsWindow.UpdateLayout();
            var sourceScreenshot = Path.Combine(Path.GetTempPath(), $"mdcx-source-settings-{Guid.NewGuid():N}.png");
            using (var bitmap = new RenderTargetBitmap(new PixelSize((int)settingsWindow.Bounds.Width, (int)settingsWindow.Bounds.Height), new Vector(96, 96)))
            { bitmap.Render(settingsWindow); bitmap.Save(sourceScreenshot); }
            Console.WriteLine($"SOURCE SETTINGS RENDER: {sourceScreenshot}");
            await ClickSettings("CancelSourceSettingsButton");
            Check(window.ActiveSourceSettings is null && !vm.IsRealSource, "Cancel discards real-source edits and returns to the unchanged workbench");
            await Click(settingsButton);
            settingsWindow = window.ActiveSourceSettings!;
            settingsDraft = (SourceSettingsViewModel)settingsWindow.DataContext!;
            settingsDraft.Mode = SourceSettingsViewModel.RealMode;
            await Pump();
            await ClickSettings("SaveSourceSettingsButton");
            Check(window.ActiveSourceSettings is null && vm.IsRealSource && vm.Assets.All(asset => !asset.MatchAttempted) && vm.SourceStatus == "已配置 · 尚未查询",
                "Explicit real-source save updates the workbench without querying or accepting media");
            Check(window.FindControl<TextBlock>("SourceModeTitle")!.Text == "本地工作空间 · 真实来源 · JavBus",
                "Rendered titlebar reflects enabled real mode without claiming offline-only operation");
            await Click(settingsButton);
            settingsWindow = window.ActiveSourceSettings!;
            await ClickSettings("ResetSourceSettingsButton");
            Check(window.ActiveSourceSettings is null && !vm.IsRealSource && vm.Assets.All(asset => !asset.MatchAttempted),
                "Explicit clear button restores demo without a query");
            await Click(settingsButton);
            settingsWindow = window.ActiveSourceSettings!;
            settingsWindow.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, "");
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.None, "");
            await Pump();
            Check(window.ActiveSourceSettings is null && !vm.IsRealSource, "Escape closes settings without changing source mode");
            var list = window.FindControl<ListBox>("MediaGrid")!;
            var search = window.FindControl<TextBox>("SearchBox")!;
            Check(vm.Assets.Count >= 2 && list.Bounds.Height > 100, "Media list is realized with a bounded scroll viewport");
            var first = vm.Assets[0];
            var second = vm.Assets[1];
            vm.SelectedAsset = first;
            await Pump();
            var secondCheck = list.GetVisualDescendants().OfType<CheckBox>().First(control => ReferenceEquals(control.DataContext, second));
            await Click(secondCheck);
            Check(second.IsChecked && vm.CheckedCount == 1, "Pointer checkbox toggles the bound batch selection");
            Check(vm.SelectedAsset == first, "Checkbox pointer input preserves the independently focused row");
            var row = list.ContainerFromIndex(1) ?? throw new InvalidOperationException("Expected second media row to be realized.");
            await Click(row, 95);
            Check(vm.SelectedAsset == second && second.IsChecked && vm.CheckedCount == 1,
                "Pointer row focus does not change checked membership");
            var headerCheck = window.GetVisualDescendants().OfType<CheckBox>().First(control =>
                AutomationProperties.GetName(control) == "勾选或清除当前筛选内的文件");
            await Click(headerCheck);
            Check(vm.CheckedCount == vm.Assets.Count && vm.VisibleCheckState == true,
                "Mixed header checkbox selects every visible row");
            await Click(headerCheck);
            Check(vm.CheckedCount == 0 && vm.VisibleCheckState == false,
                "Checked header checkbox clears every visible row");
            await Click(secondCheck);
            Check(vm.CheckedCount == 1 && second.IsChecked, "Row checkbox remains usable after bulk toggles");
            SendKey(Key.F, RawInputModifiers.Control);
            await Pump();
            Check(search.IsFocused, "Ctrl+F routes to the actual local search box");
            window.KeyTextInput(first.Filename);
            await Pump();
            Check(vm.Assets.Count == 1 && vm.Assets[0] == first && vm.CheckedCount == 1,
                "Keyboard text filters the list while preserving a hidden checked row");
            SendKey(Key.A, RawInputModifiers.Control);
            SendKey(Key.Back);
            await Pump();
            Check(vm.Assets.Count >= 2 && string.IsNullOrEmpty(vm.Search), "Keyboard clearing restores the visible media list");
            SendKey(Key.Tab);
            await Pump();
            Check(!search.IsFocused, "Tab can leave the filter and continue control navigation");
            SendKey(Key.P, RawInputModifiers.Control);
            await Pump();
            Check(window.FindControl<Grid>("PlanWorkspace")!.IsVisible && !window.FindControl<Grid>("MediaWorkspace")!.IsVisible,
                "Ctrl+P opens the real reviewed-only preview workspace");
            Check(vm.PlanOperations.Count == 0 && vm.Assets.All(asset => asset.Review is null),
                "Preview navigation cannot accept unreviewed media");
            await Click(window.FindControl<Button>("MediaNav")!);
            Check(window.FindControl<Grid>("MediaWorkspace")!.IsVisible, "Sidebar navigation returns to the media workspace");
            var edit = window.GetVisualDescendants().OfType<Button>().First(control =>
                control.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "编辑"));
            edit.BringIntoView();
            await Pump();
            await Click(edit);
            Check(window.FindControl<Grid>("EditorPanel")!.IsVisible, "Inspector edit action exposes actual editable fields");
            await Click(edit);
            Check(!window.FindControl<Grid>("EditorPanel")!.IsVisible, "Inspector edit fields collapse without accepting changes");
            var query = window.FindControl<Expander>("SearchDetails")!;
            query.BringIntoView();
            await Pump();
            await Click(query);
            Check(query.IsExpanded, "Source and title-query controls expand through real pointer input");
            query.IsExpanded = false;
            await Pump();
            var logs = window.GetVisualDescendants().OfType<Button>().First(control =>
                AutomationProperties.GetName(control) == "展开或收起任务日志");
            await Click(logs);
            Check(window.FindControl<Border>("LogsPanel")!.IsVisible, "Task logs expand through the rendered button");
            await Click(logs);
            Check(!window.FindControl<Border>("LogsPanel")!.IsVisible, "Task logs collapse without losing the workbench");
            SendKey(Key.Escape);
            await Pump();
            Check(!vm.IsBusy && vm.CheckedCount == 1 && vm.Assets.All(asset => asset.Review is null),
                "Escape preserves selection and never confirms metadata");
            if (window.Bounds.Width <= 1200 && Program.Options.FontScale >= 1.2)
            {
                var center = window.FindControl<ScrollViewer>("CenterScroller")!;
                Check(center.Extent.Height > center.Viewport.Height,
                    "Small window with large text exposes a bounded outer workspace scroll");
                var point = center.TranslatePoint(new Point(50, 40), window)!.Value;
                window.MouseMove(point);
                window.MouseWheel(point, new Vector(0, -100));
                await Pump();
                Check(center.Offset.Y > 0, "Pointer wheel can reach the lower naming draft at large font size");
            }
            Console.WriteLine($"VIEW SMOKE PASSED: {checks} checks");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"VIEW SMOKE FAILED: {ex}");
            return 1;
        }
    }
}

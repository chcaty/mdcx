using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Organizer.Desktop.Configuration;

namespace Organizer.Desktop;

/// <summary>A normal native window for recoverable launch errors; no log file is needed.</summary>
internal sealed class StartupErrorWindow : Window
{
    internal StartupErrorWindow(ApplicationStartupException failure)
    {
        Title = "MDCx Organizer · 无法启动";
        Width = 680;
        Height = 380;
        MinWidth = 480;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush.Parse("#F8F9F6");
        var close = new Button { Name = "StartupErrorClose", Content = "关闭", HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        var content = new DockPanel { Margin = new Thickness(24), LastChildFill = true };
        var title = new TextBlock { Text = "无法打开应用数据", FontSize = 22, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 16) };
        DockPanel.SetDock(title, Dock.Top);
        DockPanel.SetDock(close, Dock.Bottom);
        content.Children.Add(title);
        content.Children.Add(close);
        content.Children.Add(new TextBox
        {
            Name = "StartupErrorMessage", Text = failure.Message, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0), Background = Brushes.Transparent, FontSize = 14,
            Margin = new Thickness(0, 0, 0, 16)
        });
        Content = content;
    }
}

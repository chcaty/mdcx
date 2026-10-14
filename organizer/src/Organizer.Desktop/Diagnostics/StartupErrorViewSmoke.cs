using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Organizer.Desktop.Configuration;

namespace Organizer.Desktop.Diagnostics;

internal static class StartupErrorViewSmoke
{
    internal static async Task RunAsync(Window owner, Action<bool, string> check)
    {
        var failure = new ApplicationStartupException("portable_data_unwritable",
            "便携版无法安全读写数据目录：\nC:\\用户\\示例用户\\文档\\媒体资料与工具 Portable App\\请保留已有审核与配置\\data\n\n" +
            "请将整个程序文件夹解压到可写的位置（例如文档目录），保留其中的 data 文件夹后重新打开。请勿直接在压缩包内运行，也不要将 data 设置为链接。");
        var window = new StartupErrorWindow(failure);
        try
        {
            window.Show(owner);
            await Task.Delay(40);
            window.UpdateLayout();
            var message = window.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "StartupErrorMessage");
            var close = window.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "StartupErrorClose");
            check(window.IsVisible && message.IsReadOnly && message.Text == failure.Message && message.Bounds.Width > 400 && message.Bounds.Height > 100,
                "Startup error UI visibly preserves a long Unicode/space path and actionable recovery text");
            var position = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), window)!.Value;
            check(close.IsVisible && position.X > 0 && position.Y > 0 && position.Y < window.Bounds.Height,
                "Startup error close button remains visible below the bounded message area");
            var screenshot = Path.Combine(Path.GetTempPath(), $"mdcx-startup-error-{Guid.NewGuid():N}.png");
            using (var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96)))
            { bitmap.Render(window); bitmap.Save(screenshot); }
            Console.WriteLine($"STARTUP ERROR RENDER: {screenshot}");
            window.MouseMove(position);
            window.MouseDown(position, MouseButton.Left);
            window.MouseUp(position, MouseButton.Left);
            await Task.Delay(25);
            check(!window.IsVisible && owner.IsVisible, "Actual startup-error close button dismisses the error without affecting another window");
        }
        finally { if (window.IsVisible) window.Close(); }
    }
}

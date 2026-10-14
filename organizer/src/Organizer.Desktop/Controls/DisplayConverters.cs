using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Organizer.Desktop.Controls;

public sealed class PathLeafConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var path = value?.ToString() ?? "";
        if (Equals(parameter, "parent")) path = Path.GetDirectoryName(path) ?? "";
        return string.IsNullOrWhiteSpace(path) ? "未选择目录" : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NonemptyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => !string.IsNullOrWhiteSpace(value?.ToString());
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class StatusBrushConverter : IValueConverter
{
    public bool Foreground { get; set; }
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value?.ToString() ?? "";
        var colors = status.Contains("已审核", StringComparison.Ordinal) ? ("#E7EEDC", "#516B36")
            : status.Contains("检查", StringComparison.Ordinal) ? ("#F3E4D7", "#845935") : ("#F2E9D5", "#785E28");
        return Brush.Parse(Foreground ? colors.Item2 : colors.Item1);
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ScanWarningVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => !string.IsNullOrWhiteSpace(value?.ToString()) && value?.ToString() != "未发现扫描问题";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class IsZeroConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is int count && count == 0;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

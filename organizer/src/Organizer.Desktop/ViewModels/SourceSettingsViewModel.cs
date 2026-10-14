using System.Globalization;
using Organizer.Infrastructure;

namespace Organizer.Desktop.ViewModels;

/// <summary>A detached draft: editing/cancelling this object cannot change the active source.</summary>
public sealed class SourceSettingsViewModel : ObservableObject
{
    public const string DemoMode = "演示（离线样例）";
    public const string RealMode = "真实来源（JavBus）";
    private readonly MetadataSourceSettings _original;
    private string _mode, _proxy, _connectTimeout, _requestTimeout, _interval, _error = "";
    public SourceSettingsViewModel(MetadataSourceSettings settings)
    {
        _original = settings;
        _mode = settings.NetworkEnabled ? RealMode : DemoMode;
        _proxy = settings.Proxy ?? "";
        _connectTimeout = Format(settings.ConnectTimeoutSeconds);
        _requestTimeout = Format(settings.RequestTimeoutSeconds);
        _interval = Format(settings.MinIntervalSeconds);
    }
    public IReadOnlyList<string> Modes { get; } = [DemoMode, RealMode];
    public string Mode { get => _mode; set { if (Set(ref _mode, value)) { Raise(nameof(IsReal)); Raise(nameof(SaveLabel)); } } }
    public bool IsReal => Mode == RealMode;
    public string SaveLabel => IsReal ? "保存并启用真实来源" : "保存演示模式";
    public string Proxy { get => _proxy; set => Set(ref _proxy, value); }
    public string ConnectTimeout { get => _connectTimeout; set => Set(ref _connectTimeout, value); }
    public string RequestTimeout { get => _requestTimeout; set => Set(ref _requestTimeout, value); }
    public string Interval { get => _interval; set => Set(ref _interval, value); }
    public string Error { get => _error; set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(Error);
    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    public bool TryBuild(out MetadataSourceSettings settings)
    {
        settings = _original;
        if (Mode != DemoMode && Mode != RealMode) { Error = "请选择演示或真实来源。"; return false; }
        if (!double.TryParse(ConnectTimeout, NumberStyles.Float, CultureInfo.InvariantCulture, out var connect) ||
            !double.TryParse(RequestTimeout, NumberStyles.Float, CultureInfo.InvariantCulture, out var request) ||
            !double.TryParse(Interval, NumberStyles.Float, CultureInfo.InvariantCulture, out var interval))
        { Error = "超时与请求间隔必须是有效数字，单位为秒。"; return false; }
        settings = _original with { NetworkEnabled = IsReal, Proxy = string.IsNullOrWhiteSpace(Proxy) ? null : Proxy.Trim(), ConnectTimeoutSeconds = connect, RequestTimeoutSeconds = request, MinIntervalSeconds = interval };
        try { settings.Validate(); Error = ""; return true; }
        catch (InvalidDataException ex) { Error = ex.Message; return false; }
    }
}

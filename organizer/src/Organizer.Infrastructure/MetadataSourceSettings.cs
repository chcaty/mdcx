using System.Net;
using Organizer.Core;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Organizer.Infrastructure;

/// <summary>Credential-free settings shared with the independent Python HTTP worker.</summary>
public sealed record MetadataSourceSettings
{
    public const string AllowedOrigin = "https://www.javbus.com";
    public string Source { get; init; } = "javbus";
    public string Origin { get; init; } = AllowedOrigin;
    public bool NetworkEnabled { get; init; }
    public string? Proxy { get; init; }
    public double ConnectTimeoutSeconds { get; init; } = 5;
    public double ReadTimeoutSeconds { get; init; } = 10;
    public double PoolTimeoutSeconds { get; init; } = 5;
    public double RequestTimeoutSeconds { get; init; } = 15;
    public int MaxBodyBytes { get; init; } = 1_048_576;
    public int GlobalLimit { get; init; } = 2;
    public int PerHostLimit { get; init; } = 1;
    public int Attempts { get; init; } = 2;
    public double MinIntervalSeconds { get; init; } = 1;
    public double MaxRetryDelaySeconds { get; init; } = 30;

    public void Validate()
    {
        if (Source != "javbus" || Origin != AllowedOrigin ||
            !Bounded(ConnectTimeoutSeconds, .1, 60) || !Bounded(ReadTimeoutSeconds, .1, 60) ||
            !Bounded(PoolTimeoutSeconds, .1, 60) || !Bounded(RequestTimeoutSeconds, .1, 60) ||
            MaxBodyBytes is < 1 or > 1_048_576 || GlobalLimit is < 1 or > 4 || PerHostLimit is < 1 or > 2 ||
            PerHostLimit > GlobalLimit || Attempts is < 1 or > 3 || !Bounded(MinIntervalSeconds, .1, 60) ||
            !Bounded(MaxRetryDelaySeconds, 0, 60) || !ValidProxy(Proxy))
            throw new InvalidDataException("来源配置无效。请检查超时、间隔和不含账号密码的代理地址。");
    }

    private static bool Bounded(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
    public static bool ValidProxy(string? proxy)
    {
        if (proxy is null) return true;
        if (proxy.Length > 512 || proxy.Any(c => c <= 32 || c >= 127)) return false;
        var match = Regex.Match(proxy, @"^https?://(?<host>\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9.-]+):(?<port>[0-9]{1,5})/?$", RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups["port"].Value, out var port) || port is < 1 or > 65535) return false;
        var host = match.Groups["host"].Value;
        if (IPAddress.TryParse(host.Trim('[', ']'), out _)) return true;
        return host.Length <= 253 && host.Split('.').All(label => Regex.IsMatch(label, @"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$", RegexOptions.CultureInvariant));
    }
}

/// <summary>App-owned, bounded local config. Loading never starts a worker or enables absent settings.</summary>
public sealed class MetadataSettingsStore(string path)
{
    private const int MaximumBytes = 16_384;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };
    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public MetadataSourceSettings Load()
    {
        try
        {
            if (PathSafety.HasLinkComponent(Path)) throw new IOException("Source settings must not contain symbolic links or reparse points.");
            if (!File.Exists(Path)) return new();
            using var stream = File.OpenRead(Path);
            var bytes = new byte[MaximumBytes + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = stream.Read(bytes, length, bytes.Length - length);
                if (read == 0) break;
                length += read;
            }
            if (length > MaximumBytes) throw new InvalidDataException();
            using var document = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!keys.Add(property.Name)) throw new InvalidDataException();
            var value = document.RootElement.Deserialize<MetadataSourceSettings>(JsonOptions) ?? throw new InvalidDataException();
            value.Validate();
            return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { throw new InvalidDataException("来源配置无法读取或无效。已保持演示模式，请重新保存配置。"); }
    }

    public void Save(MetadataSourceSettings value)
    {
        value.Validate();
        if (PathSafety.HasLinkComponent(Path)) throw new IOException("Source settings must not contain symbolic links or reparse points.");
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory, $".metadata-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(temporary, Path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

using System.Text.Json;
using Organizer.Core;

namespace Organizer.Desktop.Configuration;

/// <summary>Explicit portable data selection. Resolution never imports or removes installed data.</summary>
internal sealed record ApplicationDataLocation(string Mode, string ApplicationDirectory, string DirectoryPath, string IndexPath)
{
    internal const string MarkerFileName = "portable.json";
    internal const string MarkerContents = "{\"formatVersion\":1}";
    internal bool IsPortable => Mode == "portable";
    internal string SettingsPath => Path.Combine(DirectoryPath, "metadata-source.json");

    internal static ApplicationDataLocation Resolve(string baseDirectory, string? indexOverride = null, string? localApplicationData = null)
    {
        var application = Path.GetFullPath(baseDirectory);
        // This deliberate override is also used by diagnostic modes. Do not even
        // inspect default data or its marker when a separate index was requested.
        if (indexOverride is not null)
        {
            try
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(indexOverride);
                var index = Path.GetFullPath(indexOverride);
                var directory = Path.GetDirectoryName(index);
                if (directory is null || Directory.Exists(index)) throw new ArgumentException("Index must identify a file.");
                return new("explicit", application, directory, index);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
            { throw new ApplicationStartupException("startup_arguments_invalid", "诊断索引必须指定有效的数据库文件路径。", exception); }
        }
        var marker = Path.Combine(application, MarkerFileName);
        if (HasPortableMarker(marker))
        {
            ValidateMarker(marker);
            var directory = Path.Combine(application, "data");
            var location = new ApplicationDataLocation("portable", application, directory, Path.Combine(directory, "index.db"));
            location.ValidatePortablePaths();
            return location;
        }
        RejectMissingPortableMarker(application, marker);
        var local = localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        var installed = Path.GetFullPath(Path.Combine(local, "MDCxOrganizer"));
        return new("installed", application, installed, Path.Combine(installed, "index.db"));
    }

    private static void RejectMissingPortableMarker(string application, string marker)
    {
        var path = Path.Combine(application, "package-manifest.json");
        try
        {
            try { _ = File.GetAttributes(path); }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            if (PathSafety.HasLinkComponent(path)) throw new InvalidDataException("Package manifest cannot be a link.");
            using var stream = File.OpenRead(path);
            const int maximumBytes = 4 * 1024 * 1024;
            if (stream.Length > maximumBytes) throw new InvalidDataException("Package manifest is too large.");
            var bytes = new byte[maximumBytes + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var count = stream.Read(bytes, length, bytes.Length - length);
                if (count == 0) break;
                length += count;
            }
            if (length > maximumBytes) throw new InvalidDataException("Package manifest is too large.");
            var offset = length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            using var document = JsonDocument.Parse(bytes.AsMemory(offset, length - offset), new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count())
                throw new InvalidDataException("Invalid package manifest.");
            string? ReadMode(string name, string legacy)
            {
                if (!root.TryGetProperty(name, out var field)) return null;
                if (field.ValueKind != JsonValueKind.String || field.GetString() is not { } text || (text != legacy && text != "portable"))
                    throw new InvalidDataException("Invalid package mode.");
                return field.GetString();
            }
            var kind = ReadMode("packageKind", "installer");
            var mode = ReadMode("dataMode", "installed");
            if (kind is not null && mode is not null && (kind == "portable") != (mode == "portable"))
                throw new InvalidDataException("Package data mode contradicts package kind.");
            if (kind == "portable" || mode == "portable")
                throw InvalidMarker(marker, new FileNotFoundException("Portable marker is missing."));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            throw new ApplicationStartupException("package_manifest_invalid",
                $"程序包清单无法读取或无效：\n{path}\n\n请重新解压完整的程序文件，并保留原有 data 文件夹。", exception);
        }
    }

    private static bool HasPortableMarker(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw InvalidMarker(path, exception); }
    }

    private static void ValidateMarker(string path)
    {
        try
        {
            if (PathSafety.HasLinkComponent(path)) throw new InvalidDataException("Portable marker cannot be a link.");
            using var stream = File.OpenRead(path);
            var bytes = new byte[4097];
            var length = 0;
            while (length < bytes.Length)
            {
                var count = stream.Read(bytes, length, bytes.Length - length);
                if (count == 0) break;
                length += count;
            }
            if (length > 4096) throw new InvalidDataException("Portable marker is too large.");
            var offset = length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            using var document = JsonDocument.Parse(bytes.AsMemory(offset, length - offset), new JsonDocumentOptions { MaxDepth = 2 });
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 1 ||
                !value.TryGetProperty("formatVersion", out var version) || !version.TryGetInt32(out var number) || number != 1)
                throw new InvalidDataException("Unsupported portable marker.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { throw InvalidMarker(path, exception); }
    }

    private static ApplicationStartupException InvalidMarker(string path, Exception exception) => new("portable_marker_invalid",
        $"便携模式标记无法读取或版本不受支持：\n{path}\n\n请重新解压完整的便携版程序文件，并保留原有 data 文件夹。", exception);

    private IEnumerable<string> DataPaths => [DirectoryPath, IndexPath, SettingsPath, IndexPath + "-wal", IndexPath + "-shm", IndexPath + "-journal"];

    private void ValidatePortablePaths()
    {
        try
        {
            if (DataPaths.Any(PathSafety.HasLinkComponent)) throw new IOException("Portable data paths must not contain links or reparse points.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw DataUnavailable(exception); }
    }

    internal ApplicationStartupException DataUnavailable(Exception exception) => new("portable_data_unwritable",
        $"便携版无法安全读写数据目录：\n{DirectoryPath}\n\n请将整个程序文件夹解压到可写的位置（例如文档目录），保留其中的 data 文件夹后重新打开。请勿直接在压缩包内运行，也不要将 data 设置为链接。", exception);

    internal ApplicationStartupException IndexUnavailable(Exception exception) => new("portable_index_unavailable",
        $"便携版无法打开本地索引：\n{IndexPath}\n\n索引文件可能无法读取，或与当前程序版本不兼容。请先备份并保留 data 文件夹，再确认使用了匹配的程序版本。不要删除或覆盖现有索引。", exception);

    internal void EnsureWritable(Action<string>? directoryProbe = null)
    {
        if (!IsPortable) return;
        try
        {
            ValidatePortablePaths();
            var probe = directoryProbe ?? ProbeDirectory;
            probe(ApplicationDirectory);
            Directory.CreateDirectory(DirectoryPath);
            probe(DirectoryPath);
            foreach (var path in DataPaths.Skip(1))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if (attributes.HasFlag(FileAttributes.Directory)) throw new IOException("Expected a data file, not a directory.");
                // Open without truncating or changing data. This catches existing
                // read-only index/config files even when their parent is writable.
                using var existing = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw DataUnavailable(exception); }
    }

    private static void ProbeDirectory(string directory)
    {
        var probe = Path.Combine(directory, $".mdcx-write-check-{Guid.NewGuid():N}.tmp");
        var created = false;
        try
        {
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                stream.WriteByte(0);
                stream.Flush(flushToDisk: true);
            }
            File.Delete(probe);
            created = false;
        }
        finally { if (created) File.Delete(probe); }
    }
}

internal sealed class ApplicationStartupException(string code, string message, Exception? innerException = null) : Exception(message, innerException)
{
    internal string Code { get; } = code;
}

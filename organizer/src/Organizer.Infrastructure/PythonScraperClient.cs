using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Organizer.Core;

namespace Organizer.Infrastructure;

public sealed class ScraperException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>One isolated process per request. Both pipes are drained concurrently with bounded buffers.</summary>
public sealed class PythonScraperClient(string pythonExecutable, string workerScript)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(25);
    public async Task<IReadOnlyList<MetadataCandidate>> MatchAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (query.Length > 256) throw new ArgumentException("Query must be at most 256 characters.", nameof(query));
        var script = Path.GetFullPath(workerScript);
        if (!File.Exists(script)) throw new FileNotFoundException("Python scraper worker was not found.", script);
        var requestId = Guid.NewGuid().ToString("N");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        var token = timeout.Token;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(pythonExecutable)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                StandardInputEncoding = new UTF8Encoding(false)
            }
        };
        process.StartInfo.ArgumentList.Add("-u");
        process.StartInfo.ArgumentList.Add(script);
        process.StartInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        process.StartInfo.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        if (!process.Start()) throw new ScraperException("worker_start", "Unable to start Python worker.");
        using var diagnosticsCancellation = new CancellationTokenSource();
        var stderr = DrainDiagnosticsAsync(process.StandardError, diagnosticsCancellation.Token);
        using var cancelled = token.Register(() => TryKill(process));
        var candidates = new List<MetadataCandidate>();
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { version = 1, requestId, command = "match", query }).AsMemory(), token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
            var terminal = false;
            var eventCount = 0;
            while (await ReadBoundedLineAsync(process.StandardOutput, token).ConfigureAwait(false) is { } line)
            {
                if (++eventCount > 512) throw new ScraperException("protocol_limit", "Worker emitted too many events.");
                using var json = JsonDocument.Parse(line);
                var e = json.RootElement;
                if (e.GetProperty("version").GetInt32() != 1) throw new ScraperException("protocol_version", "Unsupported worker protocol version.");
                if (e.GetProperty("requestId").GetString() != requestId) continue; // stale/unrelated messages cannot update this request.
                var kind = e.GetProperty("type").GetString();
                if (kind == "candidate")
                {
                    if (candidates.Count >= 64) throw new ScraperException("protocol_limit", "Too many candidates.");
                    var candidate = e.GetProperty("candidate").Deserialize<MetadataCandidate>(JsonOptions) ?? throw new ScraperException("invalid_candidate", "Missing candidate.");
                    if (string.IsNullOrWhiteSpace(candidate.Id) || candidate.Id.Length > 128 ||
                        string.IsNullOrWhiteSpace(candidate.Title) || candidate.Title.Length > 1000 ||
                        string.IsNullOrWhiteSpace(candidate.Source) || candidate.Source.Length > 128 ||
                        !Uri.TryCreate(candidate.SourceUrl, UriKind.Absolute, out var sourceUrl) || sourceUrl.Scheme != "https" || !string.IsNullOrEmpty(sourceUrl.UserInfo) ||
                        candidate.Provenance is null || candidate.Provenance.Count == 0 || candidate.Provenance.Count > 128 ||
                        candidate.Provenance.Any(p => p.Key.Length > 256 || p.Value is null || p.Value.Length > 4096) ||
                        candidate.ReviewReasons is null || candidate.ReviewReasons.Count > 32 || candidate.ReviewReasons.Any(r => r is null || r.Length > 1000) ||
                        candidate.Year is < 1888 or > 2100 ||
                        !double.IsFinite(candidate.Confidence) || candidate.Confidence is < 0 or > 1)
                        throw new ScraperException("invalid_candidate", "Worker candidate failed validation.");
                    candidates.Add(candidate);
                }
                else if (kind == "completed")
                {
                    if (e.TryGetProperty("status", out var status) && status.GetString() == "sources_failed")
                        throw new ScraperException("sources_failed", "All configured metadata sources failed; review source diagnostics before retrying.");
                    terminal = true; break;
                }
                else if (kind == "cancelled") throw new OperationCanceledException(token);
                else if (kind == "error")
                {
                    // Source failures are diagnostic; the worker may still return a valid fallback.
                    if (e.TryGetProperty("terminal", out var isTerminal) && isTerminal.ValueKind == JsonValueKind.False) continue;
                    var payload = e.TryGetProperty("error", out var nested) ? nested : e;
                    var code = payload.TryGetProperty("code", out var c) ? c.GetString() : "worker_error";
                    var message = payload.TryGetProperty("message", out var m) ? m.GetString() : "Worker returned an error.";
                    throw new ScraperException(code ?? "worker_error", message ?? "Worker returned an error.");
                }
            }
            token.ThrowIfCancellationRequested();
            if (!terminal) throw new ScraperException("worker_exit", "Worker closed stdout before a terminal event.");
            return candidates;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new ScraperException("timeout", "Scraper request exceeded its time limit."); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new ScraperException("protocol_invalid", $"Malformed worker event: {e.Message}"); }
        finally
        {
            // Request-scoped lifetime ensures a cancelled/stale worker cannot outlive the request.
            process.StandardInput.Close();
            TryKill(process);
            diagnosticsCancellation.Cancel();
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { /* Bounded teardown: no inherited pipe can hang the UI. */ }
            try { await stderr.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or TimeoutException or IOException or ObjectDisposedException) { }
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }
    private static async Task DrainDiagnosticsAsync(StreamReader reader, CancellationToken token)
    {
        // Drain continuously; do not retain potentially sensitive provider diagnostics in the UI.
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) > 0) { }
    }
    private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, CancellationToken token)
    {
        var line = new StringBuilder();
        var character = new char[1];
        while (await reader.ReadAsync(character.AsMemory(), token).ConfigureAwait(false) != 0)
        {
            if (character[0] == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length >= 65536) throw new ScraperException("protocol_limit", "Worker line exceeded 64 KiB.");
            line.Append(character[0]);
        }
        return line.Length == 0 ? null : line.ToString();
    }
}

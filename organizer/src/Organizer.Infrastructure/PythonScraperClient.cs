using System.Text.Json;
using Organizer.Core;

namespace Organizer.Infrastructure;

public sealed class ScraperException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Fixture requests retain isolated processes. An explicitly configured client owns one lazy,
/// persistent worker so rate limits, Retry-After cooldowns and health survive consecutive files.
/// A failed configured session never restarts automatically; create a new client explicitly.
/// </summary>
public sealed class PythonScraperClient : IMetadataClient
{
    private readonly string _pythonExecutable, _workerScript;
    private readonly PersistentMetadataSession? _session;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly HashSet<Task> _requests = [];
    private bool _disposed;
    private Task? _disposeTask;

    public PythonScraperClient(string pythonExecutable, string workerScript, string? metadataConfigPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pythonExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerScript);
        if (metadataConfigPath is not null) ArgumentException.ThrowIfNullOrWhiteSpace(metadataConfigPath);
        _pythonExecutable = pythonExecutable;
        _workerScript = workerScript;
        MetadataConfigPath = metadataConfigPath is null ? null : Path.GetFullPath(metadataConfigPath);
        if (MetadataConfigPath is not null) _session = new(pythonExecutable, workerScript, MetadataConfigPath);
    }

    public string? MetadataConfigPath { get; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(25);
    public TimeSpan CancellationAcknowledgementTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public Task<IReadOnlyList<MetadataCandidate>> MatchAsync(string query, CancellationToken cancellationToken = default,
        IReadOnlyList<string>? sources = null)
    {
        // Snapshot caller-owned input before admitting it to the bounded asynchronous queue.
        var selectedSources = sources?.ToArray();
        MetadataWorkerProtocol.ValidateRequest(query, selectedSources, _session is not null);
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(5) ||
            CancellationAcknowledgementTimeout <= TimeSpan.Zero || CancellationAcknowledgementTimeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout), "Worker deadlines must be positive and bounded.");
        TaskCompletionSource<IReadOnlyList<MetadataCandidate>> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Reserve shutdown tracking before starting work, without entering the session
            // under this lock. Concurrent disposal must see even not-yet-started requests.
            _requests.Add(completion.Task);
        }
        _ = ExecuteRequestAsync(completion, query, cancellationToken, selectedSources);
        return completion.Task;
    }

    private async Task ExecuteRequestAsync(TaskCompletionSource<IReadOnlyList<MetadataCandidate>> completion,
        string query, CancellationToken cancellationToken, IReadOnlyList<string>? sources)
    {
        try
        {
            lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
            var candidates = _session is null
                ? await MatchFixtureAsync(query, cancellationToken, sources).ConfigureAwait(false)
                : await _session.MatchAsync(query, cancellationToken, sources, RequestTimeout, CancellationAcknowledgementTimeout).ConfigureAwait(false);
            completion.TrySetResult(candidates);
        }
        catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
        catch (Exception error) { completion.TrySetException(error); }
        finally { lock (_gate) _requests.Remove(completion.Task); }
    }

    private async Task<IReadOnlyList<MetadataCandidate>> MatchFixtureAsync(string query, CancellationToken cancellationToken,
        IReadOnlyList<string>? sources)
    {
        var requestId = Guid.NewGuid().ToString("N");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(RequestTimeout);
        var token = timeout.Token;
        token.ThrowIfCancellationRequested();
        using var process = MetadataWorkerProtocol.CreateProcess(_pythonExecutable, _workerScript);
        if (!process.Start()) throw new ScraperException("worker_start", "Unable to start Python worker.");
        using var diagnosticsCancellation = new CancellationTokenSource();
        var stderr = MetadataWorkerProtocol.DrainDiagnosticsAsync(process.StandardError, diagnosticsCancellation.Token);
        using var cancelled = token.Register(() => MetadataWorkerProtocol.TryKill(process));
        var candidates = new List<MetadataCandidate>();
        var sourceFailureCodes = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            await process.StandardInput.WriteLineAsync(MetadataWorkerProtocol.MatchCommand(requestId, query, sources).AsMemory(), token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
            var terminal = false;
            var eventCount = 0;
            while (await MetadataWorkerProtocol.ReadBoundedLineAsync(process.StandardOutput, token).ConfigureAwait(false) is { } line)
            {
                if (++eventCount > 512) throw new ScraperException("protocol_limit", "Worker emitted too many events.");
                using var json = JsonDocument.Parse(line);
                var e = json.RootElement;
                if (e.GetProperty("version").GetInt32() != 1) throw new ScraperException("protocol_version", "Unsupported worker protocol version.");
                if (e.GetProperty("requestId").GetString() != requestId) continue;
                var kind = e.GetProperty("type").GetString();
                if (kind == "candidate")
                {
                    if (candidates.Count >= 64) throw new ScraperException("protocol_limit", "Too many candidates.");
                    candidates.Add(MetadataWorkerProtocol.ReadCandidate(e.GetProperty("candidate"), query, sources, configured: false));
                }
                else if (kind == "completed")
                {
                    if (e.TryGetProperty("status", out var status) && status.GetString() == "sources_failed")
                        throw MetadataWorkerProtocol.SourceFailure(sourceFailureCodes, configured: false);
                    terminal = true; break;
                }
                else if (kind == "cancelled") throw new OperationCanceledException(token);
                else if (kind == "error")
                {
                    var code = MetadataWorkerProtocol.SafeErrorCode(e);
                    if (e.TryGetProperty("terminal", out var isTerminal) && isTerminal.ValueKind == JsonValueKind.False)
                    {
                        sourceFailureCodes.Add(code);
                        continue;
                    }
                    throw MetadataWorkerProtocol.SafeError(code);
                }
            }
            token.ThrowIfCancellationRequested();
            if (!terminal) throw new ScraperException("worker_exit", "Worker closed stdout before a terminal event.");
            return candidates;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        { throw new ScraperException("timeout", "Scraper request exceeded its time limit."); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or System.Text.DecoderFallbackException)
        { throw new ScraperException("protocol_invalid", "The worker emitted a malformed event."); }
        finally
        {
            process.StandardInput.Close();
            MetadataWorkerProtocol.TryKill(process);
            diagnosticsCancellation.Cancel();
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { }
            try { await stderr.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or TimeoutException or IOException or ObjectDisposedException) { }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            return new(_disposeTask ??= Task.Run(DisposeCoreAsync));
        }
    }

    private async Task DisposeCoreAsync()
    {
        _lifetime.Cancel();
        if (_session is not null) await _session.DisposeAsync().ConfigureAwait(false);
        Task[] requests;
        lock (_gate) requests = _requests.ToArray();
        try { await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false); }
        catch (Exception) { /* The caller owns request failures; shutdown is bounded. */ }
        // Do not dispose the token source while an inherited pipe could still be unwinding.
        if (requests.All(task => task.IsCompleted)) _lifetime.Dispose();
    }
}

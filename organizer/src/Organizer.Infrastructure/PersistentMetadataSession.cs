using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Organizer.Core;

namespace Organizer.Infrastructure;

/// <summary>
/// One configuration snapshot and one worker lifetime. Queries are serialized rather than
/// parallelized across processes, keeping the Python Scheduler's rate/cooldown state intact.
/// All pipe readers run independently of the UI, with no unbounded event or diagnostic queue.
/// </summary>
internal sealed class PersistentMetadataSession(string pythonExecutable, string workerScript, string metadataConfigPath) : IAsyncDisposable
{
    private const int MaximumPendingQueries = 8;
    private const int MaximumSessionQueries = 8192;
    private const int MaximumStaleEvents = 1024;
    private const long MaximumDiagnosticCharacters = 8 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Process? _process;
    private Task _stdout = Task.CompletedTask, _stderr = Task.CompletedTask, _exit = Task.CompletedTask;
    private Task? _disposeTask;
    private RequestState? _active;
    private ScraperException? _fault;
    private bool _disposed, _hasCorrelatedEvent;
    private int _pendingQueries, _sentQueries, _staleEvents;

    private sealed class RequestState(string query, IReadOnlyList<string>? sources)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Query { get; } = query;
        public IReadOnlyList<string>? Sources { get; } = sources;
        public List<MetadataCandidate> Candidates { get; } = [];
        public HashSet<string> SourceFailures { get; } = new(StringComparer.Ordinal);
        public TaskCompletionSource<IReadOnlyList<MetadataCandidate>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Terminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationRequested { get; set; }
        public int EventCount { get; set; }
        public int CandidateCount { get; set; }
        public int SourceErrorCount { get; set; }
    }

    public async Task<IReadOnlyList<MetadataCandidate>> MatchAsync(string query, CancellationToken caller,
        IReadOnlyList<string>? sources, TimeSpan timeout, TimeSpan acknowledgementTimeout)
    {
        lock (_gate) ThrowUnavailable();
        caller.ThrowIfCancellationRequested();
        if (Interlocked.Increment(ref _pendingQueries) > MaximumPendingQueries)
        {
            Interlocked.Decrement(ref _pendingQueries);
            throw new ScraperException("busy", "The metadata request queue is full. Wait for pending queries to finish.");
        }
        var acquired = false;
        RequestState? state = null;
        try
        {
            using var queued = CancellationTokenSource.CreateLinkedTokenSource(caller, _stop.Token);
            try { await _serial.WaitAsync(queued.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!caller.IsCancellationRequested)
            {
                lock (_gate) ThrowUnavailable();
                throw;
            }
            acquired = true;
            lock (_gate)
            {
                ThrowUnavailable();
                if (_sentQueries >= MaximumSessionQueries) throw MetadataWorkerProtocol.SafeError("session_full");
            }
            // Process creation is never performed on the caller's dispatcher thread.
            await Task.Run(EnsureStarted, queued.Token).ConfigureAwait(false);
            using var request = CancellationTokenSource.CreateLinkedTokenSource(caller, _stop.Token);
            request.CancelAfter(timeout);
            state = new(query, sources);
            Process process;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, nameof(PythonScraperClient));
                if (_fault is not null) throw _fault;
                _active = state;
                process = _process!;
                _sentQueries++;
            }
            var fullySent = false;
            try
            {
                await WriteCommandAsync(process, MetadataWorkerProtocol.MatchCommand(state.Id, query, sources), request.Token).ConfigureAwait(false);
                fullySent = true;
                var candidates = await state.Completion.Task.WaitAsync(request.Token).ConfigureAwait(false);
                caller.ThrowIfCancellationRequested();
                lock (_gate) ObjectDisposedException.ThrowIf(_disposed, nameof(PythonScraperClient));
                // A fully validated terminal result remains valid if the process exits just
                // afterward. The next query still reports the permanently faulted session.
                if (request.IsCancellationRequested && !_stop.IsCancellationRequested)
                    throw new OperationCanceledException(request.Token);
                return candidates;
            }
            catch (OperationCanceledException)
            {
                if (_stop.IsCancellationRequested)
                {
                    lock (_gate)
                    {
                        ObjectDisposedException.ThrowIf(_disposed, nameof(PythonScraperClient));
                        // WaitAsync cancellation and the already-published result can race.
                        // Never lose validated completion merely because EOF arrived next.
                        if (!caller.IsCancellationRequested && state.Completion.Task.IsCompletedSuccessfully)
                            return state.Completion.Task.Result;
                        if (_fault is not null) throw _fault;
                    }
                }
                if (fullySent) await CancelAndDrainAsync(state, acknowledgementTimeout).ConfigureAwait(false);
                else Fault(new("worker_io", "A worker command was interrupted. This session cannot safely be reused."));
                if (caller.IsCancellationRequested) throw new OperationCanceledException(caller);
                throw MetadataWorkerProtocol.SafeError("timeout");
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                lock (_gate) ObjectDisposedException.ThrowIf(_disposed, nameof(PythonScraperClient));
                // A startup error may have closed stdin before its already-buffered safe
                // frame reaches the stdout pump. Preserve that typed failure when possible.
                if (!fullySent)
                {
                    try { await _stdout.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false); }
                    catch (TimeoutException) { }
                }
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, nameof(PythonScraperClient));
                    if (_fault is not null) throw _fault;
                }
                var failure = new ScraperException("worker_io", "The worker communication channel failed.");
                Fault(failure);
                throw failure;
            }
        }
        finally
        {
            lock (_gate) if (ReferenceEquals(_active, state)) _active = null;
            // Observe faulted completions even when cancellation won the WaitAsync race.
            if (state is not null) _ = state.Completion.Task.Exception;
            if (acquired) _serial.Release();
            Interlocked.Decrement(ref _pendingQueries);
        }
    }

    private void EnsureStarted()
    {
        Process? process = null;
        try
        {
            lock (_gate)
            {
                ThrowUnavailable();
                if (_process is not null) return;
                process = MetadataWorkerProtocol.CreateProcess(pythonExecutable, workerScript, metadataConfigPath);
                if (!process.Start()) throw new ScraperException("worker_start", "Unable to start Python worker.");
                _process = process;
                // Do not execute pipe/cancellation continuations under the startup lock.
                _stdout = Task.Run(() => PumpOutputAsync(process));
                _stderr = Task.Run(() => PumpDiagnosticsAsync(process));
                _exit = Task.Run(() => ObserveExitAsync(process));
            }
        }
        catch (Exception e) when (e is ScraperException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process?.Dispose();
            var failure = e as ScraperException ?? new ScraperException("worker_start", "Unable to start the configured Python worker.");
            Fault(failure);
            throw failure;
        }
    }

    private static async Task WriteCommandAsync(Process process, string command, CancellationToken token)
    {
        await process.StandardInput.WriteLineAsync(command.AsMemory(), token).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
    }

    private async Task CancelAndDrainAsync(RequestState state, TimeSpan deadline)
    {
        Process process;
        lock (_gate)
        {
            state.CancellationRequested = true;
            state.Candidates.Clear();
            if (state.Terminal.Task.IsCompleted || _fault is not null || _disposed) return;
            process = _process!;
        }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        cancel.CancelAfter(deadline);
        try
        {
            var command = JsonSerializer.Serialize(new { version = 1, requestId = state.Id, command = "cancel" });
            await WriteCommandAsync(process, command, cancel.Token).ConfigureAwait(false);
            // A terminal event (including a completion that raced cancellation) proves the
            // prior request is drained. Late candidates are ignored before the next request.
            await state.Terminal.Task.WaitAsync(cancel.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException)
        {
            Fault(new("cancel_unacknowledged", "The worker did not acknowledge cancellation. Explicitly reload source settings before retrying."));
        }
    }

    private async Task PumpOutputAsync(Process process)
    {
        try
        {
            while (await MetadataWorkerProtocol.ReadBoundedLineAsync(process.StandardOutput, _stop.Token).ConfigureAwait(false) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                Dispatch(document.RootElement);
            }
            if (!_stop.IsCancellationRequested) Fault(new("worker_exit", "The configured worker closed its output unexpectedly."));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ScraperException error) { Fault(error); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or DecoderFallbackException)
        { Fault(new("protocol_invalid", "The configured worker emitted a malformed event.")); }
        catch (IOException) { Fault(new("worker_io", "The configured worker output channel failed.")); }
    }

    private void Dispatch(JsonElement e)
    {
        if (e.GetProperty("version").GetInt32() != 1)
            throw new ScraperException("protocol_version", "Unsupported worker protocol version.");
        var requestId = e.GetProperty("requestId").GetString();
        var kind = e.GetProperty("type").GetString();
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) return;
            if (kind == "startup_error" && requestId == "" && !_hasCorrelatedEvent)
            {
                var code = MetadataWorkerProtocol.SafeErrorCode(e);
                if (code is not ("invalid_config" or "live_unavailable" or "dependency_unavailable") ||
                    !e.TryGetProperty("terminal", out var startupTerminal) || startupTerminal.ValueKind != JsonValueKind.True)
                    throw new ScraperException("protocol_invalid", "Invalid worker startup failure event.");
                throw MetadataWorkerProtocol.SafeError(code);
            }
            var state = _active;
            if (state is null || state.Id != requestId || state.Terminal.Task.IsCompleted)
            {
                if (++_staleEvents > MaximumStaleEvents) throw new ScraperException("protocol_limit", "Worker emitted too many unrelated or post-terminal events.");
                return;
            }
            _hasCorrelatedEvent = true;
            if (++state.EventCount > 512) throw new ScraperException("protocol_limit", "Worker emitted too many events for one request.");
            switch (kind)
            {
                case "candidate":
                    if (++state.CandidateCount > 64) throw new ScraperException("protocol_limit", "Worker emitted too many candidates.");
                    if (state.CancellationRequested) return;
                    state.Candidates.Add(MetadataWorkerProtocol.ReadCandidate(e.GetProperty("candidate"), state.Query, state.Sources, configured: true));
                    break;
                case "health":
                    // Health remains in the long-lived scheduler. Do not retain raw payloads.
                    break;
                case "completed":
                    ValidateCompleted(e, state);
                    if (state.CancellationRequested) state.Completion.TrySetCanceled();
                    else if (e.TryGetProperty("status", out var status) && status.GetString() == "sources_failed")
                        state.Completion.TrySetException(MetadataWorkerProtocol.SourceFailure(state.SourceFailures, configured: true));
                    else state.Completion.TrySetResult(state.Candidates.ToArray());
                    state.Terminal.TrySetResult();
                    break;
                case "cancelled":
                    RequireTerminal(e);
                    state.Candidates.Clear();
                    state.Completion.TrySetCanceled();
                    state.Terminal.TrySetResult();
                    break;
                case "error":
                    if (!e.TryGetProperty("error", out var payload) || payload.ValueKind != JsonValueKind.Object ||
                        !payload.TryGetProperty("code", out var errorCode) || errorCode.ValueKind != JsonValueKind.String ||
                        !e.TryGetProperty("terminal", out var terminal) || terminal.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new ScraperException("protocol_invalid", "The worker emitted an invalid structured error event.");
                    var code = MetadataWorkerProtocol.SafeErrorCode(e);
                    if (terminal.ValueKind == JsonValueKind.False)
                    {
                        state.SourceErrorCount++;
                        state.SourceFailures.Add(code);
                    }
                    else
                    {
                        state.Completion.TrySetException(MetadataWorkerProtocol.SafeError(code));
                        state.Terminal.TrySetResult();
                    }
                    break;
                default:
                    throw new ScraperException("protocol_invalid", "The configured worker emitted an unknown event type.");
            }
        }
    }

    private static void RequireTerminal(JsonElement e)
    {
        if (!e.TryGetProperty("terminal", out var terminal) || terminal.ValueKind != JsonValueKind.True)
            throw new ScraperException("protocol_invalid", "The worker emitted an invalid terminal event.");
    }

    private static void ValidateCompleted(JsonElement e, RequestState state)
    {
        RequireTerminal(e);
        if (!e.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String ||
            !e.TryGetProperty("count", out var count) || count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var candidateCount) ||
            !e.TryGetProperty("sourceFailures", out var failures) || failures.ValueKind != JsonValueKind.Number || !failures.TryGetInt32(out var sourceFailures) ||
            candidateCount != state.CandidateCount || sourceFailures != state.SourceErrorCount)
            throw new ScraperException("protocol_invalid", "The worker completion totals do not agree with its observed events.");
        var consistent = status.GetString() switch
        {
            "matched" => candidateCount > 0,
            "no_match" => candidateCount == 0 && sourceFailures == 0,
            "sources_failed" => candidateCount == 0 && sourceFailures > 0,
            _ => false
        };
        if (!consistent) throw new ScraperException("protocol_invalid", "The worker completion status contradicts its observed events.");
    }

    private async Task PumpDiagnosticsAsync(Process process)
    {
        try { await MetadataWorkerProtocol.DrainDiagnosticsAsync(process.StandardError, _stop.Token, MaximumDiagnosticCharacters).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ScraperException error) { Fault(error); }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        { if (!_stop.IsCancellationRequested) Fault(new("worker_io", "The configured worker diagnostic channel failed.")); }
    }

    private async Task ObserveExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(_stop.Token).ConfigureAwait(false);
            // Drain buffered terminal/startup-error frames before recording the exit. A
            // child inheriting the pipes cannot keep an exited worker looking healthy.
            try { await _stdout.WaitAsync(TimeSpan.FromMilliseconds(250), _stop.Token).ConfigureAwait(false); }
            catch (TimeoutException) { }
            if (!_stop.IsCancellationRequested) Fault(new("worker_exit", "The configured worker exited. Source settings must be reloaded explicitly."));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        { if (!_stop.IsCancellationRequested) Fault(new("worker_exit", "The configured worker process became unavailable.")); }
    }

    private void ThrowUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(PythonScraperClient));
        if (_fault is not null)
            throw new ScraperException("session_faulted", $"The metadata session stopped ({_fault.Code}). Explicitly reload source settings before retrying.");
    }

    private void Fault(ScraperException error)
    {
        Process? process;
        lock (_gate)
        {
            if (_disposed || _fault is not null) return;
            _fault = error;
            _active?.Candidates.Clear();
            _active?.Completion.TrySetException(error);
            _active?.Terminal.TrySetResult();
            process = _process;
        }
        _stop.Cancel();
        if (process is not null) MetadataWorkerProtocol.TryKill(process);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true;
            _active?.Candidates.Clear();
            _active?.Completion.TrySetException(new ObjectDisposedException(nameof(PythonScraperClient)));
            _active?.Terminal.TrySetResult();
            // Cancellation callbacks and process teardown always run outside both the
            // client and session locks, including when disposal starts on the UI thread.
            return new(_disposeTask = Task.Run(DisposeCoreAsync));
        }
    }

    private async Task DisposeCoreAsync()
    {
        _stop.Cancel();
        Process? process;
        Task[] pumps;
        lock (_gate)
        {
            process = _process;
            pumps = [_stdout, _stderr, _exit];
        }
        if (process is null) return;
        MetadataWorkerProtocol.TryKill(process);
        try { process.StandardInput.Close(); }
        catch (Exception e) when (e is IOException or InvalidOperationException) { }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException) { }
        try { await Task.WhenAll(pumps).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException or IOException or ObjectDisposedException) { }
        process.Dispose();
    }
}

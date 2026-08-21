using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Agents.AI.ContactCenter.Media.Transcription;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;

namespace Agents.AI.ContactCenter.Media.Audio.Resilience;

/// <summary>
/// <see cref="ISpeechRecognizer"/> decorator that wraps an ordered list of inner
/// recognizer factories (typically one per Azure region) with a Polly v8 pipeline
/// (Retry + Circuit Breaker per endpoint) and walks the list on fallback.
/// </summary>
/// <remarks>
/// <para>
/// Failover semantics: the decorator restarts the recognition session on a fresh
/// endpoint when a transient error occurs <em>before any final
/// (<see cref="TranscriptSegment.IsFinal"/>) transcript segment has been emitted
/// to the caller</em>. Once the first final segment crosses the boundary,
/// subsequent errors are propagated to the caller's transcript stream unchanged.
/// </para>
/// <para>
/// Audio that arrived while the recognizer was between sessions is <em>not</em>
/// replayed onto the new endpoint; it is counted via the
/// <c>speech.resilience.audio_frames_dropped_total</c> meter so the operational
/// impact is observable.
/// </para>
/// <para>
/// Each instance owns one session. Disposing the decorator disposes the currently
/// active inner recognizer.
/// </para>
/// </remarks>
public sealed class ResilientSpeechRecognizer : ISpeechRecognizer
{
    private readonly IReadOnlyList<EndpointEntry> _endpoints;
    private readonly SpeechResilienceOptions _options;
    private readonly ILogger<ResilientSpeechRecognizer> _logger;

    private readonly Channel<TranscriptSegment> _outer = Channel.CreateUnbounded<TranscriptSegment>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly CancellationTokenSource _cts = new();
    private readonly object _stateLock = new();

    private Task? _lifecycleTask;
    private ISpeechRecognizer? _current;
    private string _currentEndpointName = string.Empty;
    private int _started;
    private int _completedRequested;
    private int _disposed;
    private volatile bool _hasReceivedFinal;

    public ResilientSpeechRecognizer(
        IReadOnlyList<(string Name, Func<ISpeechRecognizer> Factory)> endpoints,
        SpeechResilienceOptions options,
        ILogger<ResilientSpeechRecognizer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);

        if (endpoints.Count == 0)
        {
            throw new ArgumentException("At least one recognizer endpoint is required.", nameof(endpoints));
        }

        _options = options;
        _logger = logger ?? NullLogger<ResilientSpeechRecognizer>.Instance;
        _endpoints = endpoints
            .Select(e => new EndpointEntry(
                e.Name,
                e.Factory,
                SpeechResiliencePipelineBuilder.Build(
                    e.Name,
                    SpeechResilienceTelemetry.OperationRecognizeStart,
                    options,
                    includeAttemptTimeout: false)))
            .ToArray();
    }

    /// <inheritdoc />
    public async Task WriteAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        EnsureLifecycleStarted();

        // Snapshot the current inner under no lock - the lifecycle task swaps
        // _current atomically and we accept that a frame written during a
        // restart is reported as dropped.
        var inner = Volatile.Read(ref _current);
        if (inner is null)
        {
            SpeechResilienceTelemetry.RecordDroppedFrames(_currentEndpointName, 1);
            _logger.LogDebug(
                "Dropped audio frame ({Bytes} bytes): no active recognizer (restart in progress)",
                audioData.Length);
            return;
        }

        try
        {
            await inner.WriteAudioAsync(audioData, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!SpeechExceptionClassifier.IsCallerCancellation(ex, cancellationToken))
        {
            // The lifecycle task observes the transcript channel for failures and
            // drives restart; from the caller's WriteAudioAsync perspective the
            // frame was simply not durable.
            SpeechResilienceTelemetry.RecordDroppedFrames(_currentEndpointName, 1);
            _logger.LogDebug(
                ex,
                "Dropped audio frame ({Bytes} bytes): inner WriteAudioAsync failed on {EndpointName}",
                audioData.Length,
                _currentEndpointName);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TranscriptSegment> GetTranscriptsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        EnsureLifecycleStarted();

        await foreach (var segment in _outer.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return segment;
        }
    }

    /// <inheritdoc />
    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed != 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _completedRequested, 1) != 0)
        {
            return;
        }

        var inner = Volatile.Read(ref _current);
        if (inner is not null)
        {
            try
            {
                await inner.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!SpeechExceptionClassifier.IsCallerCancellation(ex, cancellationToken))
            {
                _logger.LogWarning(ex, "Inner CompleteAsync failed on {EndpointName}", _currentEndpointName);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error cancelling resilient recognizer lifecycle token");
        }

        _outer.Writer.TryComplete();

        if (_lifecycleTask is not null)
        {
            try
            {
                await _lifecycleTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (!SpeechExceptionClassifier.IsCallerCancellation(ex, CancellationToken.None))
            {
                _logger.LogDebug(ex, "Lifecycle task completed with error during disposal");
            }
        }

        var inner = Interlocked.Exchange(ref _current, null);
        if (inner is not null)
        {
            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing inner recognizer");
            }
        }

        _cts.Dispose();
    }

    private void EnsureLifecycleStarted()
    {
        if (Volatile.Read(ref _started) != 0)
        {
            return;
        }

        lock (_stateLock)
        {
            if (_started != 0)
            {
                return;
            }

            _started = 1;
            _lifecycleTask = Task.Run(RunLifecycleAsync, CancellationToken.None);
        }
    }

    private async Task RunLifecycleAsync()
    {
        var endpointCount = _options.EnableFallback ? _endpoints.Count : 1;
        Exception? lastException = null;

        try
        {
            for (var i = 0; i < endpointCount; i++)
            {
                if (_cts.IsCancellationRequested)
                {
                    break;
                }

                var entry = _endpoints[i];

                using var activity = SpeechResilienceTelemetry.ActivitySource.StartActivity(
                    "speech.recognize.session",
                    ActivityKind.Client);
                activity?.SetTag(SpeechResilienceTelemetry.TagOperation, SpeechResilienceTelemetry.OperationRecognizeStart);
                activity?.SetTag(SpeechResilienceTelemetry.TagEndpointName, entry.Name);
                activity?.SetTag(SpeechResilienceTelemetry.TagEndpointIndex, i);

                var startTimestamp = Stopwatch.GetTimestamp();
                Exception? attemptException = null;

                try
                {
                    await entry.Pipeline
                        .ExecuteAsync(
                            async ct => await RunOneSessionAsync(entry, ct).ConfigureAwait(false),
                            _cts.Token)
                        .ConfigureAwait(false);

                    // Clean session completion (inner stream ended naturally).
                    activity?.SetTag(SpeechResilienceTelemetry.TagOutcome, SpeechResilienceTelemetry.OutcomeSuccess);
                    SpeechResilienceTelemetry.RecordAttemptDuration(
                        SpeechResilienceTelemetry.OperationRecognizeStart,
                        entry.Name,
                        SpeechResilienceTelemetry.OutcomeSuccess,
                        GetElapsedMs(startTimestamp));

                    _outer.Writer.TryComplete();
                    return;
                }
                catch (Exception ex) when (SpeechExceptionClassifier.IsCallerCancellation(ex, _cts.Token))
                {
                    activity?.SetTag(SpeechResilienceTelemetry.TagOutcome, SpeechResilienceTelemetry.OutcomeCancelled);
                    SpeechResilienceTelemetry.RecordAttemptDuration(
                        SpeechResilienceTelemetry.OperationRecognizeStart,
                        entry.Name,
                        SpeechResilienceTelemetry.OutcomeCancelled,
                        GetElapsedMs(startTimestamp));
                    _outer.Writer.TryComplete();
                    return;
                }
                catch (Exception ex)
                {
                    attemptException = ex;
                    lastException = ex;

                    var transient = SpeechExceptionClassifier.IsTransient(ex);
                    var outcome = transient
                        ? SpeechResilienceTelemetry.OutcomeTransientError
                        : SpeechResilienceTelemetry.OutcomeTerminalError;

                    activity?.SetTag(SpeechResilienceTelemetry.TagOutcome, outcome);
                    activity?.SetTag(SpeechResilienceTelemetry.TagExceptionType, ex.GetType().FullName);
                    if (ex is SpeechSdkException sdk)
                    {
                        activity?.SetTag(SpeechResilienceTelemetry.TagErrorCode, sdk.ErrorCode.ToString());
                    }

                    SpeechResilienceTelemetry.RecordAttemptDuration(
                        SpeechResilienceTelemetry.OperationRecognizeStart,
                        entry.Name,
                        outcome,
                        GetElapsedMs(startTimestamp));

                    // Failover boundary: once a final transcript has crossed to
                    // the caller, we MUST NOT swallow further errors.
                    if (_hasReceivedFinal)
                    {
                        _logger.LogWarning(
                            ex,
                            "Recognizer failed after first final segment on {EndpointName}; surfacing to caller",
                            entry.Name);
                        _outer.Writer.TryComplete(ex);
                        return;
                    }

                    if (!transient)
                    {
                        _logger.LogError(
                            ex,
                            "Recognizer failed with terminal error on {EndpointName}; surfacing to caller",
                            entry.Name);
                        _outer.Writer.TryComplete(ex);
                        return;
                    }

                    if (i + 1 < endpointCount)
                    {
                        _logger.LogWarning(
                            ex,
                            "Recognizer transient failure on {EndpointName} before first final; falling back to {NextEndpoint}",
                            entry.Name,
                            _endpoints[i + 1].Name);

                        SpeechResilienceTelemetry.RecordFallback(
                            SpeechResilienceTelemetry.OperationRecognizeStart,
                            entry.Name,
                            _endpoints[i + 1].Name,
                            ex);
                    }
                    else
                    {
                        _logger.LogError(
                            ex,
                            "Recognizer transient failure on {EndpointName}; no more endpoints to try",
                            entry.Name);
                    }
                }
                finally
                {
                    await SwapOutCurrentAsync(attemptException).ConfigureAwait(false);
                }
            }

            // Exhausted: surface the last transient failure.
            _outer.Writer.TryComplete(
                lastException ?? new InvalidOperationException(
                    "ResilientSpeechRecognizer exhausted all endpoints."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in resilient recognizer lifecycle");
            _outer.Writer.TryComplete(ex);
        }
    }

    private async Task RunOneSessionAsync(EndpointEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var inner = entry.Factory();
        Volatile.Write(ref _current, inner);
        _currentEndpointName = entry.Name;

        try
        {
            await foreach (var segment in inner
                .GetTranscriptsAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                if (segment.IsFinal)
                {
                    _hasReceivedFinal = true;
                }

                await _outer.Writer.WriteAsync(segment, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // Detach so a stale inner is not used by WriteAudioAsync while the
            // pipeline decides whether to retry or fall back.
            if (ReferenceEquals(Volatile.Read(ref _current), inner))
            {
                Volatile.Write(ref _current, null);
            }

            await DisposeInnerSafelyAsync(inner).ConfigureAwait(false);
            throw;
        }
    }

    private async Task SwapOutCurrentAsync(Exception? attemptException)
    {
        var inner = Interlocked.Exchange(ref _current, null);
        if (inner is null)
        {
            return;
        }

        await DisposeInnerSafelyAsync(inner).ConfigureAwait(false);
        _ = attemptException;
    }

    private async Task DisposeInnerSafelyAsync(ISpeechRecognizer inner)
    {
        try
        {
            await inner.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error disposing inner recognizer for {EndpointName}", _currentEndpointName);
        }
    }

    private static double GetElapsedMs(long startTimestamp) =>
        Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

    private sealed record EndpointEntry(string Name, Func<ISpeechRecognizer> Factory, ResiliencePipeline Pipeline);
}

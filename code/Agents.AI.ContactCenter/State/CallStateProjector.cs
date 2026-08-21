using System.Collections.Frozen;
using System.Text;
using System.Threading.Channels;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.State;

/// <summary>
/// Per-call owner of the immutable state slices. Folds the <see cref="StrategyEvent"/> stream into the
/// <see cref="CallStateBag"/> on a single writer (the call's event pump), exposes lock-free typed reads,
/// hydrates from the snapshot store on (re)attach, and schedules debounced persistence.
/// </summary>
/// <remarks>
/// Registered <c>Scoped</c> by <c>AddCallState</c> so every service in a call's DI scope shares one
/// instance. Because the only writer is the single event-pump thread, no per-field locking is needed:
/// slices are immutable snapshots published via atomic reference swaps and read lock-free.
/// </remarks>
public sealed class CallStateProjector : IPromptStateRenderer, IAsyncDisposable
{
    private readonly string _callId;
    private readonly CallStateBag _bag = new();
    private readonly ICallStateProjection[] _projections;
    private readonly FrozenDictionary<Type, ICallStateProjection> _byType;
    private readonly ICallStateStore _store;
    private readonly ICallEventLog? _eventLog;
    private readonly CallStateOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CallStateProjector>? _logger;

    // Persistence is kept off the fold hot path: Fold enqueues onto this single-reader channel; the
    // background loop appends to the optional event log and writes debounced snapshots to the store.
    private readonly Channel<StrategyEvent> _persistChannel = Channel.CreateUnbounded<StrategyEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private Task? _persistLoop;

    // Single fold lock. The fold runs at emit time (the StateFoldingChannelWriter funnel),
    // which can be driven by several strategy threads
    private readonly Lock _foldLock = new();

    private long _version;
    private long _lastSequence;
    private int _sinceLastSnapshot;
    private volatile bool _dirty;

    public CallStateProjector(
        string callId,
        IEnumerable<ICallStateProjection> projections,
        ICallStateStore store,
        CallStateOptions options,
        ICallEventLog? eventLog = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        _callId = callId;
        _projections = [.. projections];
        _byType = _projections.ToFrozenDictionary(p => p.SnapshotType);
        _store = store;
        _options = options;
        _eventLog = eventLog;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<CallStateProjector>();
    }

    /// <summary>The call this projector serves.</summary>
    public string CallId => _callId;

    /// <summary>The underlying slice bag (lock-free reads).</summary>
    internal CallStateBag Bag => _bag;

    /// <summary>Lock-free typed read of a slice snapshot.</summary>
    /// <exception cref="InvalidOperationException">No provider is registered for <typeparamref name="TState"/>.</exception>
    public TState Get<TState>() where TState : class
    {
        if (_byType.TryGetValue(typeof(TState), out var projection))
        {
            return (TState)projection.ReadBoxed(_bag);
        }

        throw new InvalidOperationException(
            $"No call-state projection is registered for slice type '{typeof(TState).Name}'. " +
            $"Register one via AddCallStateProjection<T>().");
    }

    /// <summary>
    /// Load the persisted snapshot (if any) into the bag, replay any event-log tail persisted after the
    /// snapshot's <see cref="CallStateSnapshot.EventSequence"/> watermark, and start the background
    /// persister. Call once, before the first <see cref="Fold"/>, from the call's start path so a mid-call
    /// resume or pod failover picks up where the previous owner left off.
    /// </summary>
    public async Task HydrateAsync(CancellationToken cancellationToken = default)
    {
        long watermark = 0;

        try
        {
            var snapshot = await _store.LoadAsync(_callId, cancellationToken).ConfigureAwait(false);
            if (snapshot is not null)
            {
                foreach (var projection in _projections)
                {
                    if (snapshot.Slices.TryGetValue(projection.SliceId, out var json))
                    {
                        try
                        {
                            projection.Restore(_bag, json);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Restore failed for slice {SliceId} on call {CallId}", projection.SliceId, _callId);
                        }
                    }
                }

                _version = snapshot.Version;
                watermark = snapshot.EventSequence;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Hydrate failed for call {CallId}; starting from initial state", _callId);
        }

        // Replay the event-log tail recorded after the snapshot watermark so a resume/failover catches up
        // events that weren't folded into the last snapshot. Replayed events are folded straight into the
        // providers — never re-appended or re-emitted, and HydrateAsync runs before the event pump starts.
        // Best-effort: a replay failure leaves us on snapshot-only state rather than blocking the call.
        var maxSequence = watermark;
        if (_eventLog is not null)
        {
            try
            {
                await foreach (var envelope in _eventLog.ReadAsync(_callId, afterSequence: watermark, cancellationToken).ConfigureAwait(false))
                {
                    ApplyToProjections(envelope.Event);
                    if (envelope.Sequence > maxSequence)
                    {
                        maxSequence = envelope.Sequence;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Event-log replay failed for call {CallId}; continuing with snapshot-only state", _callId);
            }
        }

        Volatile.Write(ref _lastSequence, maxSequence);
        _persistLoop = Task.Run(RunPersistLoopAsync);
    }

    /// <summary>
    /// Fold one event into every slice, then hand it to the background persister. Thread-safe: the
    /// read-modify-write fold is serialized by a single lock so it can be driven directly from emit time
    /// (the <see cref="StateFoldingChannelWriter"/> funnel) on any strategy thread. <see cref="Get{TState}"/>
    /// reads stay lock-free.
    /// </summary>
    public void Fold(StrategyEvent strategyEvent)
    {
        ArgumentNullException.ThrowIfNull(strategyEvent);

        lock (_foldLock)
        {
            ApplyToProjections(strategyEvent);
        }

        // Non-blocking hand-off to the background persister. Buffered until HydrateAsync starts the loop.
        _persistChannel.Writer.TryWrite(strategyEvent);
    }

    /// <summary>
    /// Fold one event into every projection's slice. Shared by <see cref="Fold"/> (live event-pump path)
    /// and <see cref="HydrateAsync"/> (event-log replay), which folds without enqueueing to the persister.
    /// </summary>
    private void ApplyToProjections(StrategyEvent strategyEvent)
    {
        for (var i = 0; i < _projections.Length; i++)
        {
            try
            {
                _projections[i].Fold(_bag, strategyEvent);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Projection {SliceId} failed folding {EventType} on call {CallId}",
                    _projections[i].SliceId, strategyEvent.GetType().Name, _callId);
            }
        }
    }

    /// <summary>Force a snapshot flush (e.g. on call end). No-op until <see cref="HydrateAsync"/> has run.</summary>
    public Task FlushAsync(CancellationToken cancellationToken = default)
        => _persistLoop is null ? Task.CompletedTask : FlushCoreAsync(cancellationToken);

    /// <inheritdoc />
    public string RenderAsPrompt()
    {
        var sb = new StringBuilder();
        foreach (var projection in _projections)
        {
            var rendered = projection.Render(_bag);
            if (!string.IsNullOrWhiteSpace(rendered))
            {
                sb.AppendLine(rendered);
            }
        }

        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        _persistChannel.Writer.TryComplete();

        if (_persistLoop is not null)
        {
            try { await _persistLoop.ConfigureAwait(false); }
            catch { /* loop already logged */ }
        }

        // Final best-effort flush with a fresh token in case shutdown cancelled the loop mid-batch.
        try
        {
            await FlushCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Final call-state flush failed for call {CallId}", _callId);
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        _cts.Dispose();
        _flushGate.Dispose();
    }

    // ── Background persistence (kept off the fold hot path) ─────────────────────────

    /// <summary>
    /// Drains folded events on a single reader: appends each to the optional <see cref="ICallEventLog"/>,
    /// then writes a debounced snapshot to the <see cref="ICallStateStore"/> after
    /// <see cref="CallStateOptions.SnapshotEveryNEvents"/> events or when the batch drains.
    /// </summary>
    private async Task RunPersistLoopAsync()
    {
        var reader = _persistChannel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var strategyEvent))
                {
                    if (_eventLog is not null)
                    {
                        try
                        {
                            // Advance the replay watermark only for events that were durably appended, so a
                            // swallowed append never moves it past an event that is missing from the log.
                            var sequence = await _eventLog.AppendAsync(_callId, strategyEvent, _cts.Token).ConfigureAwait(false);
                            Volatile.Write(ref _lastSequence, sequence);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Event log append failed for call {CallId}", _callId);
                        }
                    }

                    _dirty = true;
                    if (++_sinceLastSnapshot >= _options.SnapshotEveryNEvents)
                    {
                        await FlushCoreAsync(_cts.Token).ConfigureAwait(false);
                    }
                }

                // Batch drained — flush any pending changes so a quiet call still persists promptly.
                if (_dirty)
                {
                    await FlushCoreAsync(_cts.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Call-state persister loop terminated for call {CallId}", _callId);
        }
    }

    /// <summary>Serialize all slices and persist a snapshot. Safe to call concurrently; serialized by a gate.</summary>
    private async Task FlushCoreAsync(CancellationToken cancellationToken = default)
    {
        if (!_dirty)
        {
            return;
        }

        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_dirty)
            {
                return;
            }

            var slices = new Dictionary<string, string>(_projections.Length, StringComparer.Ordinal);
            foreach (var projection in _projections)
            {
                slices[projection.SliceId] = projection.Serialize(_bag);
            }

            var snapshot = new CallStateSnapshot
            {
                CallId = _callId,
                Version = _version,
                EventSequence = Volatile.Read(ref _lastSequence),
                Slices = slices,
            };

            try
            {
                var saved = await _store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
                _version = saved.Version;
                _sinceLastSnapshot = 0;
                _dirty = false;
            }
            catch (CallStateConcurrencyException ex)
            {
                _logger?.LogWarning(ex, "Concurrency conflict persisting call {CallId}; reloading version", _callId);
                var current = await _store.LoadAsync(_callId, cancellationToken).ConfigureAwait(false);
                if (current is not null)
                {
                    // Reload only the concurrency token. This single writer stays authoritative for the
                    // replay watermark (_lastSequence), which is ahead of the reloaded snapshot's value.
                    // _dirty stays set (and _sinceLastSnapshot unreset), so this same slice state is
                    // re-serialized and re-saved on the next drain with the corrected version — the
                    // just-computed snapshot is retried, not lost.
                    _version = current.Version;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Snapshot persist failed for call {CallId}", _callId);
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }
}

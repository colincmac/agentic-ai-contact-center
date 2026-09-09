using System.Collections.Frozen;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Channels;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.State;

/// <summary>
/// Per-call owner of immutable state slices. Live folds and admission to persistence share one lock;
/// typed reads remain lock-free. A separate single-reader projection tracks the durable log prefix,
/// keeping snapshot serialization and store I/O off the emit path.
/// </summary>
public sealed class CallStateProjector : IPromptStateRenderer, IAsyncDisposable
{
    private readonly string _callId;
    private readonly CallStateBag _bag = new();
    private readonly CallStateBag _persistenceBag = new();
    private readonly ICallStateProjection[] _projections;
    private readonly FrozenDictionary<Type, ICallStateProjection> _byType;
    private readonly ICallStateStore _store;
    private readonly ICallEventLog? _eventLog;
    private readonly ILogger<CallStateProjector> _logger;
    private readonly Channel<PersistenceWork> _persistChannel;
    private readonly int _queueCapacity;
    private readonly int _snapshotEveryNEvents;
    private readonly TimeSpan _shutdownTimeout;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _foldLock = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _hydrateTask;
    private Task? _persistLoop;
    private Task? _disposeTask;
    private Exception? _failure;
    private bool _disposing;

    // Only hydration and then the single persistence reader access these fields and _persistenceBag.
    private long _version;
    private long _lastSequence;
    private int _sinceLastSnapshot;
    private bool _dirty;
    private readonly Dictionary<string, (object State, string Json)> _serializedSlices = new(StringComparer.Ordinal);

    private readonly record struct PersistenceWork(StrategyEvent? Event, TaskCompletionSource? Flush = null);

    public CallStateProjector(
        string callId,
        IEnumerable<ICallStateProjection> projections,
        ICallStateStore store,
        CallStateOptions options,
        ICallEventLog? eventLog = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        ArgumentNullException.ThrowIfNull(projections);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.PersistenceQueueCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.SnapshotEveryNEvents, 1);
        if (options.ShutdownTimeout <= TimeSpan.Zero || options.ShutdownTimeout.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ShutdownTimeout must be a positive, finite timer duration.");
        }

        _callId = callId;
        _projections = [.. projections];
        _byType = _projections.ToFrozenDictionary(p => p.SnapshotType);
        _store = store;
        _eventLog = eventLog;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<CallStateProjector>();
        _queueCapacity = options.PersistenceQueueCapacity;
        _snapshotEveryNEvents = options.SnapshotEveryNEvents;
        _shutdownTimeout = options.ShutdownTimeout;
        _persistChannel = Channel.CreateBounded<PersistenceWork>(new BoundedChannelOptions(_queueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>The call this projector serves.</summary>
    public string CallId => _callId;

    /// <summary>The underlying slice bag (lock-free reads).</summary>
    internal CallStateBag Bag => _bag;

    /// <summary>
    /// Completes on shutdown or faults when state admission, hydration, or persistence fails.
    /// Hosts can observe this task even when no further events or flushes occur.
    /// </summary>
    public Task PersistenceCompletion => _completion.Task;

    /// <summary>Lock-free typed read of a slice snapshot, not a durability acknowledgement.</summary>
    public TState Get<TState>() where TState : class
    {
        if (_byType.TryGetValue(typeof(TState), out var projection))
        {
            return (TState)projection.ReadBoxed(_bag);
        }

        throw new InvalidOperationException(
            $"No call-state projection is registered for slice type '{typeof(TState).Name}'. " +
            "Register one via AddCallStateProjection<T>().");
    }

    /// <summary>
    /// Restore the snapshot and ordered log tail, then start persistence. Call once before normal
    /// event emission. Events folded before hydration are reapplied over the restored state.
    /// Restore/replay failures propagate; partially restored state must not authorize a resumed call.
    /// </summary>
    public Task HydrateAsync(CancellationToken cancellationToken = default)
    {
        lock (_foldLock)
        {
            ThrowIfUnavailable();
            if (_hydrateTask is not null)
            {
                throw new InvalidOperationException("Call state hydration has already started.");
            }

            return _hydrateTask = HydrateCoreAsync(cancellationToken);
        }
    }

    private async Task HydrateCoreAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        try
        {
            var snapshot = await _store.LoadAsync(_callId, linked.Token).ConfigureAwait(false);
            if (snapshot is not null)
            {
                foreach (var projection in _projections)
                {
                    if (snapshot.Slices.TryGetValue(projection.SliceId, out var json))
                    {
                        projection.Restore(_persistenceBag, json);
                    }
                }

                _version = snapshot.Version;
                _lastSequence = snapshot.EventSequence;
            }

            if (_eventLog is not null)
            {
                await foreach (var envelope in _eventLog.ReadAsync(_callId, _lastSequence, linked.Token).ConfigureAwait(false))
                {
                    if (envelope.Sequence <= _lastSequence)
                    {
                        throw new InvalidOperationException("Call event log replay must be strictly increasing.");
                    }

                    ApplyToProjections(_persistenceBag, envelope.Event);
                    _lastSequence = envelope.Sequence;
                    _dirty = true;
                }
            }
            else
            {
                _lastSequence = 0;
            }

            lock (_foldLock)
            {
                linked.Token.ThrowIfCancellationRequested();
                ThrowIfFailed();
                foreach (var projection in _projections)
                {
                    var initial = projection.ReadBoxed(_persistenceBag);
                    _persistenceBag.Set(projection.SliceId, initial);
                    _bag.Set(projection.SliceId, initial);
                }

                // No reader runs yet. Rotate the bounded pre-hydration queue without changing order.
                var buffered = _persistChannel.Reader.Count;
                for (var i = 0; i < buffered; i++)
                {
                    if (!_persistChannel.Reader.TryRead(out var work))
                    {
                        throw new InvalidOperationException("Buffered call state was lost during hydration.");
                    }

                    ApplyToProjections(_bag, work.Event!);
                    if (!_persistChannel.Writer.TryWrite(work))
                    {
                        throw new InvalidOperationException("Buffered call state could not be scheduled.");
                    }
                }

                _persistLoop = Task.Run(RunPersistLoopAsync);
            }
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && _cts.IsCancellationRequested)
            {
                ex = new TimeoutException($"Call-state persistence did not drain within {_shutdownTimeout}.", ex);
            }
            Fail(ex);
            ExceptionDispatchInfo.Throw(ex);
        }
    }

    /// <summary>
    /// Publish immediately and admit the same event to the ordered, bounded persistence queue.
    /// No serialization or I/O occurs here. Overflow rejects the event before mutation and faults
    /// this projector; accepted events are only eventually durable until a flush succeeds.
    /// </summary>
    public void Fold(StrategyEvent strategyEvent)
    {
        ArgumentNullException.ThrowIfNull(strategyEvent);
        lock (_foldLock)
        {
            ThrowIfUnavailable();
            if (_hydrateTask is not null && _persistLoop is null)
            {
                throw new InvalidOperationException("Call state hydration must finish before folding events.");
            }

            try
            {
                // All writers and completion use this lock; the reader can only free capacity.
                if (_persistChannel.Reader.Count >= _queueCapacity)
                {
                    throw new InvalidOperationException($"Call-state persistence queue is full for call '{_callId}'.");
                }

                ApplyToProjections(_bag, strategyEvent);
                if (!_persistChannel.Writer.TryWrite(new PersistenceWork(strategyEvent)))
                {
                    throw new InvalidOperationException("Call-state persistence is no longer accepting events.");
                }
            }
            catch (Exception ex)
            {
                Fail(ex);
                throw;
            }
        }
    }

    private bool ApplyToProjections(CallStateBag bag, StrategyEvent strategyEvent)
    {
        var changed = false;
        foreach (var projection in _projections)
        {
            var previous = bag.GetRaw(projection.SliceId);
            projection.Fold(bag, strategyEvent);
            changed |= !ReferenceEquals(previous, bag.GetRaw(projection.SliceId));
        }
        return changed;
    }

    /// <summary>
    /// Persist all events admitted before this ordered flush barrier. No-op before hydration.
    /// Waits asynchronously for queue capacity; caller cancellation never blocks the persistence reader.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_foldLock)
            {
                ThrowIfUnavailable();
                if (_hydrateTask is null)
                {
                    return;
                }

                if (_persistLoop is null)
                {
                    throw new InvalidOperationException("Call state hydration must finish before flushing.");
                }

                if (_persistChannel.Writer.TryWrite(new PersistenceWork(null, completion)))
                {
                    break;
                }
            }

            if (!await _persistChannel.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false))
            {
                lock (_foldLock)
                {
                    ThrowIfUnavailable();
                }
                throw new InvalidOperationException("Call-state persistence has stopped.");
            }
        }

        var result = await Task.WhenAny(completion.Task, _completion.Task).WaitAsync(cancellationToken).ConfigureAwait(false);
        await result.ConfigureAwait(false);
    }

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

    public ValueTask DisposeAsync()
    {
        lock (_foldLock)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposing = true;
            // Preserve immediate pre-hydration reads without silently discarding their queued events.
            if (_hydrateTask is null && _failure is null && _persistChannel.Reader.Count > 0)
            {
                _hydrateTask = HydrateCoreAsync(CancellationToken.None);
            }
            return new ValueTask(_disposeTask = DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        _cts.CancelAfter(_shutdownTimeout);
        try
        {
            if (_hydrateTask is not null)
            {
                await _hydrateTask.WaitAsync(_cts.Token).ConfigureAwait(false);
            }

            lock (_foldLock)
            {
                _persistChannel.Writer.TryComplete();
            }

            if (_persistLoop is not null)
            {
                await _persistLoop.WaitAsync(_cts.Token).ConfigureAwait(false);
            }

            lock (_foldLock)
            {
                ThrowIfFailed();
            }
            _completion.TrySetResult();
        }
        catch (OperationCanceledException ex) when (_cts.IsCancellationRequested)
        {
            var timeout = new TimeoutException($"Call-state persistence did not drain within {_shutdownTimeout}.", ex);
            Fail(timeout);
            throw timeout;
        }
        finally
        {
            lock (_foldLock)
            {
                _persistChannel.Writer.TryComplete(_failure);
            }

            var pending = _persistLoop ?? _hydrateTask;
            if (pending is null || pending.IsCompleted)
            {
                _cts.Dispose();
            }
            else
            {
                // An uncooperative store must not make disposal hang or race token-source disposal.
                _ = pending.ContinueWith(task =>
                {
                    _ = task.Exception;
                    _cts.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private async Task RunPersistLoopAsync()
    {
        TaskCompletionSource? activeFlush = null;
        try
        {
            var reader = _persistChannel.Reader;
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var work))
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    activeFlush = work.Flush;
                    if (work.Event is { } strategyEvent)
                    {
                        var sequence = _lastSequence;
                        if (_eventLog is not null)
                        {
                            sequence = await _eventLog.AppendAsync(_callId, strategyEvent, _cts.Token).ConfigureAwait(false);
                            if (sequence <= _lastSequence)
                            {
                                throw new InvalidOperationException("Call event log append must advance its sequence.");
                            }
                        }

                        _cts.Token.ThrowIfCancellationRequested();
                        var changed = ApplyToProjections(_persistenceBag, strategyEvent);
                        _lastSequence = sequence;
                        _dirty |= changed || _eventLog is not null;
                        if (_dirty && ++_sinceLastSnapshot >= _snapshotEveryNEvents)
                        {
                            await PersistSnapshotAsync(_cts.Token).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        await PersistSnapshotAsync(_cts.Token).ConfigureAwait(false);
                        activeFlush!.TrySetResult();
                        activeFlush = null;
                    }
                }

                await PersistSnapshotAsync(_cts.Token).ConfigureAwait(false);
            }

            await PersistSnapshotAsync(_cts.Token).ConfigureAwait(false);
            _completion.TrySetResult();
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && _cts.IsCancellationRequested)
            {
                ex = new TimeoutException($"Call-state persistence did not drain within {_shutdownTimeout}.", ex);
            }
            Fail(ex);
            activeFlush?.TrySetException(ex);
            while (_persistChannel.Reader.TryRead(out var work))
            {
                work.Flush?.TrySetException(ex);
            }
            ExceptionDispatchInfo.Throw(ex);
        }
    }

    private async Task PersistSnapshotAsync(CancellationToken cancellationToken)
    {
        if (!_dirty)
        {
            return;
        }

        var slices = new Dictionary<string, string>(_projections.Length, StringComparer.Ordinal);
        foreach (var projection in _projections)
        {
            var state = projection.ReadBoxed(_persistenceBag);
            if (!_serializedSlices.TryGetValue(projection.SliceId, out var cached) || !ReferenceEquals(cached.State, state))
            {
                cached = (state, projection.Serialize(_persistenceBag));
                _serializedSlices[projection.SliceId] = cached;
            }
            slices[projection.SliceId] = cached.Json;
        }

        var saved = await _store.SaveAsync(new CallStateSnapshot
        {
            CallId = _callId,
            Version = _version,
            EventSequence = _lastSequence,
            Slices = slices,
        }, cancellationToken).ConfigureAwait(false);
        _version = saved.Version;
        _sinceLastSnapshot = 0;
        _dirty = false;
    }

    private void ThrowIfUnavailable()
    {
        ThrowIfFailed();
        ObjectDisposedException.ThrowIf(_disposing, this);
    }

    private void ThrowIfFailed()
    {
        if (_failure is not null)
        {
            ExceptionDispatchInfo.Throw(_failure);
        }
    }

    private void Fail(Exception error)
    {
        lock (_foldLock)
        {
            if (_failure is not null)
            {
                return;
            }

            _failure = error;
            _persistChannel.Writer.TryComplete(error);
            _completion.TrySetException(error);
            _logger.LogError(error, "Call-state persistence failed for call {CallId}", _callId);
        }
    }
}

using System.Collections.Concurrent;
using System.Threading.Channels;
using Agents.AI.ContactCenter.Telemetry;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.Calling.Core;

/// <summary>
/// In-process implementation of <see cref="ICallQualityReporter"/>. Holds the live
/// snapshot per call, broadcasts each update to all subscribers, and dual-emits
/// alerts/updates to <see cref="CallingTelemetry"/> for OTel-driven alerting.
/// </summary>
public sealed class InMemoryCallQualityReporter : ICallQualityReporter
{
    private readonly ConcurrentDictionary<string, RegistrationState> _registrations = new();
    private readonly List<Subscriber> _subscribers = [];
    private readonly Lock _subscribersLock = new();
    private readonly CallingTelemetry _telemetry;
    private readonly ILogger<InMemoryCallQualityReporter> _logger;

    public InMemoryCallQualityReporter(
        ILoggerFactory loggerFactory,
        CallingTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(telemetry);
        _telemetry = telemetry;
        _logger = loggerFactory.CreateLogger<InMemoryCallQualityReporter>();
    }

    /// <summary>
    /// Seed an initial snapshot when a call session starts. Required so updates
    /// have something to mutate.
    /// </summary>
    public ICallQualityRegistration Register(CallQualitySnapshot initial)
    {
        ArgumentNullException.ThrowIfNull(initial);

        var state = new RegistrationState(initial);
        while (true)
        {
            if (!_registrations.TryGetValue(initial.CallId, out var existing))
            {
                if (_registrations.TryAdd(initial.CallId, state))
                {
                    break;
                }
                continue;
            }

            lock (existing.Gate)
            {
                if (_registrations.TryUpdate(initial.CallId, state, existing))
                {
                    break;
                }
            }
        }

        Broadcast(initial);
        return new Registration(this, state);
    }

    private void Unregister(RegistrationState state)
    {
        lock (state.Gate)
        {
            ((ICollection<KeyValuePair<string, RegistrationState>>)_registrations)
                .Remove(new KeyValuePair<string, RegistrationState>(state.Snapshot.CallId, state));
        }
    }

    private void Update(RegistrationState state, Func<CallQualitySnapshot, CallQualitySnapshot> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        CallQualitySnapshot? next = null;
        lock (state.Gate)
        {
            if (!IsCurrent(state))
            {
                _logger.LogDebug("Update for stale call-quality registration {CallId}; ignoring", state.Snapshot.CallId);
                return;
            }

            var candidate = mutate(state.Snapshot);
            if (!string.Equals(candidate.CallId, state.Snapshot.CallId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A call-quality mutation cannot change the call id.");
            }
            next = candidate with
            {
                Alerts = [.. state.Alerts],
                UpdatedAt = DateTimeOffset.UtcNow
            };
            state.Snapshot = next;
        }

        _telemetry.SnapshotUpdated(next.CallId);
        Broadcast(next);
    }

    private void RaiseAlert(RegistrationState state, QualityAlert alert)
    {
        CallQualitySnapshot? next = null;
        lock (state.Gate)
        {
            if (!IsCurrent(state))
            {
                return;
            }

            state.Alerts.Add(alert);
            next = state.Snapshot with
            {
                Alerts = [.. state.Alerts],
                UpdatedAt = DateTimeOffset.UtcNow
            };
            state.Snapshot = next;
        }

        _telemetry.AlertRaised(next.CallId, alert);
        Broadcast(next);
    }

    private void ResolveAlert(RegistrationState state, string alertId)
    {
        QualityAlert? removed = null;
        CallQualitySnapshot? next = null;
        lock (state.Gate)
        {
            if (!IsCurrent(state))
            {
                return;
            }

            var idx = state.Alerts.FindIndex(a => a.AlertId == alertId);
            if (idx >= 0)
            {
                removed = state.Alerts[idx];
                state.Alerts.RemoveAt(idx);
                next = state.Snapshot with
                {
                    Alerts = [.. state.Alerts],
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                state.Snapshot = next;
            }
        }

        if (removed is not null)
        {
            _telemetry.AlertResolved(state.Snapshot.CallId, removed);
            Broadcast(next!);
        }
    }

    public CallQualitySnapshot? TryGetSnapshot(string callId)
    {
        if (!_registrations.TryGetValue(callId, out var state))
        {
            return null;
        }

        lock (state.Gate)
        {
            return IsCurrent(state) ? state.Snapshot : null;
        }
    }

    public IReadOnlyCollection<CallQualitySnapshot> GetActiveSnapshots()
    {
        var snapshots = new List<CallQualitySnapshot>(_registrations.Count);
        foreach (var state in _registrations.Values)
        {
            lock (state.Gate)
            {
                if (IsCurrent(state))
                {
                    snapshots.Add(state.Snapshot);
                }
            }
        }
        return snapshots;
    }

    public ICallQualitySubscription Subscribe(string? callIdFilter = null)
    {
        var channel = Channel.CreateBounded<CallQualitySnapshot>(
            new BoundedChannelOptions(32)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });

        var subscriber = new Subscriber(this, channel, callIdFilter);
        lock (_subscribersLock)
        {
            _subscribers.Add(subscriber);
        }

        // Replay the current snapshot for the filter so new dashboards see "now" immediately.
        foreach (var snap in GetActiveSnapshots())
        {
            if (callIdFilter is null || snap.CallId == callIdFilter)
            {
                subscriber.Writer.TryWrite(snap);
            }
        }

        return subscriber;
    }

    private bool IsCurrent(RegistrationState state)
        => _registrations.TryGetValue(state.Snapshot.CallId, out var current)
            && ReferenceEquals(current, state);

    private void Broadcast(CallQualitySnapshot snapshot)
    {
        lock (_subscribersLock)
        {
            for (var i = _subscribers.Count - 1; i >= 0; i--)
            {
                var s = _subscribers[i];
                if (s.Filter is not null && s.Filter != snapshot.CallId)
                {
                    continue;
                }

                if (!s.Writer.TryWrite(snapshot))
                {
                    _subscribers.RemoveAt(i);
                }
            }
        }
    }

    private void Unsubscribe(Subscriber subscriber)
    {
        lock (_subscribersLock)
        {
            _subscribers.Remove(subscriber);
        }
        subscriber.Writer.TryComplete();
    }

    private sealed class Subscriber(
        InMemoryCallQualityReporter owner,
        Channel<CallQualitySnapshot> channel,
        string? filter) : ICallQualitySubscription
    {
        private int _disposed;

        public string? Filter { get; } = filter;

        public ChannelWriter<CallQualitySnapshot> Writer => channel.Writer;

        public ChannelReader<CallQualitySnapshot> Reader => channel.Reader;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Unsubscribe(this);
            }
        }
    }

    private sealed class RegistrationState(CallQualitySnapshot snapshot)
    {
        public Lock Gate { get; } = new();

        public CallQualitySnapshot Snapshot { get; set; } = snapshot;

        public List<QualityAlert> Alerts { get; } = [.. snapshot.Alerts];
    }

    private sealed class Registration(
        InMemoryCallQualityReporter owner,
        RegistrationState state) : ICallQualityRegistration
    {
        private int _disposed;

        public string CallId => state.Snapshot.CallId;

        public void Update(Func<CallQualitySnapshot, CallQualitySnapshot> mutate)
            => owner.Update(state, mutate);

        public void RaiseAlert(QualityAlert alert)
            => owner.RaiseAlert(state, alert);

        public void ResolveAlert(string alertId)
            => owner.ResolveAlert(state, alertId);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Unregister(state);
            }
        }
    }
}

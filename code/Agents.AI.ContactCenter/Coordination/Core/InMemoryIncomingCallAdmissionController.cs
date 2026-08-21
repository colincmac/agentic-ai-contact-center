using System.Collections.Concurrent;
using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.Coordination.Core;

public sealed class InMemoryIncomingCallAdmissionController : IIncomingCallAdmissionController
{
    private readonly ConcurrentDictionary<string, IncomingCallAdmission> _admissions = new(StringComparer.Ordinal);
    private readonly IClusterIdentity _identity;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _leaseDuration;

    public InMemoryIncomingCallAdmissionController(
        IClusterIdentity identity,
        IOptions<HyperscaleOptions> options,
        TimeProvider timeProvider)
    {
        _identity = identity;
        _timeProvider = timeProvider;
        _leaseDuration = options.Value.IncomingCallAdmission.LeaseDuration;
    }

    public Task<IncomingCallAdmissionResult> TryAcquireAsync(
        string serverCallId,
        string eventGridEventId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifiers(serverCallId, eventGridEventId);
        cancellationToken.ThrowIfCancellationRequested();

        while (true)
        {
            var now = _timeProvider.GetUtcNow();
            var candidate = CreateLocalAdmission(serverCallId, eventGridEventId, now);
            if (_admissions.TryAdd(serverCallId, candidate))
            {
                return Task.FromResult(new IncomingCallAdmissionResult(IncomingCallAdmissionOutcome.Acquired, candidate));
            }

            if (!_admissions.TryGetValue(serverCallId, out var existing))
            {
                continue;
            }

            var outcome = Classify(existing, now);
            if (outcome is not IncomingCallAdmissionOutcome.Acquired)
            {
                return Task.FromResult(new IncomingCallAdmissionResult(outcome, existing));
            }

            if (_admissions.TryUpdate(serverCallId, candidate, existing))
            {
                return Task.FromResult(new IncomingCallAdmissionResult(IncomingCallAdmissionOutcome.Acquired, candidate));
            }
        }
    }

    public Task<bool> MarkAnsweringAsync(string serverCallId, CancellationToken cancellationToken = default)
        => TransitionAsync(
            serverCallId,
            IncomingCallAdmissionState.Claimed,
            current => current with
            {
                State = IncomingCallAdmissionState.Answering,
                LeaseUntil = _timeProvider.GetUtcNow() + _leaseDuration
            },
            cancellationToken);

    public Task<bool> MarkAnsweredAsync(
        string serverCallId,
        string callConnectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callConnectionId);
        return TransitionAsync(
            serverCallId,
            IncomingCallAdmissionState.Answering,
            current => current with
            {
                State = IncomingCallAdmissionState.Answered,
                CallConnectionId = callConnectionId
            },
            cancellationToken);
    }

    public Task<bool> MarkAnswerFailedAsync(
        string serverCallId,
        bool retryable,
        CancellationToken cancellationToken = default)
        => FailAsync(serverCallId, retryable, cancellationToken);

    public Task<IncomingCallAdmission?> GetAsync(
        string serverCallId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverCallId);
        cancellationToken.ThrowIfCancellationRequested();
        _admissions.TryGetValue(serverCallId, out var admission);
        return Task.FromResult(admission);
    }

    private async Task<bool> TransitionAsync(
        string serverCallId,
        IncomingCallAdmissionState expectedState,
        Func<IncomingCallAdmission, IncomingCallAdmission> transition,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverCallId);
        cancellationToken.ThrowIfCancellationRequested();

        while (true)
        {
            if (!_admissions.TryGetValue(serverCallId, out var current)
                || current.State != expectedState
                || !string.Equals(current.InstanceId, _identity.InstanceId, StringComparison.Ordinal))
            {
                return false;
            }

            if (_admissions.TryUpdate(serverCallId, transition(current), current))
            {
                return true;
            }

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private async Task<bool> FailAsync(
        string serverCallId,
        bool retryable,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverCallId);
        cancellationToken.ThrowIfCancellationRequested();

        while (true)
        {
            if (!_admissions.TryGetValue(serverCallId, out var current)
                || current.State is not (IncomingCallAdmissionState.Claimed or IncomingCallAdmissionState.Answering)
                || !string.Equals(current.InstanceId, _identity.InstanceId, StringComparison.Ordinal))
            {
                return false;
            }

            var failed = current with
            {
                State = retryable
                    ? IncomingCallAdmissionState.FailedRetryable
                    : IncomingCallAdmissionState.RecoveryRequired,
                LeaseUntil = _timeProvider.GetUtcNow()
            };
            if (_admissions.TryUpdate(serverCallId, failed, current))
            {
                return true;
            }

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private IncomingCallAdmission CreateLocalAdmission(
        string serverCallId,
        string eventGridEventId,
        DateTimeOffset now)
        => new(
            serverCallId,
            eventGridEventId,
            _identity.ClusterId,
            _identity.PodId,
            _identity.InstanceId,
            IncomingCallAdmissionState.Claimed,
            now + _leaseDuration);

    private static IncomingCallAdmissionOutcome Classify(IncomingCallAdmission admission, DateTimeOffset now)
        => admission.State switch
        {
            IncomingCallAdmissionState.Answered => IncomingCallAdmissionOutcome.AlreadyAnswered,
            IncomingCallAdmissionState.RecoveryRequired => IncomingCallAdmissionOutcome.RecoveryRequired,
            IncomingCallAdmissionState.Answering when admission.LeaseUntil <= now
                => IncomingCallAdmissionOutcome.RecoveryRequired,
            IncomingCallAdmissionState.FailedRetryable => IncomingCallAdmissionOutcome.Acquired,
            IncomingCallAdmissionState.Claimed when admission.LeaseUntil <= now
                => IncomingCallAdmissionOutcome.Acquired,
            _ => IncomingCallAdmissionOutcome.Busy
        };

    private static void ValidateIdentifiers(string serverCallId, string eventGridEventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverCallId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventGridEventId);
    }
}

namespace Agents.AI.ContactCenter.Coordination;

/// <summary>
/// Coordinates the one pod allowed to perform the external ACS AnswerCall side effect for an
/// IncomingCall delivery. The ACS server call id is the admission key; Event Grid event ids are
/// delivery observations and must not create independent admission records.
/// </summary>
public interface IIncomingCallAdmissionController
{
    Task<IncomingCallAdmissionResult> TryAcquireAsync(
        string serverCallId,
        string eventGridEventId,
        CancellationToken cancellationToken = default);

    Task<bool> MarkAnsweringAsync(string serverCallId, CancellationToken cancellationToken = default);

    Task<bool> MarkAnsweredAsync(
        string serverCallId,
        string callConnectionId,
        CancellationToken cancellationToken = default);

    Task<bool> MarkAnswerFailedAsync(
        string serverCallId,
        bool retryable,
        CancellationToken cancellationToken = default);

    Task<IncomingCallAdmission?> GetAsync(
        string serverCallId,
        CancellationToken cancellationToken = default);
}

public enum IncomingCallAdmissionState
{
    Claimed = 0,
    Answering = 1,
    Answered = 2,
    FailedRetryable = 3,
    RecoveryRequired = 4
}

public enum IncomingCallAdmissionOutcome
{
    Acquired,
    Busy,
    AlreadyAnswered,
    RecoveryRequired
}

public sealed record IncomingCallAdmission(
    string ServerCallId,
    string EventGridEventId,
    string ClusterId,
    string PodId,
    string InstanceId,
    IncomingCallAdmissionState State,
    DateTimeOffset LeaseUntil,
    string? CallConnectionId = null);

public readonly record struct IncomingCallAdmissionResult(
    IncomingCallAdmissionOutcome Outcome,
    IncomingCallAdmission Admission)
{
    public bool Acquired => Outcome is IncomingCallAdmissionOutcome.Acquired;
}

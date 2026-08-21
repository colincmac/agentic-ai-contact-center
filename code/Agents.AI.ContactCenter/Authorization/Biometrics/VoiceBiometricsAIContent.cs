using Microsoft.Extensions.AI;

namespace Agents.AI.ContactCenter.Authorization.Biometrics;

public class  BiometricVoiceEnrollmentStarted : AIContent
{
    public BiometricVoiceEnrollmentStarted(string participantId, DateTimeOffset startedAt)
    {
        ParticipantId = participantId;
        StartedAt = startedAt;
    }

    public string ParticipantId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
}

public class BiometricVoiceEnrollmentEnded : AIContent
{
    public BiometricVoiceEnrollmentEnded(string participantId, DateTimeOffset endedAt)
    {
        ParticipantId = participantId;
        EndedAt = endedAt;
    }

    public string ParticipantId { get; set; }
    public DateTimeOffset EndedAt { get; set; }
}

public class BiometricVoiceVerificationStarted : AIContent
{
    public BiometricVoiceVerificationStarted(string participantId, DateTimeOffset startedAt)
    {
        ParticipantId = participantId;
        StartedAt = startedAt;
    }

    public string ParticipantId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
}

public class BiometricVoiceVerificationEnded : AIContent
{
    public BiometricVoiceVerificationEnded(string participantId, DateTimeOffset endedAt)
    {
        ParticipantId = participantId;
        EndedAt = endedAt;
    }

    public string ParticipantId { get; set; }
    public DateTimeOffset EndedAt { get; set; }
}

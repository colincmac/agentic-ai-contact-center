using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using Agents.AI.ContactCenter.State.Projections;

namespace Agents.AI.ContactCenter.Authentication;

public static class CallerEvidencePolicy
{
    public static bool Satisfies(AuthSnapshot state, AuthStepGroup group, TimeSpan maxAge, DateTimeOffset now)
        => group.AuthenticatorNames.Any(name =>
        {
            var proof = state.GetRequirement(name);
            return proof.Satisfied
                && state.UserId != CallerIdentity.Anonymous.UserId
                && proof.SubjectId == state.UserId
                && proof.VerifiedAt is { } at && at <= now && now - at <= maxAge;
        });
}

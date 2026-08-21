namespace Agents.AI.ContactCenter.Authentication;

/// <summary>
/// A follow-up step the caller must complete to elevate their verification level.
/// Surfaced from <see cref="ICallerAuthenticator"/> implementations that cannot resolve
/// identity passively (e.g. SMS OTP, voice biometric enrollment, Entra Verified ID).
/// </summary>
/// <param name="Method">Discriminator describing how the caller satisfies the challenge.</param>
/// <param name="Prompt">Operator/agent-facing instructions to read or play to the caller.</param>
/// <param name="ChallengeId">Stable id correlating the challenge with the verification attempt.</param>
/// <param name="ExpiresAt">UTC time after which the challenge is no longer valid.</param>
/// <param name="Metadata">Free-form metadata for the implementing authenticator.</param>
public sealed record AuthenticationChallenge(
    AuthenticationMethod Method,
    string Prompt,
    string ChallengeId,
    DateTimeOffset ExpiresAt,
    IReadOnlyDictionary<string, object?>? Metadata = null);

public enum AuthenticationMethod
{
    /// <summary>Look up the caller's identity from their inbound (ANI) phone number.</summary>
    AniLookup,

    /// <summary>One-time code delivered via SMS.</summary>
    SmsOtp,

    /// <summary>One-time code delivered via email.</summary>
    EmailOtp,

    /// <summary>Push notification to a registered authenticator app.</summary>
    AuthenticatorPush,

    /// <summary>Magic-link delivered via SMS or email.</summary>
    MagicLink,

    /// <summary>Spoken phrase compared against an enrolled voice biometric profile.</summary>
    VoiceBiometric,

    /// <summary>Entra Verified ID credential presentation.</summary>
    EntraVerifiedId,

    /// <summary>Knowledge-based question (e.g. last 4 of SSN, recent transaction amount).</summary>
    KnowledgeBased
}

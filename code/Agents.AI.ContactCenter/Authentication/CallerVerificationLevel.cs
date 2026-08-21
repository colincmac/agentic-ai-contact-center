namespace Agents.AI.ContactCenter.Authentication;

/// <summary>
/// The strength of trust the platform places in the caller's claimed identity.
/// Authenticators are expected to set this to the strongest level they have proof of;
/// the orchestrator promotes the active <see cref="CallerIdentity"/> to whichever level
/// is highest across all successful authenticators.
/// </summary>
public enum CallerVerificationLevel
{
    /// <summary>No verification has been performed (or all attempts failed).</summary>
    None = 0,

    /// <summary>Caller was matched to a known identity by ANI / inbound phone number.</summary>
    AniMatch = 10,

    /// <summary>Caller answered knowledge-based questions (e.g. last 4 of SSN, recent transaction).</summary>
    KnowledgeBased = 20,

    /// <summary>Caller passed an out-of-band MFA challenge (SMS OTP, email link, push notification).</summary>
    MultiFactor = 30,

    /// <summary>Caller's voice matched an enrolled biometric profile within tolerance.</summary>
    VoiceBiometric = 40,

    /// <summary>Caller presented an Entra Verified ID credential.</summary>
    EntraVerifiedId = 50,

    /// <summary>Multiple high-assurance methods succeeded — strongest available trust.</summary>
    Strong = 60
}

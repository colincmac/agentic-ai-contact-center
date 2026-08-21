namespace Agents.AI.ContactCenter.Authentication;

/// <summary>
/// Discriminated outcome returned by an <see cref="ICallerAuthenticator"/>.
/// </summary>
public abstract record AuthenticationOutcome
{
    
    private AuthenticationOutcome() { }

    /// <summary>
    /// The authenticator established (or elevated) the caller's identity.
    /// </summary>
    public sealed record Authenticated(CallerIdentity Identity) : AuthenticationOutcome;

    /// <summary>
    /// The authenticator was not applicable to this caller — for example, ANI lookup with no
    /// matching record. The orchestrator continues to the next authenticator without raising
    /// a failure event.
    /// </summary>
    public sealed record NotApplicable(string Reason) : AuthenticationOutcome;

    /// <summary>
    /// The authenticator attempted verification and the caller failed (wrong code, voice mismatch,
    /// biometric below threshold, etc.). The orchestrator records the failure and may stop the
    /// chain depending on configuration.
    /// </summary>
    public sealed record Failed(string Reason) : AuthenticationOutcome;

    /// <summary>
    /// Verification cannot complete without caller interaction. The orchestrator surfaces the
    /// challenge as a <c>StrategyEvent.CallerAuthenticationChallenge</c>; the strategy is
    /// expected to drive the caller through it (typically via tools the IVR navigator exposes).
    /// </summary>
    public sealed record NeedsChallenge(AuthenticationChallenge Challenge) : AuthenticationOutcome;
}

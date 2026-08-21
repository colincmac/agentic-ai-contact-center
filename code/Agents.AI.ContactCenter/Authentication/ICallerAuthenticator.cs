namespace Agents.AI.ContactCenter.Authentication;

/// <summary>
/// One pluggable verification method (ANI lookup, MFA, voice biometric, Verified ID, …).
/// Implementations are stateless; per-call mutable state lives on <see cref="CallerAuthenticationState"/>.
/// </summary>
public interface ICallerAuthenticator
{
    /// <summary>Stable name surfaced in events and telemetry.</summary>
    string Name { get; }

    /// <summary>
    /// Run this authenticator against the supplied context.
    /// </summary>
    Task<AuthenticationOutcome> AuthenticateAsync(AuthenticationContext context, CancellationToken cancellationToken = default);
}

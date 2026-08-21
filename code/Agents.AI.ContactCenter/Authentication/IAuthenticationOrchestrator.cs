namespace Agents.AI.ContactCenter.Authentication;

/// <summary>
/// Aggregate result of running an ordered set of <see cref="ICallerAuthenticator"/>s.
/// </summary>
/// <param name="Identity">
/// The strongest identity established (or <see cref="CallerIdentity.Anonymous"/> when nothing succeeded).
/// </param>
/// <param name="Steps">One <see cref="AuthenticationStep"/> per authenticator the orchestrator executed.</param>
public sealed record AuthenticationRunResult(CallerIdentity Identity, IReadOnlyList<AuthenticationStep> Steps);

/// <summary>One authenticator's contribution to an <see cref="AuthenticationRunResult"/>.</summary>
/// <param name="AuthenticatorName">Value of <see cref="ICallerAuthenticator.Name"/>.</param>
/// <param name="Outcome">The discriminated outcome the authenticator returned.</param>
/// <param name="At">UTC timestamp at which the authenticator finished.</param>
public sealed record AuthenticationStep(string AuthenticatorName, AuthenticationOutcome Outcome, DateTimeOffset At);

/// <summary>
/// Composes one or more <see cref="ICallerAuthenticator"/> runs into a single decision for the call.
/// </summary>
/// <remarks>
/// Implementations decide ordering, short-circuiting (e.g. stop on first <see cref="AuthenticationOutcome.Failed"/>),
/// and which authenticators they include. Strategies should resolve this via DI, run it once at call start,
/// and rerun it if the navigator transitions to a workflow step that demands a higher verification level.
/// </remarks>
public interface IAuthenticationOrchestrator
{
    /// <summary>
    /// Run the configured chain of authenticators, threading the strongest identity from one authenticator
    /// to the next and returning the aggregate result. The authoritative identity + audit trail are produced
    /// by folding the resulting steps' events into the call's <c>AuthSnapshot</c> (done by the caller).
    /// </summary>
    Task<AuthenticationRunResult> AuthenticateAsync(
        AuthenticationContext context,
        CancellationToken cancellationToken = default);
}

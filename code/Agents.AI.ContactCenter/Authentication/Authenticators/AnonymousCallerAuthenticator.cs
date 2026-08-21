namespace Agents.AI.ContactCenter.Authentication.Authenticators;

/// <summary>
/// Default authenticator that always returns <see cref="AuthenticationOutcome.NotApplicable"/>.
/// Registered automatically by <c>AddCallerAuthentication</c> so the orchestrator has at least
/// one authenticator to enumerate even when the host has not added any concrete methods.
/// </summary>
public sealed class AnonymousCallerAuthenticator : ICallerAuthenticator
{
    public string Name => "Anonymous";

    public Task<AuthenticationOutcome> AuthenticateAsync(AuthenticationContext context, CancellationToken cancellationToken = default)
        => Task.FromResult<AuthenticationOutcome>(new AuthenticationOutcome.NotApplicable("Anonymous authenticator is a placeholder."));
}

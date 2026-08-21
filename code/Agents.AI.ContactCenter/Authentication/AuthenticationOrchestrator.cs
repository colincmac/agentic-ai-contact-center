using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Authentication;

/// <summary>
/// Default <see cref="IAuthenticationOrchestrator"/>. Runs each registered
/// <see cref="ICallerAuthenticator"/> in DI registration order, threading the strongest identity from one
/// authenticator to the next, and short-circuiting on the first <see cref="AuthenticationOutcome.Failed"/>
/// when <see cref="StopOnFailure"/> is true (the default). Stateless: the authoritative identity and audit
/// trail are produced by folding the returned steps into the call's <c>AuthSnapshot</c>.
/// </summary>
public sealed class AuthenticationOrchestrator : IAuthenticationOrchestrator
{
    private readonly IReadOnlyList<ICallerAuthenticator> _authenticators;
    private readonly ILogger<AuthenticationOrchestrator> _logger;

    public AuthenticationOrchestrator(
        IEnumerable<ICallerAuthenticator> authenticators,
        ILogger<AuthenticationOrchestrator>? logger = null)
    {
        _authenticators = [.. authenticators];
        _logger = logger ?? NullLogger<AuthenticationOrchestrator>.Instance;
    }

    /// <summary>When true, stop running authenticators on first <see cref="AuthenticationOutcome.Failed"/>.</summary>
    public bool StopOnFailure { get; init; } = true;

    /// <summary>When true, stop running authenticators as soon as one returns <see cref="AuthenticationOutcome.NeedsChallenge"/>.</summary>
    public bool StopOnChallenge { get; init; } = true;

    public async Task<AuthenticationRunResult> AuthenticateAsync(
        AuthenticationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Thread the strongest identity locally through the chain. The authoritative strengthen + audit
        // happens when the run's CallerIdentified events fold into the call's AuthSnapshot.
        var identity = context.CurrentIdentity ?? CallerIdentity.Anonymous;

        if (_authenticators.Count == 0)
        {
            _logger.LogDebug("No ICallerAuthenticator registered; returning anonymous identity for call {CallId}", context.CallId);
            return new AuthenticationRunResult(identity, []);
        }

        var steps = new List<AuthenticationStep>(_authenticators.Count);

        foreach (var authenticator in _authenticators)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AuthenticationOutcome outcome;
            try
            {
                var stepContext = context with { CurrentIdentity = identity };
                outcome = await authenticator.AuthenticateAsync(stepContext, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Authenticator {Name} threw for call {CallId}", authenticator.Name, context.CallId);
                outcome = new AuthenticationOutcome.Failed($"Authenticator '{authenticator.Name}' threw: {ex.Message}");
            }

            var step = new AuthenticationStep(authenticator.Name, outcome, DateTimeOffset.UtcNow);
            steps.Add(step);

            switch (outcome)
            {
                case AuthenticationOutcome.Authenticated authenticated:
                    if (authenticated.Identity.VerificationLevel >= identity.VerificationLevel)
                    {
                        identity = authenticated.Identity;
                    }
                    break;

                case AuthenticationOutcome.NeedsChallenge:
                    if (StopOnChallenge) { return new AuthenticationRunResult(identity, steps); }
                    break;

                case AuthenticationOutcome.Failed:
                    if (StopOnFailure) { return new AuthenticationRunResult(identity, steps); }
                    break;

                case AuthenticationOutcome.NotApplicable:
                    // Fall through to next authenticator.
                    break;
            }
        }

        return new AuthenticationRunResult(identity, steps);
    }
}

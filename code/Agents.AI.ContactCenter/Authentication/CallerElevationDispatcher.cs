using System.Threading.Channels;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.State;
using Agents.AI.ContactCenter.State.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Authentication;

/// <summary>
/// Default <see cref="ICallerElevationDispatcher"/>. Looks up the named authenticator from
/// the DI-registered <see cref="ICallerAuthenticator"/> enumerable, runs it, and projects each
/// outcome onto the supplied strategy event channel. Caller identity and verification level are read
/// from the call's folded <see cref="AuthSnapshot"/>; the dispatcher never mutates state directly —
/// the emitted events fold into the snapshot synchronously through the strategy's emit funnel.
/// </summary>
public sealed class CallerElevationDispatcher : ICallerElevationDispatcher
{
    private readonly IReadOnlyDictionary<string, ICallerAuthenticator> _authenticatorsByName;
    private readonly IServiceProvider _services;
    private readonly ILogger<CallerElevationDispatcher> _logger;

    public CallerElevationDispatcher(
        IEnumerable<ICallerAuthenticator> authenticators,
        IServiceProvider services,
        ILogger<CallerElevationDispatcher>? logger = null)
    {
        _services = services;
        _logger = logger ?? NullLogger<CallerElevationDispatcher>.Instance;
        _authenticatorsByName = authenticators
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
    }

    private AuthSnapshot Auth => _services.GetService<CallStateProjector>()?.Get<AuthSnapshot>() ?? AuthSnapshot.Empty;

    public async Task<AuthenticationRunResult> DispatchAsync(
        string authenticatorName,
        string callId,
        CallEdgeMetadata? callerMetadata = null,
        ChannelWriter<StrategyEvent>? events = null,
        IReadOnlyDictionary<string, string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(authenticatorName);
        ArgumentException.ThrowIfNullOrEmpty(callId);

        if (!_authenticatorsByName.TryGetValue(authenticatorName, out var authenticator))
        {
            _logger.LogWarning(
                "No ICallerAuthenticator named '{Name}' is registered for call {CallId}; dispatch is a no-op.",
                authenticatorName, callId);
            return new AuthenticationRunResult(Auth.Identity, []);
        }

        var previousLevel = Auth.Level;

        var context = new AuthenticationContext(
            CallId: callId,
            CallerMetadata: callerMetadata,
            CurrentIdentity: Auth.Identity,
            Services: _services,
            Tags: tags);

        AuthenticationOutcome outcome;
        try
        {
            outcome = await authenticator.AuthenticateAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Authenticator {Name} threw during elevation dispatch for call {CallId}", authenticator.Name, callId);
            outcome = new AuthenticationOutcome.Failed($"Authenticator '{authenticator.Name}' threw: {ex.Message}");
        }

        var step = new AuthenticationStep(authenticator.Name, outcome, DateTimeOffset.UtcNow);

        // Emit-only: the AuthStateProjection folds these into the audit trail, identity, pending challenge,
        // and verification level. No direct state mutation.
        switch (outcome)
        {
            case AuthenticationOutcome.Authenticated authenticated:
                if (events is not null)
                {
                    await events.WriteAsync(
                        new StrategyEvent.CallerIdentified(authenticated.Identity, authenticator.Name, step.At),
                        cancellationToken).ConfigureAwait(false);
                }
                break;

            case AuthenticationOutcome.Failed failed:
                if (events is not null)
                {
                    await events.WriteAsync(
                        new StrategyEvent.CallerAuthenticationFailed(authenticator.Name, failed.Reason, step.At),
                        cancellationToken).ConfigureAwait(false);
                }
                break;

            case AuthenticationOutcome.NeedsChallenge challenge:
                if (events is not null)
                {
                    await events.WriteAsync(
                        new StrategyEvent.CallerAuthenticationChallenge(challenge.Challenge, step.At),
                        cancellationToken).ConfigureAwait(false);
                }
                break;
        }

        var currentLevel = Auth.Level;
        if (events is not null && currentLevel != previousLevel)
        {
            await events.WriteAsync(
                new StrategyEvent.CallerVerificationLevelChanged(previousLevel, currentLevel, DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
        }

        return new AuthenticationRunResult(Auth.Identity, [step]);
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Authentication.Authenticators;

/// <summary>
/// Host-supplied PIN validator. Implementations check the caller-supplied digits against
/// the back-office system (CRM, vault, banking core). Returning <see langword="null"/>
/// means "no PIN on file"; returning <c>false</c> means "wrong PIN".
/// </summary>
public interface IPinValidator
{
    /// <summary>
    /// Validate <paramref name="digits"/> against the PIN on file for
    /// <paramref name="identity"/>. Returns <see langword="true"/> on match,
    /// <see langword="false"/> on mismatch, <see langword="null"/> when no PIN is on file.
    /// </summary>
    Task<bool?> ValidateAsync(CallerIdentity identity, string digits, CancellationToken cancellationToken = default);
}

/// <summary>
/// Elevates a caller already identified by an upstream authenticator (typically
/// <see cref="AniIdentityLookupAuthenticator"/>) to <see cref="CallerVerificationLevel.KnowledgeBased"/>
/// after they correctly supply their PIN. The actual digits are read from a per-call
/// <see cref="PinAttempt"/> resolved out of the request's DI scope, so any surface that can
/// gather a PIN (DTMF collector, realtime tool, web form) can drive this authenticator
/// through the orchestrator instead of mutating <see cref="CallerAuthenticationState"/> directly.
/// </summary>
/// <remarks>
/// To trigger this authenticator from a tool / IVR step:
/// <code>
/// var attempt = services.GetRequiredService&lt;PinAttempt&gt;();
/// attempt.Digits = "4242";
/// var orchestrator = services.GetRequiredService&lt;IAuthenticationOrchestrator&gt;();
/// var state = services.GetRequiredService&lt;CallerAuthenticationState&gt;();
/// await orchestrator.AuthenticateAsync(context, state, ct);
/// </code>
/// </remarks>
public sealed class PinAuthenticator : ICredentialAuthenticator
{
    private readonly ILogger<PinAuthenticator> _logger;

    public PinAuthenticator(ILogger<PinAuthenticator>? logger = null)
    {
        _logger = logger ?? NullLogger<PinAuthenticator>.Instance;
    }

    public string Name => "Pin";

    public CallerVerificationLevel ElevatesTo => CallerVerificationLevel.KnowledgeBased;

    public CallerVerificationLevel RequiredPriorLevel => CallerVerificationLevel.AniMatch;

    public CredentialRequest DescribeRequest(AuthenticationContext context) => new()
    {
        AuthenticatorName = Name,
        Kind = CredentialKind.Digits,
        Purpose = "your PIN",
        RealtimeInstruction = "Ask the caller for their PIN. When they say or key it, call submit_credential with authenticator \"Pin\" and the digits.",
        SsmlPrompt = "Please enter your PIN, followed by the pound key.",
        MinLength = 4,
        MaxLength = 8,
        Secret = true,
    };

    public void StashInput(AuthenticationContext context, CredentialInput input)
    {
        var attempt = context.Services.GetService<PinAttempt>();
        if (attempt is null)
        {
            _logger.LogWarning("PinAuthenticator.StashInput called but no PinAttempt is registered.");
            return;
        }
        attempt.Digits = input.Value;
    }

    public async Task<AuthenticationOutcome> AuthenticateAsync(AuthenticationContext context, CancellationToken cancellationToken = default)
    {
        var attempt = context.Services.GetService<PinAttempt>();
        var validator = context.Services.GetService<IPinValidator>();

        if (string.IsNullOrEmpty(attempt?.Digits))
        {
            // No PIN being submitted on this orchestrator run; skip silently so the chain
            // can still execute passive authenticators (ANI, etc.).
            return new AuthenticationOutcome.NotApplicable("No PIN attempt in scope.");
        }

        if (context.CurrentIdentity.UserId == CallerIdentity.Anonymous.UserId)
        {
            return new AuthenticationOutcome.Failed("Caller must be identified before PIN can be validated.");
        }

        if (validator is null)
        {
            _logger.LogWarning("PinAuthenticator invoked but no IPinValidator is registered.");
            return new AuthenticationOutcome.NotApplicable("No IPinValidator registered.");
        }

        var digits = attempt.Digits;
        // One-shot: clear the attempt so a stale value can't re-elevate later.
        attempt.Digits = null;

        var result = await validator.ValidateAsync(context.CurrentIdentity, digits, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return new AuthenticationOutcome.Failed("No PIN on file for caller.");
        }
        if (result is false)
        {
            return new AuthenticationOutcome.Failed("Incorrect PIN.");
        }

        var elevated = context.CurrentIdentity with
        {
            VerificationLevel = CallerVerificationLevel.KnowledgeBased,
            AuthenticatedBy = Name,
            AuthenticatedAt = DateTimeOffset.UtcNow,
        };
        _logger.LogInformation(
            "Caller {UserId} elevated to KnowledgeBased via PinAuthenticator on call {CallId}",
            context.CurrentIdentity.UserId, context.CallId);
        return new AuthenticationOutcome.Authenticated(elevated);
    }
}

/// <summary>
/// Per-call mutable container that surfaces the digits the caller most recently supplied
/// to <see cref="PinAuthenticator"/>. Registered as <c>Scoped</c> so the same instance is
/// visible to the tool gathering the PIN and the authenticator validating it.
/// </summary>
public sealed class PinAttempt
{
    /// <summary>The digits collected from the caller, or <see langword="null"/> when no attempt is pending.</summary>
    public string? Digits { get; set; }
}

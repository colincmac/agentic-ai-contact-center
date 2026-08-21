using Agents.AI.ContactCenter.State;
using Agents.AI.ContactCenter.State.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Authentication.Authenticators;

/// <summary>
/// Establishes a knowledge-based identity for a caller who could not be matched by ANI by
/// looking them up on the last four digits of their account / card (optionally narrowed by a
/// name the caller already gave). The digits are read from a per-call <see cref="Last4Attempt"/>
/// so any modality — DTMF collector, realtime submit tool — can drive it through the inline
/// authentication plan without mutating <see cref="CallerAuthenticationState"/> directly.
/// </summary>
public sealed class IdentifyByLast4Authenticator : ICredentialAuthenticator
{
    private readonly ICallerDirectory _directory;
    private readonly ILogger<IdentifyByLast4Authenticator> _logger;

    public IdentifyByLast4Authenticator(
        ICallerDirectory directory,
        ILogger<IdentifyByLast4Authenticator>? logger = null)
    {
        _directory = directory;
        _logger = logger ?? NullLogger<IdentifyByLast4Authenticator>.Instance;
    }

    public string Name => "IdentifyLast4";

    public CallerVerificationLevel ElevatesTo => CallerVerificationLevel.KnowledgeBased;

    public CallerVerificationLevel RequiredPriorLevel => CallerVerificationLevel.None;

    public CredentialRequest DescribeRequest(AuthenticationContext context) => new()
    {
        AuthenticatorName = Name,
        Kind = CredentialKind.Digits,
        Purpose = "the last four digits of your account",
        RealtimeInstruction =
            "Ask the caller for the last four digits of their account or card number. When they " +
            "provide them, call submit_credential with authenticator \"IdentifyLast4\" and the digits.",
        SsmlPrompt = "Please enter the last four digits of your account number, followed by the pound key.",
        MinLength = 4,
        MaxLength = 4,
        Secret = false,
    };

    public void StashInput(AuthenticationContext context, CredentialInput input)
    {
        var attempt = context.Services.GetService<Last4Attempt>();
        if (attempt is null)
        {
            _logger.LogWarning("IdentifyByLast4Authenticator.StashInput called but no Last4Attempt is registered.");
            return;
        }
        attempt.Digits = input.Value;
    }

    public async Task<AuthenticationOutcome> AuthenticateAsync(AuthenticationContext context, CancellationToken cancellationToken = default)
    {
        var attempt = context.Services.GetService<Last4Attempt>();
        if (string.IsNullOrEmpty(attempt?.Digits))
        {
            return new AuthenticationOutcome.NotApplicable("No last-4 attempt in scope.");
        }

        var last4 = attempt.Digits;
        // One-shot: clear so a stale value can't re-identify later.
        attempt.Digits = null;

        var nameHint = context.Services.GetService<CallStateProjector>()?.Get<IvrSnapshot>().Slots.GetValueOrDefault("CallerFullName");

        var match = await _directory.FindByAccountLast4Async(last4, nameHint, cancellationToken).ConfigureAwait(false);
        if (match is null)
        {
            return new AuthenticationOutcome.Failed("No account matched those digits.");
        }

        var identity = match with
        {
            VerificationLevel = CallerVerificationLevel.KnowledgeBased,
            AuthenticatedBy = Name,
            AuthenticatedAt = DateTimeOffset.UtcNow,
        };

        _logger.LogInformation(
            "Identified caller {UserId} ({DisplayName}) by account last-4 on call {CallId}",
            identity.UserId, identity.DisplayName, context.CallId);

        return new AuthenticationOutcome.Authenticated(identity);
    }
}

/// <summary>
/// Per-call mutable container that surfaces the account last-4 digits the caller most recently
/// supplied to <see cref="IdentifyByLast4Authenticator"/>. Registered as <c>Scoped</c>.
/// </summary>
public sealed class Last4Attempt
{
    /// <summary>The last-4 digits collected from the caller, or <see langword="null"/> when none pending.</summary>
    public string? Digits { get; set; }
}

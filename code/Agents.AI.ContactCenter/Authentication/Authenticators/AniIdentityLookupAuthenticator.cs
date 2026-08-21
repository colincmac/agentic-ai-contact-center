using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Authentication.Authenticators;

/// <summary>
/// Resolves the caller's identity from the inbound (ANI) phone number on the caller edge by
/// delegating to a host-supplied <see cref="ICallerDirectory"/>. Produces an identity at
/// <see cref="CallerVerificationLevel.AniMatch"/> on success.
/// </summary>
/// <remarks>
/// ANI alone is a weak signal (caller-id can be spoofed). Hosts that need stronger trust
/// should chain a higher-assurance authenticator (MFA, voice biometric, Verified ID) after
/// this one in the orchestrator.
/// </remarks>
public sealed class AniIdentityLookupAuthenticator : ICallerAuthenticator
{
    private readonly ICallerDirectory _directory;
    private readonly ILogger<AniIdentityLookupAuthenticator> _logger;

    public AniIdentityLookupAuthenticator(
        ICallerDirectory directory,
        ILogger<AniIdentityLookupAuthenticator>? logger = null)
    {
        _directory = directory;
        _logger = logger ?? NullLogger<AniIdentityLookupAuthenticator>.Instance;
    }

    public string Name => "AniLookup";

    public async Task<AuthenticationOutcome> AuthenticateAsync(AuthenticationContext context, CancellationToken cancellationToken = default)
    {
        if (context.CallerMetadata is null)
        {
            return new AuthenticationOutcome.NotApplicable("No caller-edge metadata available for ANI lookup.");
        }

        var phone = NormalizePhoneNumber(context.CallerMetadata.RawIdentifier);
        if (string.IsNullOrEmpty(phone))
        {
            _logger.LogDebug("Caller edge for call {CallId} has no usable phone identifier; skipping ANI lookup", context.CallId);
            return new AuthenticationOutcome.NotApplicable("Caller edge has no E.164 phone identifier.");
        }

        var match = await _directory.FindByPhoneNumberAsync(phone, cancellationToken).ConfigureAwait(false);
        if (match is null)
        {
            _logger.LogInformation("No directory entry found for inbound number {Phone} on call {CallId}", phone, context.CallId);
            return new AuthenticationOutcome.NotApplicable($"No directory entry for {phone}.");
        }

        var identity = match with
        {
            VerificationLevel = CallerVerificationLevel.AniMatch,
            AuthenticatedBy = Name,
            AuthenticatedAt = DateTimeOffset.UtcNow
        };

        _logger.LogInformation(
            "Resolved caller {UserId} ({DisplayName}) by ANI {Phone} on call {CallId}",
            identity.UserId, identity.DisplayName, phone, context.CallId);

        return new AuthenticationOutcome.Authenticated(identity);
    }

    private static string NormalizePhoneNumber(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) { return string.Empty; }

        // ACS RawId for PSTN endpoints looks like "4:+15551234567"; strip the type prefix.
        var colon = raw.IndexOf(':');
        var candidate = colon >= 0 ? raw[(colon + 1)..] : raw;

        return candidate.StartsWith('+') ? candidate : $"+{candidate}";
    }
}

using Agents.AI.ContactCenter.State;
using Agents.AI.ContactCenter.State.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Authentication.Authenticators;

/// <summary>
/// Host-supplied SMS delivery surface. Implementations forward the OTP body to the
/// caller-registered phone number through whichever SMS provider the host uses
/// (ACS SMS, Twilio, …). The default implementation only logs the code which is
/// fine for tests / showcase but must be replaced for production.
/// </summary>
public interface ISmsOtpSender
{
    Task SendAsync(string phoneNumberE164, string code, CancellationToken cancellationToken = default);
}

/// <summary>
/// MFA authenticator that issues a one-time code via <see cref="ISmsOtpSender"/>, persists
/// the expected secret in <see cref="IChallengeStore"/>, and resolves the challenge when the
/// caller supplies the code through a per-call <see cref="SmsOtpAttempt"/>.
/// </summary>
/// <remarks>
/// First invocation issues the OTP and returns <see cref="AuthenticationOutcome.NeedsChallenge"/>
/// so the strategy / IVR step can collect digits from the caller. The follow-up invocation
/// (triggered by the same authenticator through the orchestrator) consumes the digits on
/// <see cref="SmsOtpAttempt"/> and returns <see cref="AuthenticationOutcome.Authenticated"/>
/// at <see cref="CallerVerificationLevel.MultiFactor"/> on success.
/// </remarks>
public sealed class SmsOtpAuthenticator : ICredentialAuthenticator
{
    private readonly TimeSpan _ttl;
    private readonly ILogger<SmsOtpAuthenticator> _logger;

    public SmsOtpAuthenticator(ILogger<SmsOtpAuthenticator>? logger = null, TimeSpan? challengeTtl = null)
    {
        _logger = logger ?? NullLogger<SmsOtpAuthenticator>.Instance;
        _ttl = challengeTtl ?? TimeSpan.FromMinutes(5);
    }

    public string Name => "SmsOtp";

    public CallerVerificationLevel ElevatesTo => CallerVerificationLevel.MultiFactor;

    public CallerVerificationLevel RequiredPriorLevel => CallerVerificationLevel.AniMatch;

    public CredentialRequest DescribeRequest(AuthenticationContext context) => new()
    {
        AuthenticatorName = Name,
        Kind = CredentialKind.OutOfBandCode,
        Purpose = "the 6-digit code we just sent you",
        RealtimeInstruction = "A one-time code has been sent to the caller. Ask them to read it back, then call submit_credential with authenticator \"SmsOtp\" and the code.",
        SsmlPrompt = "Please enter the 6-digit code we just sent to your phone, followed by the pound key.",
        MinLength = 6,
        MaxLength = 6,
        Secret = true,
    };

    public void StashInput(AuthenticationContext context, CredentialInput input)
    {
        var attempt = context.Services.GetService<SmsOtpAttempt>();
        if (attempt is null)
        {
            _logger.LogWarning("SmsOtpAuthenticator.StashInput called but no SmsOtpAttempt is registered.");
            return;
        }
        attempt.Code = input.Value;
        attempt.ChallengeId ??= context.Services.GetService<CallStateProjector>()?.Get<AuthSnapshot>().PendingChallenge?.ChallengeId;
    }

    public async Task<AuthenticationOutcome> AuthenticateAsync(AuthenticationContext context, CancellationToken cancellationToken = default)
    {
        if (context.CurrentIdentity.UserId == CallerIdentity.Anonymous.UserId
            || string.IsNullOrWhiteSpace(context.CurrentIdentity.PhoneNumber))
        {
            return new AuthenticationOutcome.NotApplicable("SMS OTP requires an identified caller with a phone number on file.");
        }

        var store = context.Services.GetService<IChallengeStore>();
        if (store is null)
        {
            return new AuthenticationOutcome.NotApplicable("No IChallengeStore registered.");
        }

        var attempt = context.Services.GetService<SmsOtpAttempt>();

        // caller submitted a code → validate it.
        if (attempt is not null && !string.IsNullOrEmpty(attempt.Code) && !string.IsNullOrEmpty(attempt.ChallengeId))
        {
            var record = await store.GetAsync(attempt.ChallengeId, cancellationToken).ConfigureAwait(false);
            var submittedCode = attempt.Code;
            var submittedChallengeId = attempt.ChallengeId;

            // One-shot: clear the attempt regardless of outcome.
            attempt.Code = null;
            attempt.ChallengeId = null;

            if (record is null)
            {
                return new AuthenticationOutcome.Failed("OTP challenge expired or unknown.");
            }
            if (!string.Equals(record.UserId, context.CurrentIdentity.UserId, StringComparison.Ordinal))
            {
                return new AuthenticationOutcome.Failed("OTP challenge does not belong to this caller.");
            }
            if (!string.Equals(record.Secret, submittedCode, StringComparison.Ordinal))
            {
                return new AuthenticationOutcome.Failed("Incorrect OTP code.");
            }

            await store.RemoveAsync(submittedChallengeId, cancellationToken).ConfigureAwait(false);

            var elevated = context.CurrentIdentity with
            {
                VerificationLevel = CallerVerificationLevel.MultiFactor,
                AuthenticatedBy = Name,
                AuthenticatedAt = DateTimeOffset.UtcNow,
            };
            _logger.LogInformation(
                "Caller {UserId} elevated to MultiFactor via SMS OTP on call {CallId}",
                context.CurrentIdentity.UserId, context.CallId);
            return new AuthenticationOutcome.Authenticated(elevated);
        }

        // Phase 1: issue a fresh OTP challenge.
        var sender = context.Services.GetService<ISmsOtpSender>();
        if (sender is null)
        {
            return new AuthenticationOutcome.NotApplicable("No ISmsOtpSender registered.");
        }

        var code = GenerateOtp();
        var challengeId = Guid.NewGuid().ToString("N");
        var expiresAt = DateTimeOffset.UtcNow.Add(_ttl);

        await store.SaveAsync(
            challengeId,
            new ChallengeRecord(context.CurrentIdentity.UserId, AuthenticationMethod.SmsOtp, code, expiresAt),
            cancellationToken).ConfigureAwait(false);

        try
        {
            await sender.SendAsync(context.CurrentIdentity.PhoneNumber!, code, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send SMS OTP for call {CallId}", context.CallId);
            await store.RemoveAsync(challengeId, cancellationToken).ConfigureAwait(false);
            return new AuthenticationOutcome.Failed($"Unable to send OTP: {ex.Message}");
        }

        var challenge = new AuthenticationChallenge(
            Method: AuthenticationMethod.SmsOtp,
            Prompt: $"A 6-digit code has been sent to the phone number ending in {Last4(context.CurrentIdentity.PhoneNumber!)}. Please read it back.",
            ChallengeId: challengeId,
            ExpiresAt: expiresAt);

        _logger.LogInformation(
            "Issued SMS OTP challenge {ChallengeId} for caller {UserId} on call {CallId}",
            challengeId, context.CurrentIdentity.UserId, context.CallId);

        return new AuthenticationOutcome.NeedsChallenge(challenge);
    }

    private static string GenerateOtp()
        => System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    private static string Last4(string phone)
        => phone.Length <= 4 ? phone : phone[^4..];
}

/// <summary>
/// Per-call mutable container the strategy / tool sets when the caller supplies an OTP. Reset
/// to <see langword="null"/> after each consumption so a stale code can't re-elevate later.
/// </summary>
public sealed class SmsOtpAttempt
{
    /// <summary>Server-issued challenge id (from <see cref="AuthenticationChallenge.ChallengeId"/>).</summary>
    public string? ChallengeId { get; set; }

    /// <summary>The digits the caller supplied.</summary>
    public string? Code { get; set; }
}

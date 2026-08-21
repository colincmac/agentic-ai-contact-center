namespace Agents.AI.ContactCenter.Authentication;

/// <summary>
/// An <see cref="ICallerAuthenticator"/> that can be driven interactively by an inline
/// authentication plan: it advertises the verification level it grants, the level the caller
/// must already hold before it can run, and the <see cref="CredentialRequest"/> describing
/// the input to gather. The strategy renders that request for its modality; the executor
/// stashes the collected value on the authenticator's attempt buffer and dispatches it.
/// </summary>
public interface ICredentialAuthenticator : ICallerAuthenticator
{
    /// <summary>The verification level the caller reaches when this authenticator succeeds.</summary>
    CallerVerificationLevel ElevatesTo { get; }

    /// <summary>
    /// Minimum verification level the caller must already hold for this authenticator to be
    /// runnable. The plan runner skips (and reports) steps whose prerequisite is unmet.
    /// </summary>
    CallerVerificationLevel RequiredPriorLevel { get; }

    /// <summary>Describe the credential to collect from the caller for this run.</summary>
    CredentialRequest DescribeRequest(AuthenticationContext context);

    /// <summary>
    /// Stash <paramref name="input"/> on this authenticator's per-call attempt buffer so the
    /// subsequent <see cref="ICallerAuthenticator.AuthenticateAsync"/> run consumes it. Keeps
    /// the executor decoupled from per-authenticator buffer types (PinAttempt, SmsOtpAttempt…).
    /// </summary>
    void StashInput(AuthenticationContext context, CredentialInput input);
}

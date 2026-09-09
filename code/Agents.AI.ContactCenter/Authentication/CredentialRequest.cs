namespace Agents.AI.ContactCenter.Authentication;

/// <summary>
/// The shape of input a <see cref="ICredentialAuthenticator"/> needs from the caller. Each
/// per-tier strategy renders this descriptor with its own transport — realtime exposes a
/// submit tool + spoken instruction, DTMF synthesizes the SSML prompt and collects digits —
/// so the acquisition logic is authored once on the authenticator instead of once per channel.
/// </summary>
public enum CredentialKind
{
    /// <summary>A fixed-length run of DTMF / spoken digits (PIN, last-4, account number).</summary>
    Digits,

    /// <summary>Free-form spoken text (a name, a security answer).</summary>
    SpokenText,

    /// <summary>
    /// An out-of-band one-time code (SMS / email OTP). The executor triggers issuance on step
    /// entry, then collects the code the caller reads back.
    /// </summary>
    OutOfBandCode,
}

/// <summary>
/// Modality-agnostic description of one credential the caller must supply to satisfy an
/// authentication step. Produced by <see cref="ICredentialAuthenticator.DescribeRequest"/>
/// and consumed by every conversation strategy.
/// </summary>
public sealed record CredentialRequest
{
    /// <summary>Name of the <see cref="ICallerAuthenticator"/> this credential feeds. Case-insensitive.</summary>
    public required string AuthenticatorName { get; init; }

    /// <summary>What kind of input to collect — drives the per-modality acquisition surface.</summary>
    public required CredentialKind Kind { get; init; }

    /// <summary>Short human phrase naming the credential, e.g. "your 4-digit PIN". Used in generated prompts.</summary>
    public required string Purpose { get; init; }

    /// <summary>Instruction surfaced to the realtime model ("Ask the caller for X, then call submit_credential").</summary>
    public string? RealtimeInstruction { get; init; }

    /// <summary>SSML (or plain text) the DTMF / scripted tier synthesizes to prompt the caller.</summary>
    public string? SsmlPrompt { get; init; }

    /// <summary>Minimum acceptable length for <see cref="CredentialKind.Digits"/> input.</summary>
    public int? MinLength { get; init; }

    /// <summary>Maximum acceptable length for <see cref="CredentialKind.Digits"/> input. Collection auto-submits on reaching it.</summary>
    public int? MaxLength { get; init; }

    /// <summary>When true the value is sensitive (PIN, OTP) — strategies mask it in logs / transcripts.</summary>
    public bool Secret { get; init; }

    /// <summary>True for <see cref="CredentialKind.OutOfBandCode"/> — the executor issues the challenge before collecting.</summary>
    public bool OutOfBand => Kind == CredentialKind.OutOfBandCode;
}

/// <summary>
/// Raw input collected from the caller for a <see cref="CredentialRequest"/>, handed to
/// <c>WorkflowExecutor.SubmitCredentialAsync</c> by the active strategy.
/// </summary>
/// <param name="Value">The collected value (digits, spoken text, OTP code).</param>
public readonly record struct CredentialInput(string Value);

/// <summary>
/// Progress of a single authenticator within an inline authentication plan, tracked on
/// <see cref="CallerAuthenticationState"/> so a resumed tier knows what's already done and the
/// executor can bound retries.
/// </summary>
/// <param name="Satisfied">True once this authenticator has elevated the caller.</param>
/// <param name="Attempts">Number of credential attempts made against this authenticator.</param>
/// <param name="LastReason">Latest failure / status reason, when any.</param>
public sealed record CredentialProgress(
    bool Satisfied = false,
    int Attempts = 0,
    string? LastReason = null,
    string? SubjectId = null,
    DateTimeOffset? VerifiedAt = null);

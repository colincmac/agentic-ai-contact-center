using System.Collections.Immutable;
using System.Text;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Calling;

namespace Agents.AI.ContactCenter.State.Projections;

/// <summary>Audit outcome for a single authenticator attempt, in a serialization-friendly form.</summary>
public enum AuthStepOutcome
{
    /// <summary>The authenticator established or elevated the caller's identity.</summary>
    Authenticated,

    /// <summary>The authenticator attempted verification and the caller failed.</summary>
    Failed,

    /// <summary>The authenticator requires caller interaction (OTP, biometric phrase, …).</summary>
    Challenged
}

/// <summary>One authenticator attempt recorded on the caller's audit trail.</summary>
public sealed record AuthStep(string AuthenticatorName, AuthStepOutcome Outcome, DateTimeOffset At);

/// <summary>An open challenge awaiting caller interaction.</summary>
public sealed record PendingChallenge(AuthenticationMethod Method, string Prompt, string ChallengeId, DateTimeOffset ExpiresAt);

/// <summary>
/// Immutable projection of the caller's evolving identity and authentication audit trail. Replaces the
/// lock-guarded <c>CallerAuthenticationState</c>: every field here is set by folding events, never by
/// direct mutation, so the slice is a pure function of the <see cref="StrategyEvent"/> stream.
/// </summary>
public sealed record AuthSnapshot
{
    /// <summary>The anonymous starting point for every call.</summary>
    public static readonly AuthSnapshot Empty = new();

    /// <summary>Stable back-office user id, or <c>"anonymous"</c> when unverified.</summary>
    public string UserId { get; init; } = "anonymous";

    /// <summary>Human-readable name used to address the caller.</summary>
    public string DisplayName { get; init; } = "Anonymous Caller";

    /// <summary>E.164 phone number, when known.</summary>
    public string? PhoneNumber { get; init; }

    /// <summary>Email, when known.</summary>
    public string? Email { get; init; }

    /// <summary>
    /// The strongest full caller identity established so far. Lossless (claims, object id, authenticated-by,
    /// timestamps) so authenticators and tools that read <c>AuthenticationContext.CurrentIdentity</c> keep
    /// working after the lock-guarded <c>CallerAuthenticationState</c> is removed.
    /// </summary>
    public CallerIdentity Identity { get; init; } = CallerIdentity.Anonymous;

    /// <summary>Strongest verification level established so far.</summary>
    public CallerVerificationLevel Level { get; init; } = CallerVerificationLevel.None;

    /// <summary>Authentication methods that contributed to the current identity.</summary>
    public ImmutableArray<string> Methods { get; init; } = [];

    /// <summary>Ordered audit trail of authenticator attempts.</summary>
    public ImmutableArray<AuthStep> Steps { get; init; } = [];

    /// <summary>Open challenge waiting on the caller, or <see langword="null"/> when none is in flight.</summary>
    public PendingChallenge? PendingChallenge { get; init; }

    /// <summary>
    /// Per-authenticator inline-authentication progress keyed by authenticator name (case-insensitive).
    /// Lets a resumed tier pick up exactly where the credential sequence left off after a mid-call swap,
    /// and bounds retries per step. Replaces the lock-guarded <c>CallerAuthenticationState.Requirements</c>.
    /// </summary>
    public ImmutableDictionary<string, CredentialProgress> Requirements { get; init; } =
        ImmutableDictionary.Create<string, CredentialProgress>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Current progress for <paramref name="authenticatorName"/>; a fresh, unsatisfied value when none recorded yet.</summary>
    public CredentialProgress GetRequirement(string authenticatorName)
        => Requirements.TryGetValue(authenticatorName, out var p) ? p : new CredentialProgress();

    /// <summary>True once any authenticator has established an identity.</summary>
    public bool IsAuthenticated => Level != CallerVerificationLevel.None;
}

/// <summary>
/// Folds caller-authentication events (<see cref="StrategyEvent.CallerIdentified"/>,
/// <see cref="StrategyEvent.CallerAuthenticationFailed"/>,
/// <see cref="StrategyEvent.CallerAuthenticationChallenge"/>,
/// <see cref="StrategyEvent.CallerVerificationLevelChanged"/>) into an <see cref="AuthSnapshot"/>.
/// </summary>
public sealed class AuthStateProjection : CallStateProjection<AuthSnapshot>
{
    /// <summary>Stable slice id used as the persistence key.</summary>
    public const string Slice = "auth";

    public AuthStateProjection() : base(Slice, () => AuthSnapshot.Empty) { }

    protected override AuthSnapshot Apply(AuthSnapshot current, StrategyEvent strategyEvent) => strategyEvent switch
    {
        StrategyEvent.CallerIdentified e => Identify(current, e),
        StrategyEvent.CallerAuthenticationFailed e => current with
        {
            Steps = current.Steps.Add(new AuthStep(e.AuthenticatorName, AuthStepOutcome.Failed, e.At)),
        },
        StrategyEvent.CallerAuthenticationChallenge e => current with
        {
            PendingChallenge = new PendingChallenge(e.Challenge.Method, e.Challenge.Prompt, e.Challenge.ChallengeId, e.Challenge.ExpiresAt),
            Steps = current.Steps.Add(new AuthStep(e.Challenge.Method.ToString(), AuthStepOutcome.Challenged, e.At)),
        },
        StrategyEvent.CallerVerificationLevelChanged e when e.To > current.Level => current with { Level = e.To },
        StrategyEvent.CredentialAttempted e => RecordRequirement(current, e),
        _ => current,
    };

    private static AuthSnapshot RecordRequirement(AuthSnapshot current, StrategyEvent.CredentialAttempted e)
    {
        var prior = current.Requirements.TryGetValue(e.AuthenticatorName, out var p) ? p : new CredentialProgress();
        var updated = prior with
        {
            Satisfied = prior.Satisfied || e.Satisfied,
            Attempts = prior.Attempts + 1,
            LastReason = e.Reason,
        };
        return current with { Requirements = current.Requirements.SetItem(e.AuthenticatorName, updated) };
    }

    private static AuthSnapshot Identify(AuthSnapshot current, StrategyEvent.CallerIdentified e)
    {
        var identity = e.Identity;

        // Only adopt the incoming identity's profile fields when it is at least as strong as what we
        // already hold; a weaker authenticator must not overwrite a stronger established identity.
        var adopt = identity.VerificationLevel >= current.Level;

        var methods = current.Methods;
        foreach (var method in identity.AuthenticationMethods)
        {
            if (!methods.Contains(method, StringComparer.OrdinalIgnoreCase))
            {
                methods = methods.Add(method);
            }
        }
        if (!methods.Contains(e.AuthenticatorName, StringComparer.OrdinalIgnoreCase))
        {
            methods = methods.Add(e.AuthenticatorName);
        }

        return current with
        {
            Identity = adopt ? identity : current.Identity,
            UserId = adopt ? identity.UserId : current.UserId,
            DisplayName = adopt ? identity.DisplayName : current.DisplayName,
            PhoneNumber = adopt ? identity.PhoneNumber ?? current.PhoneNumber : current.PhoneNumber,
            Email = adopt ? identity.Email ?? current.Email : current.Email,
            Level = identity.VerificationLevel > current.Level ? identity.VerificationLevel : current.Level,
            Methods = methods,
            PendingChallenge = null,
            Steps = current.Steps.Add(new AuthStep(e.AuthenticatorName, AuthStepOutcome.Authenticated, e.At)),
        };
    }

    protected override string? Render(AuthSnapshot state)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Caller Authentication State");
        sb.AppendLine("### Verified Information");
        sb.AppendLine($"- Verification level: {state.Level}");
        if (!string.IsNullOrWhiteSpace(state.DisplayName))
        {
            sb.AppendLine($"- Name: {state.DisplayName}");
        }
        if (!string.IsNullOrWhiteSpace(state.PhoneNumber))
        {
            sb.AppendLine($"- Phone: {state.PhoneNumber}");
        }

        if (state.Steps.Length > 0)
        {
            sb.AppendLine("### Authentication Steps Status (in the form <AuthenticatorName> : <Outcome>)");
            foreach (var step in state.Steps)
            {
                sb.AppendLine($"- {step.AuthenticatorName} : {step.Outcome}");
            }
        }

        return sb.ToString();
    }
}

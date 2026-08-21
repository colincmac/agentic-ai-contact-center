namespace Agents.AI.ContactCenter.IvrWorkflow.Blueprint;

/// <summary>
/// Authored, ordered sequence of authentication steps a caller must satisfy on entry to a
/// stage before its business prompt / tools are surfaced. Compiled onto the stage and driven
/// inline by the executor — the same plan runs under DTMF and realtime because each strategy
/// renders the per-step <c>CredentialRequest</c> with its own transport.
/// </summary>
public sealed class AuthenticationPlanBlueprint
{
    /// <summary>Ordered step groups. The executor advances through them top-to-bottom.</summary>
    public required IReadOnlyList<AuthStepGroup> Steps { get; init; }

    /// <summary>Maximum credential attempts allowed per step before the plan fails. Default 3.</summary>
    public int MaxAttemptsPerStep { get; init; } = 3;
}

/// <summary>
/// One step in an <see cref="AuthenticationPlanBlueprint"/>. A single authenticator name is a
/// required step; multiple names are an <c>anyOf</c> — satisfying any one completes the step.
/// </summary>
/// <param name="AuthenticatorNames">Authenticator names (matched case-insensitively to <c>ICallerAuthenticator.Name</c>).</param>
public sealed record AuthStepGroup(IReadOnlyList<string> AuthenticatorNames)
{
    /// <summary>True when the caller may choose between several authenticators to satisfy this step.</summary>
    public bool IsAnyOf => AuthenticatorNames.Count > 1;
}

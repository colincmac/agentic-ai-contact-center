using System.ComponentModel;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.Extensions.AITools;
using Agents.AI.Monitoring.Correlation;
using Agents.AI.Monitoring.Dynamics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.AITools;

/// <summary>
/// AI-callable call-control verbs (hang up, transfer) bound to the live
/// <see cref="ICallSession"/>. The agent reaches the active session through
/// the scoped <see cref="ICallSession"/>, so this class must be
/// resolved from the per-call DI scope created by <c>CallSessionFactory</c>.
/// Register the tool surface with
/// <see cref="DependencyInjection.CallSessionContainerExtensions.AddCallControlTools(DependencyInjection.CallSessionContainerBuilder, string)"/>.
/// </summary>
public sealed class CallControlTools: IAIToolCollection
{
    private readonly ILogger<CallControlTools> _logger;
    private readonly ICallSessionAccessor _callSessionAccessor;
    private readonly ICallCorrelationAccessor? _correlation;
    private readonly ICallCorrelationStore? _correlationStore;

    public CallControlTools(
        ICallSessionAccessor callSessionAccessor,
        ICallCorrelationAccessor? correlation = null,
        ICallCorrelationStore? correlationStore = null,
        ILogger<CallControlTools>? logger = null)
    {
        _callSessionAccessor = callSessionAccessor;
        _correlation = correlation;
        _correlationStore = correlationStore;
        _logger = logger ?? NullLogger<CallControlTools>.Instance;
    }

    /// <summary>Stable tool name used in YAML workflows for the hang-up verb.</summary>
    public const string HangUpToolName = "hang_up_call";

    /// <summary>Stable tool name used in YAML workflows for the transfer verb.</summary>
    public const string TransferToolName = "transfer_call";

    [Description(
        "End the current phone call. Use this only when the conversation is complete, " +
        "the caller has confirmed they are done, or escalation is no longer possible. " +
        "After calling this, no further audio will be played to the caller.")]
    public async Task<CallControlResult> HangUpCallAsync(
        [Description("Brief human-readable reason for hanging up (e.g. 'caller satisfied', 'task complete').")]
        string reason,
        CancellationToken cancellationToken = default)
    {
        var session = _callSessionAccessor.Current;
        if (session is null)
        {
            _logger.LogWarning("HangUpCallAsync invoked but no active call session is bound to this scope");
            return new CallControlResult(false, "No active call session is bound to this agent.");
        }

        try
        {
            await session.HangUpAsync(hangUpForEveryone: true, reason: reason, cancellationToken).ConfigureAwait(false);
            return new CallControlResult(true, $"Call {session.CallId} hung up.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hang up failed for call {CallId}", session.CallId);
            return new CallControlResult(false, $"Hang up failed: {ex.Message}");
        }
    }

    [Description(
        "Transfer the current phone call to a human or another endpoint. " +
        "Use this when the caller explicitly asks for a person, when the request is " +
        "outside the agent's authorized scope, or when policy requires escalation.")]
    public async Task<CallControlResult> TransferCallAsync(
        [Description("The destination identifier. For 'phone' use E.164 (e.g. '+15551234567'). For 'teams' use the Microsoft Teams user ID. For 'consultative' use an ACS user ID.")]
        string targetIdentifier,
        [Description("Transfer kind: 'phone' (blind transfer to PSTN), 'teams' (blind transfer to Teams user), or 'consultative' (warm transfer to an ACS user).")]
        string transferKind = "phone",
        [Description("Optional reason for the transfer; recorded in telemetry and may be passed as transfer context.")]
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var session = _callSessionAccessor.Current;
        if (session is null)
        {
            _logger.LogWarning("TransferCallAsync invoked but no active call session is bound to this scope");
            return new CallControlResult(false, "No active call session is bound to this agent.");
        }

        if (string.IsNullOrWhiteSpace(targetIdentifier))
        {
            return new CallControlResult(false, "targetIdentifier is required.");
        }

        if (!TryParseTransferKind(transferKind, out var kind))
        {
            return new CallControlResult(false,
                $"Unknown transferKind '{transferKind}'. Use 'phone', 'teams', or 'consultative'.");
        }

        var customContext = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(reason))
        {
            customContext["reason"] = reason;
        }

        // Attach the canonical correlation ids (context_id, e2e_call_id, intent, language) so the
        // downstream Dynamics 365 workstream can hydrate a screen-pop and the call stays correlated.
        var correlation = _correlation?.Current;
        if (correlation is null && _correlationStore is not null)
        {
            correlation = await _correlationStore
                .GetByCallKeyAsync(session.CallId, cancellationToken).ConfigureAwait(false);
        }

        if (correlation is not null)
        {
            foreach (var pair in D365ContextVariableMap.FromCorrelation(correlation))
            {
                customContext[pair.Key] = pair.Value;
            }
        }

        var request = new TransferRequest(
            targetIdentifier,
            kind,
            customContext.Count > 0 ? customContext : null);

        try
        {
            await session.TransferAsync(request, cancellationToken).ConfigureAwait(false);
            return new CallControlResult(true,
                $"Transfer initiated for call {session.CallId} to {targetIdentifier} ({kind}).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transfer failed for call {CallId}", session.CallId);
            return new CallControlResult(false, $"Transfer failed: {ex.Message}");
        }
    }

    /// <summary>Build the <see cref="AIFunction"/> for <see cref="HangUpCallAsync"/> bound to <paramref name="instance"/>.</summary>
    public static AIFunction BuildHangUpTool(CallControlTools instance) =>
        AIFunctionFactory.Create(instance.HangUpCallAsync, name: HangUpToolName);

    /// <summary>Build the <see cref="AIFunction"/> for <see cref="TransferCallAsync"/> bound to <paramref name="instance"/>.</summary>
    public static AIFunction BuildTransferTool(CallControlTools instance) =>
        AIFunctionFactory.Create(instance.TransferCallAsync, name: TransferToolName);

    private static bool TryParseTransferKind(string raw, out TransferKind kind)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "phone":
            case "pstn":
            case "blind_phone":
            case "blindtophonenumber":
                kind = TransferKind.BlindToPhoneNumber;
                return true;
            case "teams":
            case "blind_teams":
            case "blindtoteamsuser":
                kind = TransferKind.BlindToTeamsUser;
                return true;
            case "consultative":
            case "warm":
                kind = TransferKind.Consultative;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    public IEnumerable<AITool> AsAITools()
    {
        yield return AIFunctionFactory.Create(HangUpCallAsync, name: HangUpToolName);
        yield return AIFunctionFactory.Create(TransferCallAsync, name: TransferToolName);
    }
}

/// <summary>Result envelope returned by <see cref="CallControlTools"/> verbs.</summary>
public sealed record CallControlResult(bool Success, string Message);

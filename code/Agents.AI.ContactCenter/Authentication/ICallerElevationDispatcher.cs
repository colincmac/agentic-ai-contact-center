using System.Threading.Channels;
using Agents.AI.ContactCenter.Calling;

namespace Agents.AI.ContactCenter.Authentication;

/// <summary>
/// Single entry point for mid-call elevation requests. Tools (PIN collectors, OTP
/// validators, biometric prompts, …) call this instead of mutating
/// <see cref="CallerAuthenticationState"/> directly. The dispatcher runs the named
/// <see cref="ICallerAuthenticator"/> through the orchestrator, updates the per-call
/// state, and (when a <see cref="ChannelWriter{T}"/> is supplied) emits
/// <see cref="StrategyEvent.CallerVerificationLevelChanged"/> / <see cref="StrategyEvent.CallerIdentified"/>
/// / <see cref="StrategyEvent.CallerAuthenticationFailed"/> / <see cref="StrategyEvent.CallerAuthenticationChallenge"/>
/// centrally.
/// </summary>
public interface ICallerElevationDispatcher
{
    /// <summary>
    /// Run the authenticator with name <paramref name="authenticatorName"/> against the
    /// current caller state.
    /// </summary>
    /// <param name="authenticatorName">
    /// Matches <see cref="ICallerAuthenticator.Name"/> on a registered authenticator (case-insensitive).
    /// </param>
    /// <param name="callId">Active call identifier used for audit + event timestamps.</param>
    /// <param name="callerMetadata">
    /// Optional caller-edge metadata. Authenticators that don't need it (e.g. PIN) can pass
    /// <see langword="null"/>. ANI-style authenticators won't run without it.
    /// </param>
    /// <param name="events">
    /// Optional per-call strategy event writer. When supplied, the dispatcher emits
    /// caller authentication strategy events. When <see langword="null"/>
    /// the dispatcher relies on <see cref="CallerAuthenticationState.IdentityChanged"/> for
    /// downstream observers.
    /// </param>
    /// <param name="tags">Optional tags attached to the <see cref="AuthenticationContext"/>.</param>
    Task<AuthenticationRunResult> DispatchAsync(
        string authenticatorName,
        string callId,
        CallEdgeMetadata? callerMetadata = null,
        ChannelWriter<StrategyEvent>? events = null,
        IReadOnlyDictionary<string, string>? tags = null,
        CancellationToken cancellationToken = default);
}

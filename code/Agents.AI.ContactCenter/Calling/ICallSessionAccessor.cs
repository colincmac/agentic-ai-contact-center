namespace Agents.AI.ContactCenter.Calling;

/// <summary>
/// Scoped accessor that exposes the <see cref="ICallSession"/> currently being
/// served by this DI scope. Set by <c>CallSessionFactory</c> when it builds the
/// session, then resolved by AI tool collections (e.g. <c>CallControlTools</c>)
/// so AI agents can hang up / transfer the live call
/// </summary>
/// <remarks>
/// AsyncLocal is not used for various reasons:
/// - The session's pumps are spawned by Task.Run inside StartAsync, which captures ExecutionContext at spawn time. Tool invocations come through the UseFunctionInvocation pipeline and realtime callbacks — flows that may not descend from the exact point the AsyncLocal was set.
/// - One (Kubernetes) pod multiplexes many concurrent calls. If a context is ever reused or set on the wrong flow, you don't get null — you get the wrong call's session (silent cross-talk), which is far worse to debug than a missing binding.
/// - The DI scope makes "this scope == this call" structural and explicit, while AsyncLocal makes it ambient and implicit. With a fragmented execution flow (pumps + function-invocation + webhook dispatch), explicit wins.
/// </remarks>
public interface ICallSessionAccessor
{
    /// <summary>The session bound to this scope, or <see langword="null"/> if none was set.</summary>
    ICallSession? Current { get; }

    void Set(ICallSession session);
}

/// <summary>
/// Default in-memory implementation. Single-assignment: a scope serves exactly
/// one session for its lifetime, which matches how <c>CallSessionFactory</c>
/// creates one DI scope per call.
/// </summary>
public sealed class CallSessionAccessor : ICallSessionAccessor
{
    private ICallSession? _current;

    public ICallSession? Current => _current;

    public CallSessionAccessor(ICallContextAccessor callContextAccessor, ICallSessionRegistry registry)
    {
        if (callContextAccessor.Current?.CallId is { } callId)
        {
            _current = registry.TryGet(callId);
        }
    }
    public void Set(ICallSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (Interlocked.CompareExchange(ref _current, session, null) is not null)
        {
            throw new InvalidOperationException(
                $"{nameof(CallSessionAccessor)} already bound to call '{_current!.CallId}'.");
        }
    }
}

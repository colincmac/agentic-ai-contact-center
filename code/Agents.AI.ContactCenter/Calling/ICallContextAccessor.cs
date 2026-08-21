namespace Agents.AI.ContactCenter.Calling;

/// <summary>
/// Provides ambient access to the <see cref="IncomingCallContext"/> for the call currently being handled,
/// so the active call's facts can flow through a request/handler pipeline without being threaded through
/// every method signature.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Must be registered with a scoped lifetime — one DI scope per call.</strong> The context is
/// intended to be bound exactly once per scope via <see cref="Set(IncomingCallContext)"/>.
/// </para>
/// <para>
/// If this is mistakenly registered as a singleton (or otherwise shared across calls), the first call
/// binds the backing field and every subsequent concurrent call throws <see cref="InvalidOperationException"/>
/// from <see cref="Set(IncomingCallContext)"/>, while <see cref="Current"/> returns the wrong call's context.
/// </para>
/// </remarks>
public interface ICallContextAccessor
{
    /// <summary>
    /// Gets the <see cref="IncomingCallContext"/> bound to the current scope, or <see langword="null"/>
    /// if <see cref="Set(IncomingCallContext)"/> has not yet been called for this scope.
    /// </summary>
    IncomingCallContext? Current { get; }

    /// <summary>
    /// Binds the <see cref="IncomingCallContext"/> for the current scope. May be called only once per scope.
    /// </summary>
    /// <param name="context">The incoming call context to associate with the current scope.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A context has already been bound for the current scope.</exception>
    void Set(IncomingCallContext context);
}

/// <summary>
/// Default <see cref="ICallContextAccessor"/> implementation that holds a single
/// <see cref="IncomingCallContext"/> for the lifetime of its DI scope.
/// </summary>
/// <remarks>
/// Register this with a scoped lifetime so each call receives its own instance. The context is bound
/// atomically with <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>, guaranteeing exactly-once
/// assignment even under concurrent access within the same scope.
/// </remarks>
public sealed class CallContextAccessor : ICallContextAccessor
{
    private IncomingCallContext? _current;

    /// <inheritdoc />
    public IncomingCallContext? Current => _current;

    /// <inheritdoc />
    public void Set(IncomingCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Interlocked.CompareExchange(ref _current, context, null) is not null)
        {
            throw new InvalidOperationException(
                $"{nameof(CallContextAccessor)} already bound to call '{_current!.CallId}'.");
        }
    }
}

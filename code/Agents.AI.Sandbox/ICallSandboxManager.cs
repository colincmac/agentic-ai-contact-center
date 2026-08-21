namespace Agents.AI.Sandbox;

/// <summary>
/// Per-call owner of a single <see cref="ICallSandbox"/>. Registered as a scoped service in
/// the per-call DI scope created by <c>CallSessionFactory</c>, so the same sandbox is shared
/// by every tool invocation on a call, survives composite-tier swaps (RealtimeVoice →
/// IntentNlu → DtmfOnly), and is torn down deterministically when the call scope is disposed.
/// </summary>
public interface ICallSandboxManager : IAsyncDisposable
{
    /// <summary>Whether sandboxed execution is configured and enabled for this host.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Returns the call's sandbox, provisioning it lazily on first use. Throws
    /// <see cref="InvalidOperationException"/> when sandboxing is not enabled — callers that
    /// can degrade gracefully should check <see cref="IsEnabled"/> first.
    /// </summary>
    ValueTask<ICallSandbox> EnsureAsync(CancellationToken cancellationToken = default);
}

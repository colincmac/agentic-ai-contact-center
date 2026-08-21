using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Shared.Diagnostics;

namespace Agents.AI.Realtime;

public abstract class DelegatingRealtimeAIAgent(RealtimeAIAgent innerAgent) : AIAgent, IRealtimeAgent
{
    /// <summary>
    /// Gets the inner agent instance that receives delegated operations.
    /// </summary>
    /// <value>
    /// The underlying <see cref="RealtimeAIAgent"/> instance that handles core agent operations.
    /// </value>
    /// <remarks>
    /// Derived classes can use this property to access the inner agent for custom delegation scenarios
    /// or to forward operations with additional processing.
    /// </remarks>
    protected RealtimeAIAgent InnerAgent { get; } = innerAgent ?? throw new ArgumentNullException(nameof(innerAgent));

    /// <inheritdoc />
    protected override string? IdCore => InnerAgent.Id;

    /// <inheritdoc />
    public override string? Name => InnerAgent.Name;

    /// <inheritdoc />
    public override string? Description => InnerAgent.Description;

    /// <inheritdoc />
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        _ = Throw.IfNull(serviceType);

        // If the key is non-null, we don't know what it means so pass through to the inner service.
        return
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this :
            InnerAgent.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc />
    public virtual ValueTask<RealtimeAIAgentSession> CreateSessionAsync(
        RealtimeSessionOptions? sessionOptions = null,
        CancellationToken cancellationToken = default)
    {
        return InnerAgent.CreateSessionAsync(sessionOptions, cancellationToken);
    }

    /// <inheritdoc />
    public virtual Task SendAsync(
        RealtimeAIAgentSession session,
        RealtimeClientMessage message,
        CancellationToken cancellationToken = default)
    {

        return InnerAgent.SendAsync(session, message, cancellationToken);
    }

    /// <inheritdoc />
    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) => InnerAgent.CreateSessionAsync(cancellationToken);

    /// <inheritdoc />
    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
        => InnerAgent.SerializeSessionAsync(session, jsonSerializerOptions, cancellationToken);

    /// <inheritdoc />
    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
        => InnerAgent.DeserializeSessionAsync(serializedState, jsonSerializerOptions, cancellationToken);

    /// <inheritdoc />
    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
        => InnerAgent.RunAsync(messages, session, options, cancellationToken);

    /// <inheritdoc />
    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
        => InnerAgent.RunStreamingAsync(messages, session, options, cancellationToken);
}



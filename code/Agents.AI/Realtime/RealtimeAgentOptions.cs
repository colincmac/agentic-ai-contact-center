using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agents.AI.Realtime;

/// <summary>
/// Configuration options for the <see cref="RealtimeAIAgent"/>.
/// </summary>
public sealed class RealtimeAgentOptions
{
    /// <summary>
    /// Gets or sets the unique identifier for the agent.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the human-readable name for the agent.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets a description of the agent's purpose and capabilities.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the default session options to use when creating new realtime sessions.
    /// </summary>
    public RealtimeSessionOptions? SessionOptions { get; set; }

    /// <summary>
    /// Gets or sets the <see cref="ChatHistoryProvider"/> instance to use for providing chat history for this agent.
    /// </summary>
    public ChatHistoryProvider? ChatHistoryProvider { get; set; }

    /// <summary>
    /// Gets or sets the list of <see cref="AIContextProvider"/> instances to use for providing additional context for each agent run.
    /// </summary>
    public IEnumerable<AIContextProvider>? AIContextProviders { get; set; }

    /// <summary>
    /// Creates a deep copy of this options instance.
    /// </summary>
    /// <returns>A new <see cref="RealtimeAgentOptions"/> with the same values.</returns>
    public RealtimeAgentOptions Clone() => new()
    {
        Id = Id,
        Name = Name,
        Description = Description,
        SessionOptions = SessionOptions, // shallow copy; consider deep clone if mutable
        ChatHistoryProvider = ChatHistoryProvider,
        AIContextProviders = AIContextProviders
    };
}

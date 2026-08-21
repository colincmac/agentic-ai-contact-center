using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Agents.AI;
public class ConfigureRunOptionsAgent : DelegatingAIAgent
{
    private readonly Action<AgentRunOptions?> _configureRunOptions;

    public ConfigureRunOptionsAgent(AIAgent innerAgent, Action<AgentRunOptions?> configureRunOptions) : base(innerAgent)
    {
        _configureRunOptions = configureRunOptions;
    }

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
        => this.InnerAgent.RunAsync(messages, session, ConfigureRunOptions(options), cancellationToken);

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
            => this.InnerAgent.RunStreamingAsync(messages, session, ConfigureRunOptions(options), cancellationToken);

    /// <summary>Creates and configures the <see cref="AgentRunOptions"/> to pass along to the inner client.</summary>
    private AgentRunOptions ConfigureRunOptions(AgentRunOptions? options)
    {
        options = options ?? new();
  
        _configureRunOptions(options);

        return options;
    }
}

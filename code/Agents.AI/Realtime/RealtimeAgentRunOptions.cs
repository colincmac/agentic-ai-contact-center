using System;
using System.Collections.Generic;
using System.Text;
using Extensions.AI.RealtimeVoice;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agents.AI.Realtime;

public class RealtimeAgentRunOptions : AgentRunOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RealtimeAgentRunOptions"/> class.
    /// </summary>
    /// <param name="chatOptions">Optional chat options to pass to the agent's invocation.</param>
    public RealtimeAgentRunOptions(RealtimeSessionOptions? sessionOptions = null)
    {
        this.SessionOptions = sessionOptions;
    }

    /// <summary>Gets or sets optional response options to pass to the agent's invocation.</summary>
    public RealtimeSessionOptions? SessionOptions { get; set; }


    /// <summary>
    /// Gets or sets a factory function that can replace (typically via decorators) the chat client on a per-request basis.
    /// </summary>
    /// <value>
    /// A function that receives the agent's configured chat client and returns a potentially modified or entirely
    /// different chat client to use for this specific invocation. If <see langword="null"/>, the agent's default
    /// chat client will be used without modification.
    /// </value>
    public Func<IRealtimeClient, IRealtimeClient>? RealtimeClientFactory { get; set; }
    public Func<IRealtimeClientSession, IRealtimeClientSession>? RealtimeClientSessionFactory { get; set; }
}

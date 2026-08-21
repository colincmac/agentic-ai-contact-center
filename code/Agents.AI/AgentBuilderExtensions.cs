using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agents.AI;

public delegate ValueTask<object?> FunctionCoreDelegate(
    FunctionInvocationContext context,
    CancellationToken cancellationToken);

public delegate ValueTask<object?> FunctionMiddlewareDelegate(
    AIAgent agent,
    FunctionInvocationContext context,
    FunctionCoreDelegate next,
    CancellationToken cancellationToken);

public static class AgentBuilderExtensions
{
    public static AIAgentBuilder Use(this AIAgentBuilder builder, Action<AgentRunOptions?> configureRunOptions)
    {
        return builder.Use((innerAgent, _) =>
        {
            return new ConfigureRunOptionsAgent(innerAgent, configureRunOptions);
        }); 
    }

}


using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Extensions.AI.Realtime;

namespace Agents.AI.Realtime;

public class FunctionInvokingRealtimeAIAgent: DelegatingRealtimeAIAgent
{

    private readonly Func<AIAgent, FunctionInvocationContext, Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>, CancellationToken, ValueTask<object?>> _delegateFunc;
    // Decorate options to add the middleware function

    internal FunctionInvokingRealtimeAIAgent(RealtimeAIAgent innerAgent, Func<AIAgent, FunctionInvocationContext, Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>, CancellationToken, ValueTask<object?>> delegateFunc) : base(innerAgent)
    {
        this._delegateFunc = delegateFunc;
    }

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    => this.InnerAgent.RunStreamingAsync(messages, session, AgentRunOptionsWithFunctionMiddleware(options), cancellationToken);


    private AgentRunOptions? AgentRunOptionsWithFunctionMiddleware(AgentRunOptions? options)
    {
        if (options is null || options.GetType() == typeof(AgentRunOptions))
        {
            options = new RealtimeAgentRunOptions()
            {
                ResponseFormat = options?.ResponseFormat,
                AllowBackgroundResponses = options?.AllowBackgroundResponses,
                ContinuationToken = options?.ContinuationToken,
                AdditionalProperties = options?.AdditionalProperties,
            };
        }

        if (options is not RealtimeAgentRunOptions aco)
        {
            throw new NotSupportedException($"Function Invocation Middleware is only supported without options or with {nameof(RealtimeAgentRunOptions)}.");
        }

        var originalFactory = aco.RealtimeClientFactory;
        aco.RealtimeClientFactory = realtimeClient =>
        {
            var builder = realtimeClient.AsBuilder();

            if (originalFactory is not null)
            {
                builder.Use(originalFactory);
            }
            // RealtimeSessionOptions properties are init only, so we need to create a new instance with the modified tools list instead of modifying the existing one
            return builder.ConfigureOptions(options
                =>
            {
                var tools = options.Tools?.Select(tool => tool is AIFunction aiFunction
                        ? new MiddlewareEnabledFunction(this.InnerAgent, aiFunction, this._delegateFunc)
                        : tool)
                    .ToList();
                return options.With(tools: tools);
            }).Build();
        };

        return options;
    }

    private sealed class MiddlewareEnabledFunction(AIAgent innerAgent, AIFunction innerFunction, Func<AIAgent, FunctionInvocationContext, Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>, CancellationToken, ValueTask<object?>> next) : DelegatingAIFunction(innerFunction)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var context = FunctionInvokingRealtimeClient.CurrentContext
                ?? new FunctionInvocationContext() // When there is no ambient context, create a new one to hold the arguments
                {
                    Arguments = arguments,
                    Function = this.InnerFunction,
                    CallContent = new(string.Empty, this.InnerFunction.Name, new Dictionary<string, object?>(arguments)),
                };

            return await next(innerAgent, context, CoreLogicAsync, cancellationToken).ConfigureAwait(false);

            ValueTask<object?> CoreLogicAsync(FunctionInvocationContext ctx, CancellationToken cancellationToken)
                => base.InvokeCoreAsync(ctx.Arguments, cancellationToken);
        }
    }
}

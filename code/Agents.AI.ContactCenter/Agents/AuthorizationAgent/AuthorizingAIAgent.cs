using System.Runtime.CompilerServices;
using Agents.AI.ContactCenter.AITools;
using Agents.AI.Sandbox;
using Agents.AI.Realtime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Agents.AuthorizationAgent;

public class AuthorizingAIAgent : DelegatingRealtimeAIAgent
{
    private readonly IServiceProvider? _scopedServices;
    private readonly AgentFunctionInvocationMiddleware _delegateFunc;

    public AuthorizingAIAgent(
        RealtimeAIAgent innerAgent,
        AgentFunctionInvocationMiddleware? delegateFunc = null,
        IServiceProvider? serviceProvider = null)
        : base(innerAgent)
    {
        _scopedServices = serviceProvider;
        _delegateFunc = delegateFunc ?? DefaultFunctionMiddleware;
    }

    public override async Task SendAsync(RealtimeAIAgentSession session, RealtimeClientMessage message, CancellationToken cancellationToken = default)
    {
        if (message is SessionUpdateRealtimeClientMessage updateSession)
        {
            updateSession.Options = RealtimeSessionOptionsWithFunctionMiddleware(updateSession.Options) ?? updateSession.Options;
            await base.SendAsync(session, updateSession, cancellationToken).ConfigureAwait(false);
            return;
        }

        await base.SendAsync(session, message, cancellationToken).ConfigureAwait(false);
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in InnerAgent.RunStreamingAsync(messages, session, AgentRunOptionsWithFunctionMiddleware(options), cancellationToken))
        {
            yield return update;
        }
    }

    public override async ValueTask<RealtimeAIAgentSession> CreateSessionAsync(RealtimeSessionOptions? sessionOptions = null, CancellationToken cancellationToken = default)
    {
        var options = RealtimeSessionOptionsWithFunctionMiddleware(sessionOptions);
        return await base.CreateSessionAsync(options, cancellationToken);
    }

    private static ValueTask<object?> DefaultFunctionMiddleware(AIAgent agent, AIFunctionArguments arguments, AIFunction function, Func<AIFunctionArguments, CancellationToken, ValueTask<object?>> next, CancellationToken ct)
    {
        return next(arguments, ct); // Pass through
    }

    private IReadOnlyList<AITool> ApplyToolMiddleware(IEnumerable<AITool> tools)
    {
        var wrappedTools = tools.Select(tool =>
        {
            if (tool is not AIFunction aiFunction || tool is AuthorizingAIFunction)
            {
                return tool;
            }

            // Redirect [SandboxedTool]-marked tools into the per-call microVM. The sandbox
            // decorator becomes the inner function, so AuthorizingAIFunction's auth/approval
            // gating still runs in-process before execution leaves the pod (see ADR-0013).
            AIFunction inner = SandboxedAIFunction.TryWrap(aiFunction, _scopedServices) ?? aiFunction;
            return new AuthorizingAIFunction(InnerAgent, inner, _delegateFunc, _scopedServices);
        }).ToList();
        return wrappedTools;
    }

    private RealtimeSessionOptions? RealtimeSessionOptionsWithFunctionMiddleware(RealtimeSessionOptions? options)
    {
        if (options?.Tools is null) return options;
        return options.With(tools: ApplyToolMiddleware(options.Tools));
    }

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

        if (options is not RealtimeAgentRunOptions realtimeRunOptions)
        {
            throw new NotSupportedException($"Function Invocation Middleware is only supported without options or with {nameof(RealtimeAgentRunOptions)}.");
        }

        realtimeRunOptions.SessionOptions = RealtimeSessionOptionsWithFunctionMiddleware(realtimeRunOptions.SessionOptions);
        return realtimeRunOptions;
    }




    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(AIAgent) ? this :
        serviceKey == null ? _scopedServices?.GetService(serviceType) : _scopedServices?.GetKeyedService(serviceType, serviceKey) ??
        base.GetService(serviceType, serviceKey);
}

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Agents.AI.Extensions.AITools;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
using Agents.AI.Extensions.SessionManagement;
using Agents.AI.Extensions.ToolApproval;
using Agents.AI.Realtime;
using Extensions.AI.Realtime;
using Extensions.AI.RealtimeVoice;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agents.AI.Extensions.RealtimeAgentHelpers;

public class AuthorizingRealtimeAIAgent : DelegatingRealtimeAIAgent
{
    private readonly IServiceProvider? _scopedServices;
    private readonly AgentFunctionInvocationMiddleware _delegateFunc;
    private readonly IAgentSessionRegistry _sessionRegistry;
    private readonly List<AITool> _additionalTools;

    // gpt-realtime limit
    private const int REALTIME_MAX_SESSION_TOKENS = 32000;
    private readonly int _maxSessionTokens;

    public AuthorizingRealtimeAIAgent(
        RealtimeAIAgent innerAgent,
        IAgentSessionRegistry sessionRegistry,
        AgentFunctionInvocationMiddleware? delegateFunc = null,
        IEnumerable<IAIToolCollection>? aIToolCollections = null,
        IServiceProvider? serviceProvider = null,
        int maxSessionTokenCount = REALTIME_MAX_SESSION_TOKENS) : base(innerAgent)
    {
        _sessionRegistry = sessionRegistry;
        _scopedServices = serviceProvider;
        _delegateFunc = delegateFunc ?? DefaultFunctionMiddleware;
        _additionalTools = aIToolCollections?.SelectMany(c => c.AsAITools()).ToList() ?? [];
        _maxSessionTokens = maxSessionTokenCount;
    }



    //public override async Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    //{
    //    List<AgentResponseUpdate> updates = [];

    //    await foreach (var update in RunStreamingAsync(messages, thread, options, cancellationToken))
    //    {
    //        updates.Add(update);

    //        if(update.Contents.Any(x => x is RealtimeResponseFinishedContent))
    //        {
    //            break;
    //        }
    //    }

    //    return updates.ToAgentResponse();

    //}

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        //var sessionId = conversationSessionThread.ActiveSessionId ?? Id
        //                ?? throw new InvalidOperationException("Session ID is required for callbacks");

        //Task AgentSessionCallback(ChatMessage msg, CancellationToken ct) => this.SendAsync(session, msg, ct);

        //await _sessionRegistry.RegisterSession(sessionId, AgentSessionCallback, cancellationToken).ConfigureAwait(false);

        try
        {
            await foreach (var update in InnerAgent.RunStreamingAsync(messages, session, AgentRunOptionsWithFunctionMiddleware(options), cancellationToken))
            {
                yield return update;
            }
        }
        finally
        {
            //await _sessionRegistry.UnregisterSession(sessionId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ValueTask<object?> DefaultFunctionMiddleware(AIAgent agent, AIFunctionArguments arguments, AIFunction function, Func<AIFunctionArguments, CancellationToken, ValueTask<object?>> next, CancellationToken ct)
    {
        return next(arguments, ct); // Pass through
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
                        ? new AuthorizingAgentFunction(this.InnerAgent, aiFunction, this._delegateFunc, this._scopedServices)
                        : tool)
                    .ToList();
                return new RealtimeSessionOptions()
                {
                    Tools = tools,
                    InputAudioFormat = options.InputAudioFormat,
                    Instructions = options.Instructions,
                    MaxOutputTokens = options.MaxOutputTokens,
                    Model = options.Model,
                    OutputAudioFormat = options.OutputAudioFormat,
                    OutputModalities = options.OutputModalities,
                    RawRepresentationFactory = options.RawRepresentationFactory,
                    SessionKind = options.SessionKind,
                    ToolMode = options.ToolMode,
                    TranscriptionOptions = options.TranscriptionOptions,
                    Voice = options.Voice,
                    VoiceActivityDetection = options.VoiceActivityDetection
                };
            }).Build();
        };

        return options;
    }




    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(AIAgent) ? this :
        serviceKey == null ? _scopedServices?.GetService(serviceType) : _scopedServices?.GetKeyedService(serviceType, serviceKey) ??
        base.GetService(serviceType, serviceKey);


    internal sealed class AuthorizingAgentFunction : DelegatingAIFunction
    {
        private readonly ILogger<AuthorizingAgentFunction>? _logger;
        private readonly AIAgent _agent;
        private readonly AgentFunctionInvocationMiddleware? _next;
        private readonly IServiceProvider? _scopedServices;
        // used to mark that this function follows the approval workflow
        //private readonly ApprovalRequiredAIFunction? _marker;

        public readonly List<IToolApprovalRequirement>? ToolRequirements;

        public AuthorizingAgentFunction(AIAgent agent, AIFunction innerFunction, AgentFunctionInvocationMiddleware next, IServiceProvider? scopedServices = null) : base(innerFunction)
        {
            _logger = GetService<ILoggerFactory>()?.CreateLogger<AuthorizingAgentFunction>();
            _agent = agent;
            ToolRequirements = innerFunction.UnderlyingMethod?.GetCustomAttributes(true)
                .Where(attr => attr is IToolApprovalRequirementData)
                .SelectMany(attr => ((IToolApprovalRequirementData)attr).GetRequirements())
                .ToList();
            _next = next;
            _scopedServices = scopedServices;
            //_marker = ToolRequirements is null or { Count: 0 } ? null : new ApprovalRequiredAIFunction(this);
        }

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            // The realtime client pipeline can set arguments.Services to the root
            // IServiceProvider (the singleton-registered conversation client owns
            // it), which throws when a tool tries to resolve scoped services like
            // ICallSessionAccessor. AuthorizingRealtimeAIAgent is itself resolved
            // inside the per-call DI scope, so _scopedServices points at that
            // scope — forward it so tools can reach scoped state.
            if (_scopedServices is not null)
            {
                arguments.Services = _scopedServices;
            }

            if (ToolRequirements is { Count: > 0 })
            {
                var toolApprovalHandlerProvider = arguments.Services?.GetService<IToolApprovalHandlerProvider>()
                    ?? GetService<IToolApprovalHandlerProvider>() as IToolApprovalHandlerProvider;

                if (toolApprovalHandlerProvider is not null)
                {
                    var invokingIdentity = arguments.Services?.GetService<ClaimsPrincipal>()
                        ?? GetService<ClaimsPrincipal>() as ClaimsPrincipal;
                    var approvalContext = new ToolApprovalContext(this, arguments, _agent, ToolRequirements, invokingIdentity);
                    var handlers = await toolApprovalHandlerProvider.GetHandlersAsync(approvalContext).ConfigureAwait(false);

                    foreach (var handler in handlers)
                    {
                        await handler.HandleAsync(approvalContext).ConfigureAwait(false);
                    }

                    if (!approvalContext.HasSucceeded)
                    {
                        var failure = new ToolApprovalFailure(
                            InnerFunction,
                            arguments,
                            [.. approvalContext.PendingRequirements],
                            [.. approvalContext.FailureResponses],
                            approvalContext.PendingRequirements is { Count: 0 });
                        _logger?.LogWarning(
                            "Function '{FunctionName}' invocation denied due to failed tool approval requirements.",
                            InnerFunction.Name);
                        return failure.FailureResponseMessage.Text;
                    }
                }
            }

            if (_next is not null)
            {
                return await _next.Invoke(_agent, arguments, InnerFunction, base.InvokeCoreAsync, cancellationToken);
            }
            else
            {
                return await base.InvokeCoreAsync(arguments, cancellationToken);
            }
        }

        //public override object? GetService(Type serviceType, object? serviceKey = null) =>
        //    //serviceType == typeof(ApprovalRequiredAIFunction) ? _marker :
        //    //serviceType == typeof(IEnumerable<IToolApprovalRequirement>) ? ToolRequirements :
        //    serviceType.IsInstanceOfType(typeof(AIAgent)) ? _agent :
        //    _agent.GetService(serviceType, serviceKey) ??
        //    base.GetService(serviceType, serviceKey);
    }

}

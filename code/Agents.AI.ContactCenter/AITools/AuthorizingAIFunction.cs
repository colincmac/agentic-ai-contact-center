using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text;
using Agents.AI.Extensions.ToolApproval;
using Agents.AI.Realtime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.AITools;

public sealed class AuthorizingAIFunction : DelegatingAIFunction
{
    private readonly ILogger<AuthorizingAIFunction>? _logger;
    private readonly AIAgent _agent;
    private readonly AgentFunctionInvocationMiddleware _next;
    private readonly IServiceProvider? _scopedServices;


    public readonly List<IToolApprovalRequirement>? ToolRequirements;

    public AuthorizingAIFunction(AIAgent agent, AIFunction innerFunction, AgentFunctionInvocationMiddleware next, IServiceProvider? scopedServices = null) : base(innerFunction)
    {
        _logger = GetService<ILoggerFactory>()?.CreateLogger<AuthorizingAIFunction>();
        _agent = agent;
        ToolRequirements = innerFunction.UnderlyingMethod?.GetCustomAttributes(true)
            .Where(attr => attr is IToolApprovalRequirementData)
            .SelectMany(attr => ((IToolApprovalRequirementData)attr).GetRequirements())
            .ToList();
        _next = next;
        _scopedServices = scopedServices;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (_scopedServices is not null)
        {
            arguments.Services = _scopedServices;
        }

        if (ToolRequirements is { Count: > 0 })
        {
            var toolApprovalHandlerProvider = arguments.Services?.GetService<IToolApprovalHandlerProvider>()
                ?? GetService<IToolApprovalHandlerProvider>();

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

        return await _next.Invoke(_agent, arguments, InnerFunction, base.InvokeCoreAsync, cancellationToken);
    }

    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(AIAgent) ? _agent :
        serviceKey == null ? _scopedServices?.GetService(serviceType) : _scopedServices?.GetKeyedService(serviceType, serviceKey) ??
        base.GetService(serviceType, serviceKey);
}

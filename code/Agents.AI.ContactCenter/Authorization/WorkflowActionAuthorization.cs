using System.Security.Claims;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using Agents.AI.ContactCenter.State;
using Agents.AI.ContactCenter.State.Projections;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Authorization;

public sealed record WorkflowActionRequirement(StageBlueprint Stage, CallerVerificationLevel MinimumLevel)
    : IAuthorizationRequirement;

public sealed class WorkflowActionAuthorizationHandler(CallStateProjector projector)
    : AuthorizationHandler<WorkflowActionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, WorkflowActionRequirement requirement)
    {
        var auth = projector.Get<AuthSnapshot>();
        if (projector.Get<IvrSnapshot>().CurrentStepId == requirement.Stage.Id
            && auth.Level >= requirement.MinimumLevel
            && (requirement.Stage.Authentication is not { } plan
                || plan.Steps.All(g => CallerEvidencePolicy.Satisfies(auth, g, plan.EvidenceMaxAge, DateTimeOffset.UtcNow))))
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}

/// <summary>Applied to the same tool binding for both scripted actions and realtime invocations.</summary>
internal sealed class AuthorizedWorkflowFunction(AIFunction inner, StageBlueprint stage, IServiceProvider? services)
    : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var scope = services ?? arguments.Services
            ?? throw new InvalidOperationException("Workflow actions require a call service scope.");
        var auth = scope.GetRequiredService<CallStateProjector>().Get<AuthSnapshot>();
        var level = UnderlyingMethod?.GetCustomAttributes(typeof(RequiresCallerVerificationAttribute), true)
            .OfType<RequiresCallerVerificationAttribute>().FirstOrDefault()?.MinimumLevel ?? CallerVerificationLevel.None;
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            auth.IsAuthenticated ? [new Claim(ClaimTypes.NameIdentifier, auth.UserId)] : [],
            auth.IsAuthenticated ? "CallerVerification" : null));
        var result = await scope.GetRequiredService<IAuthorizationService>().AuthorizeAsync(
            principal, InnerFunction, new WorkflowActionRequirement(stage, level)).ConfigureAwait(false);
        if (!result.Succeeded) { throw new UnauthorizedAccessException("The action is not authorized for the current caller and stage."); }
        arguments.Services = scope;
        return await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
    }
}

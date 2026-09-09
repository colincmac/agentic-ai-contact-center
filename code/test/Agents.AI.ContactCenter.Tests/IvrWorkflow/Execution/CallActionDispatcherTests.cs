using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Authorization;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Agents.AI.ContactCenter.State;
using Agents.AI.ContactCenter.State.Projections;
using Agents.AI.ContactCenter.State.Stores;
using Microsoft.AspNetCore.Authorization;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Execution;

public sealed class CallActionDispatcherTests
{
    [Fact]
    public async Task Action_IsAuthorizedAndUsesOneStableIdempotencyKey()
    {
        await using var projector = new CallStateProjector("call", [new AuthStateProjection(), new IvrStateProjection()],
            new InMemoryCallStateStore(), new());
        var action = new ActionStub();
        var services = new ServiceCollection().AddLogging().AddSingleton(projector)
            .AddSingleton<ICallWorkflowAction>(action).AddScoped<CallActionDispatcher>();
        services.AddAuthorizationCore();
        services.AddScoped<IAuthorizationHandler, WorkflowActionAuthorizationHandler>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var compiled = new WorkflowGraphCompiler().Compile(new()
        {
            Id = "flow", InitialStageId = "charge",
            Stages =
            [
                new() { Id = "charge", Action = "charge", OnActionSuccess = "done", OnActionFailure = "failed",
                    Authentication = new() { Steps = [new(["Pin"])], FailureStageId = "failed" } },
                new() { Id = "done", Terminal = true },
                new() { Id = "failed", Terminal = true },
            ],
        });
        var session = new CallWorkflowSession(compiled, scope.ServiceProvider, new CallerElevationDispatcher([], scope.ServiceProvider));
        var dispatcher = scope.ServiceProvider.GetRequiredService<CallActionDispatcher>();
        projector.Fold(new StrategyEvent.WorkflowStepEntered("charge", DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => dispatcher.ExecuteAsync(session, "charge", TestContext.Current.CancellationToken));
        Assert.Equal(0, action.Calls);
        projector.Fold(new StrategyEvent.CallerIdentified(CallerIdentity.Anonymous with
        {
            UserId = "customer", VerificationLevel = CallerVerificationLevel.KnowledgeBased,
        }, "Pin", DateTimeOffset.UtcNow));
        projector.Fold(new StrategyEvent.CredentialAttempted("Pin", true, null, DateTimeOffset.UtcNow, "customer"));
        Assert.True((await dispatcher.ExecuteAsync(session, "charge", TestContext.Current.CancellationToken)).Succeeded);
        Assert.True((await dispatcher.ExecuteAsync(session, "charge", TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal(1, action.Calls);
        Assert.Equal("call:flow:1:charge", action.Key);
    }

    private sealed class ActionStub : ICallWorkflowAction
    {
        public string Name => "charge";
        public int Calls { get; private set; }
        public string? Key { get; private set; }
        public Task<CallActionResult> ExecuteAsync(CallActionContext context, CancellationToken cancellationToken)
        {
            Calls++;
            Key = context.IdempotencyKey;
            return Task.FromResult(new CallActionResult(true, "done"));
        }
    }
}

using Agents.AI.ContactCenter.DependencyInjection;
using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Catalog;
using Agents.AI.ContactCenter.IvrWorkflow.Predicates;
using Agents.AI.Extensions.AITools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Compilation;

public sealed class WorkflowDependencyInjectionTests
{
    [Fact]
    public async Task Scoped_tools_and_predicates_validate_and_bind_per_scope()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.AddScoped<ScopedMarker>();
        builder.Services.AddScoped<IAIToolCollection, ScopedTools>();
        builder.Services.AddNamedEdgePredicate(
            "scoped-check",
            sp =>
            {
                _ = sp.GetRequiredService<ScopedMarker>();
                return (_, _) => ValueTask.FromResult(EdgePredicateResult.Allow());
            },
            ServiceLifetime.Scoped);
        builder.Services.AddCallWorkflow(CreateWorkflow("scoped-workflow", "scoped-tool", "scoped-check"));

        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        using var firstScope = host.Services.CreateScope();
        using var secondScope = host.Services.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<WorkflowRuntimeBinder>().Bind(
            host.Services.GetRequiredService<ICallWorkflowCatalog>().Get("scoped-workflow"));
        var second = secondScope.ServiceProvider.GetRequiredService<WorkflowRuntimeBinder>().Bind(
            host.Services.GetRequiredService<ICallWorkflowCatalog>().Get("scoped-workflow"));

        Assert.NotSame(first.GetStage("start").StageTools[0], second.GetStage("start").StageTools[0]);
        Assert.NotNull(first.GetStage("start").OutgoingEdges[0].Predicate);
    }

    [Fact]
    public async Task Duplicate_tool_names_fail_during_startup_validation()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.AddScoped<IAIToolCollection, ScopedTools>();
        builder.Services.AddScoped<IAIToolCollection, DuplicateScopedTools>();
        builder.Services.AddCallWorkflow(CreateWorkflow("duplicate-tools", "scoped-tool"));

        using var host = builder.Build();
        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Duplicate IVR tool name", exception.ToString(), StringComparison.Ordinal);
    }

    private static WorkflowBlueprint CreateWorkflow(
        string id,
        string toolName,
        string? predicateName = null) => new()
        {
            Id = id,
            InitialStageId = "start",
            Stages =
            [
                new StageBlueprint
                {
                    Id = "start",
                    ToolNames = [toolName],
                    Transitions =
                    [
                        new TransitionBlueprint
                        {
                            TargetStageId = "done",
                            Requires = predicateName is null ? [] : [PredicateRef.Named(predicateName)]
                        }
                    ]
                },
                new StageBlueprint { Id = "done", Terminal = true }
            ]
        };

    private sealed class ScopedMarker;

    private sealed class ScopedTools : IAIToolCollection
    {
        public IEnumerable<AITool> AsAITools()
        {
            yield return AIFunctionFactory.Create(() => "ok", "scoped-tool");
        }
    }

    private sealed class DuplicateScopedTools : IAIToolCollection
    {
        public IEnumerable<AITool> AsAITools()
        {
            yield return AIFunctionFactory.Create(() => "duplicate", "scoped-tool");
        }
    }
}

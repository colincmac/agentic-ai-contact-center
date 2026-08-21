using System.Threading.Channels;
using Agents.AI.ContactCenter.Calling;
using global::Agents.AI.ContactCenter.Authentication;
using global::Agents.AI.ContactCenter.Configuration;
using global::Agents.AI.ContactCenter.IvrWorkflow;
using global::Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using global::Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using global::Agents.AI.ContactCenter.IvrWorkflow.Execution;
using global::Agents.AI.ContactCenter.State;
using global::Agents.AI.ContactCenter.State.Projections;
using global::Agents.AI.ContactCenter.State.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Execution;

public sealed class WorkflowExecutorTests
{
    private static CompiledCallWorkflow Compile() => new WorkflowGraphCompiler().Compile(new WorkflowBlueprint
    {
        Id = "demo",
        InitialStageId = "welcome",
        Stages =
        [
            new StageBlueprint
            {
                Id = "welcome",
                Transitions =
                [
                    new TransitionBlueprint
                    {
                        TargetStageId = "balance",
                        Label = "balance",
                        Requires = [PredicateRef.AuthVerificationLevel(CallerVerificationLevel.MultiFactor)],
                    },
                    new TransitionBlueprint { TargetStageId = "transfer", Label = "agent" },
                ],
            },
            new StageBlueprint
            {
                Id = "verify",
                Transitions = [new TransitionBlueprint { TargetStageId = "balance", Label = "verified" }],
            },
            new StageBlueprint { Id = "balance", Terminal = true },
            new StageBlueprint { Id = "transfer", Terminal = true },
        ],
    });

    private static (WorkflowExecutor Executor, List<string> Rendered) NewExecutor(
        CompiledCallWorkflow workflow,
        IServiceProvider? sp = null)
    {
        var projector = new CallStateProjector(
            "call-test",
            [new AuthStateProjection(), new IvrStateProjection()],
            new InMemoryCallStateStore(),
            new CallStateOptions());
        var events = Channel.CreateUnbounded<StrategyEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var emit = new StateFoldingChannelWriter(events.Writer, () => projector);
        if (sp is null)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(projector);
            services.AddScoped<ICallerElevationDispatcher, CallerElevationDispatcher>();
            sp = services.BuildServiceProvider();
        }
        var session = new CallWorkflowSession(workflow, sp, sp.GetRequiredService<ICallerElevationDispatcher>());
        var rendered = new List<string>();

        var executor = new WorkflowExecutor(session, emit, (stage, _) =>
        {
            rendered.Add(stage.Id);
            return ValueTask.CompletedTask;
        }, projectorAccessor: () => projector);
        return (executor, rendered);
    }

    [Fact]
    public async Task EnterAsync_RendersInitialStage()
    {
        var workflow = Compile();
        var (executor, rendered) = NewExecutor(workflow);

        var stage = await executor.EnterAsync();

        Assert.Equal("welcome", stage.Id);
        Assert.Equal(["welcome"], rendered);
    }


    [Fact]
    public async Task AdvanceAlong_Allowed_AdvancesAndRenders()
    {
        var workflow = Compile();
        var (executor, rendered) = NewExecutor(workflow);
        await executor.EnterAsync();
        rendered.Clear();

        var edge = executor.CurrentStage!.FindEdgeByLabel("agent")!;
        var outcome = await executor.AdvanceAlongAsync(edge);

        var advanced = Assert.IsType<AdvanceOutcome.Advanced>(outcome);
        Assert.Equal("transfer", advanced.NewStage.Id);
        Assert.Equal(["transfer"], rendered);
    }

    [Fact]
    public async Task AdvanceAlong_Blocked_DeniesAndStays()
    {
        var workflow = Compile();
        var (executor, rendered) = NewExecutor(workflow);
        await executor.EnterAsync();
        rendered.Clear();

        var edge = executor.CurrentStage!.FindEdgeByLabel("balance")!;
        var outcome = await executor.AdvanceAlongAsync(edge);

        Assert.IsType<AdvanceOutcome.Denied>(outcome);
        Assert.Equal("welcome", executor.CurrentStage!.Id);
        Assert.Empty(rendered);
    }

    [Fact]
    public async Task AdvanceAlong_AmbiguousTarget_UsesChosenEdgePredicate()
    {
        // Two edges from 'start' to the SAME target stage 'servicing' with different
        // labels + predicates. Resolving by stage id would collapse both to the first
        // edge; AdvanceAlongAsync must honor the exact edge the caller chose.
        var workflow = new WorkflowGraphCompiler().Compile(new WorkflowBlueprint
        {
            Id = "ambiguous",
            InitialStageId = "start",
            Stages =
            [
                new StageBlueprint
                {
                    Id = "start",
                    Transitions =
                    [
                        // First edge blocks (state has no 'vip_flag').
                        new TransitionBlueprint
                        {
                            TargetStageId = "servicing",
                            Label = "vip",
                            Requires = [PredicateRef.StateHas("vip_flag")],
                        },
                        // Second edge always passes.
                        new TransitionBlueprint { TargetStageId = "servicing", Label = "standard" },
                    ],
                },
                new StageBlueprint { Id = "servicing", Terminal = true },
            ],
        });

        var (executor, _) = NewExecutor(workflow);
        await executor.EnterAsync();

        var vipEdge = executor.CurrentStage!.FindEdgeByLabel("vip")!;
        var blocked = await executor.AdvanceAlongAsync(vipEdge);
        Assert.IsType<AdvanceOutcome.Denied>(blocked);
        Assert.Equal("start", executor.CurrentStage!.Id);

        var standardEdge = executor.CurrentStage!.FindEdgeByLabel("standard")!;
        var allowed = await executor.AdvanceAlongAsync(standardEdge);
        var advanced = Assert.IsType<AdvanceOutcome.Advanced>(allowed);
        Assert.Equal("servicing", advanced.NewStage.Id);
    }
}

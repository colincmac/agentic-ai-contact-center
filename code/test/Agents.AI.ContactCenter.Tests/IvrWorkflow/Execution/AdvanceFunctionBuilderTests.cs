using System.Text.Json;
using System.Threading.Channels;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.State;
using Agents.AI.ContactCenter.State.Projections;
using Agents.AI.ContactCenter.State.Stores;
using global::Agents.AI.ContactCenter.IvrWorkflow;
using global::Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using global::Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using global::Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Microsoft.Extensions.AI;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Execution;

public sealed class AdvanceFunctionBuilderTests
{
    private static readonly JsonSerializerOptions jsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };


    private static AdvanceFunctionResult Deserialize(object? raw)
    {
        Assert.NotNull(raw);
        var json = raw is JsonElement el ? el.GetRawText() : raw!.ToString();
        return JsonSerializer.Deserialize<AdvanceFunctionResult>(json!, jsonOpts)!;
    }
    private static CompiledCallWorkflow TwoTransitionWorkflow() => new WorkflowGraphCompiler().Compile(new WorkflowBlueprint
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
                    new TransitionBlueprint { TargetStageId = "balance", Label = "balance", When = "Caller wants balance." },
                    new TransitionBlueprint { TargetStageId = "agent", Label = "agent", When = "Caller wants live agent." },
                ],
            },
            new StageBlueprint { Id = "balance", Terminal = true },
            new StageBlueprint { Id = "agent", Terminal = true },
        ],
    });

    private static (WorkflowExecutor Executor, CallWorkflowSession Session, List<string> Rendered) NewExecutor(
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
            services.AddSingleton<ICallerElevationDispatcher>(p => new CallerElevationDispatcher([], p));
            sp = services.BuildServiceProvider();
        }
        var session = new CallWorkflowSession(workflow, sp, sp.GetRequiredService<ICallerElevationDispatcher>());
        var rendered = new List<string>();
        var executor = new WorkflowExecutor(
            session,
            emit,
            (stage, _) =>
            {
                rendered.Add(stage.Id);
                return ValueTask.CompletedTask;
            },
            projectorAccessor: () => projector);
        return (executor, session, rendered);
    }

    [Fact]
    public void BuildForStage_NullOnTerminal()
    {
        var workflow = TwoTransitionWorkflow();
        var (executor, _, _) = NewExecutor(workflow);

        var fn = AdvanceFunctionBuilder.BuildForStage(workflow.GetStage("balance"), executor);

        Assert.Null(fn);
    }

    [Fact]
    public void BuildForStage_NameAndDescription()
    {
        var workflow = TwoTransitionWorkflow();
        var (executor, _, _) = NewExecutor(workflow);

        var fn = AdvanceFunctionBuilder.BuildForStage(workflow.GetStage("welcome"), executor);

        Assert.NotNull(fn);
        Assert.Equal("advance", fn!.Name);
        Assert.Contains("`balance`", fn.Description);
        Assert.Contains("Caller wants balance.", fn.Description);
        Assert.Contains("`agent`", fn.Description);
    }

    [Fact]
    public async Task Invoke_WithKnownLabel_AdvancesAndCallsRender()
    {
        var workflow = TwoTransitionWorkflow();
        var (executor, session, rendered) = NewExecutor(workflow);
        await executor.EnterAsync();

        var fn = AdvanceFunctionBuilder.BuildForStage(workflow.GetStage("welcome"), executor)!;
        var args = new AIFunctionArguments { ["target"] = "balance" };

        var result = Deserialize(await fn.InvokeAsync(args));

        Assert.True(result.Advanced);
        Assert.Equal("balance", result.Stage);
        Assert.Equal("balance", executor.CurrentStage!.Id);
        Assert.Equal(["welcome", "balance"], rendered);
    }

    [Fact]
    public async Task Invoke_WithUnknownLabel_IsDeniedWithoutMutation()
    {
        var workflow = TwoTransitionWorkflow();
        var (executor, session, rendered) = NewExecutor(workflow);
        await executor.EnterAsync();
        rendered.Clear();

        var fn = AdvanceFunctionBuilder.BuildForStage(workflow.GetStage("welcome"), executor)!;
        var args = new AIFunctionArguments { ["target"] = "unknown" };

        var result = Deserialize(await fn.InvokeAsync(args));

        Assert.False(result.Advanced);
        Assert.Contains("not a valid transition label", result.Note);
        Assert.Equal("welcome", executor.CurrentStage!.Id);
        Assert.Empty(rendered);
    }

    [Fact]
    public async Task Invoke_LabelLookup_IsCaseInsensitive()
    {
        var workflow = TwoTransitionWorkflow();
        var (executor, session, _) = NewExecutor(workflow);
        await executor.EnterAsync();

        var fn = AdvanceFunctionBuilder.BuildForStage(workflow.GetStage("welcome"), executor)!;
        var args = new AIFunctionArguments { ["target"] = "AGENT" };

        var result = Deserialize(await fn.InvokeAsync(args));

        Assert.True(result.Advanced);
        Assert.Equal("agent", executor.CurrentStage!.Id);
    }

    [Fact]
    public void JsonSchema_ConstrainsTargetToStageLabels()
    {
        var workflow = TwoTransitionWorkflow();
        var (executor, _, _) = NewExecutor(workflow);

        var fn = AdvanceFunctionBuilder.BuildForStage(workflow.GetStage("welcome"), executor)!;

        var target = fn.JsonSchema.GetProperty("properties").GetProperty("target");
        Assert.True(target.TryGetProperty("enum", out var enumElement));
        var values = enumElement.EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["balance", "agent"], values);
    }

    [Fact]
    public async Task Invoke_AmbiguousTarget_HonorsChosenEdgePredicate()
    {
        // Two edges to the SAME target stage with distinct labels + predicates. Resolving
        // by stage id would collapse both to the first edge; the builder must advance along
        // the exact edge the model selected.
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
                        new TransitionBlueprint
                        {
                            TargetStageId = "servicing",
                            Label = "vip",
                            Requires = [PredicateRef.StateHas("vip_flag")],
                        },
                        new TransitionBlueprint { TargetStageId = "servicing", Label = "standard" },
                    ],
                },
                new StageBlueprint { Id = "servicing", Terminal = true },
            ],
        });
        var (executor, session, _) = NewExecutor(workflow);
        await executor.EnterAsync();

        var fn = AdvanceFunctionBuilder.BuildForStage(workflow.GetStage("start"), executor)!;

        var blocked = Deserialize(await fn.InvokeAsync(new AIFunctionArguments { ["target"] = "vip" }));
        Assert.False(blocked.Advanced);
        Assert.Equal("start", executor.CurrentStage!.Id);

        var allowed = Deserialize(await fn.InvokeAsync(new AIFunctionArguments { ["target"] = "standard" }));
        Assert.True(allowed.Advanced);
        Assert.Equal("servicing", allowed.Stage);
    }
}

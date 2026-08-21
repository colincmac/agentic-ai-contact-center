//using System.Threading.Channels;
//using Agents.AI.ContactCenter.Calling;
//using Agents.AI.ContactCenter.IvrWorkflow.Execution;
//using global::Agents.AI.ContactCenter.Authentication;
//using global::Agents.AI.ContactCenter.IvrWorkflow;
//using global::Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
//using global::Agents.AI.ContactCenter.IvrWorkflow.Compilation;
//using Microsoft.Extensions.DependencyInjection;

//namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Navigation;

//public sealed class CallWorkflowNavigatorTests
//{

//    private static CallWorkflowSession NewSession(CompiledCallWorkflow wf, IServiceProvider? sp = null)
//    {
//        sp ??= new ServiceCollection().BuildServiceProvider();
//        return new CallWorkflowSession(wf, sp, new IvrWorkflowState(), new CallerAuthenticationState(), sp.GetRequiredService<ICallerElevationDispatcher>());
//    }

//    private static (WorkflowExecutor Executor, List<string> Rendered) NewExecutor(
//    CompiledCallWorkflow workflow,
//    IServiceProvider? sp = null,
//    IvrWorkflowState? state = null)
//    {
//        var events = Channel.CreateUnbounded<StrategyEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

//        sp ??= new ServiceCollection().BuildServiceProvider();
//        state ??= new IvrWorkflowState();
//        var session = new CallWorkflowSession(workflow, sp, state, new CallerAuthenticationState(), sp.GetRequiredService<ICallerElevationDispatcher>());
//        var rendered = new List<string>();
//        var executor = new WorkflowExecutor(session,events.Writer, (stage, _) =>
//        {
//            rendered.Add(stage.Id);
//            return ValueTask.CompletedTask;
//        });
//        return (executor, rendered);
//    }

//    private static CompiledCallWorkflow Compile() => new WorkflowGraphCompiler().Compile(new WorkflowBlueprint
//    {
//        Id = "demo",
//        InitialStageId = "welcome",
//        Stages =
//        [
//            new StageBlueprint
//            {
//                Id = "welcome",
//                Goal = "Greet and collect intent.",
//                Transitions =
//                [
//                    new TransitionBlueprint
//                    {
//                        TargetStageId = "balance",
//                        Label = "balance",
//                        Requires = [PredicateRef.AuthVerificationLevel(CallerVerificationLevel.MultiFactor)],
//                    },
//                    new TransitionBlueprint
//                    {
//                        TargetStageId = "transfer",
//                        Label = "agent",
//                    },
//                ],
//            },
//            new StageBlueprint
//            {
//                Id = "verify",
//                Transitions = [new TransitionBlueprint { TargetStageId = "balance", Label = "verified" }],
//            },
//            new StageBlueprint { Id = "balance", Terminal = true, TerminalOutcome = BlueprintTerminalOutcome.Success },
//            new StageBlueprint { Id = "transfer", Terminal = true, TerminalOutcome = BlueprintTerminalOutcome.Escalated },
//        ],
//    });

//    private static IServiceProvider EmptyServices() => new ServiceCollection().BuildServiceProvider();

//    private static CallerAuthenticationState GetAuthenticationState(CallerVerificationLevel? level = null)
//    {
//        var auth = new CallerAuthenticationState();
//        if(level is { } verificationLevel) auth.TryPromote(CallerIdentity.Anonymous with { UserId = "u", VerificationLevel = verificationLevel });
//        return auth;
//    }
//    private static IServiceProvider ServicesWithAuth(CallerVerificationLevel level)
//    {
//        var auth = new CallerAuthenticationState();
//        auth.TryPromote(CallerIdentity.Anonymous with { UserId = "u", VerificationLevel = level });
//        return new ServiceCollection().AddSingleton(auth).BuildServiceProvider();
//    }

//    [Fact]
//    public void EnterInitialStage_PositionsAtInitial()
//    {
//        var workflow = Compile();
//        var (executor, rendered) = NewExecutor(workflow);

//        var stage = executor.EnterInitialStage();

//        Assert.Equal("welcome", stage.Id);
//        Assert.Same(stage, executor.CurrentStage);
//        Assert.Equal("welcome", executor.State.CurrentStepName);
//        Assert.Equal(IvrWorkflowStatus.Running, executor.State.Status);
//    }

//    [Fact]
//    public void EnterInitialStage_ResumesFromPriorState()
//    {
//        var events = Channel.CreateUnbounded<StrategyEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

//        var workflow = Compile();
//        var state = new IvrWorkflowState { CurrentStepName = "verify" };
//        var session = new CallWorkflowSession(workflow, EmptyServices(), state, GetAuthenticationState());
//        var executor = new WorkflowExecutor(session,events.Writer, (stage, _) => ValueTask.CompletedTask);

//        var stage = executor.EnterInitialStage();

//        Assert.Equal("verify", stage.Id);
//    }

//    // TODO: broken test, need to fix
//    //[Fact]
//    //public async Task EvaluateTransition_Allowed_WhenPredicatePasses()
//    //{
//    //    var workflow = Compile();

//    //    var (executor, rendered) = NewExecutor(workflow, ServicesWithAuth(CallerVerificationLevel.MultiFactor));
//    //    executor.EnterInitialStage();

//    //    var result = await executor.EvaluateTransitionAsync("balance");

//    //    var allowed = Assert.IsType<TransitionEvaluation.Allowed>(result);
//    //    Assert.Equal("balance", allowed.Edge.TargetStageId);
//    //}

//    [Fact]
//    public async Task EvaluateTransition_Blocked_WhenPredicateDenies()
//    {
//        var workflow = Compile();
//        var (executor, rendered) = NewExecutor(workflow); // no auth state
//        executor.EnterInitialStage();

//        var result = await executor.EvaluateTransitionAsync("balance");

//        var blocked = Assert.IsType<TransitionEvaluation.Blocked>(result);
//        Assert.Equal("balance", blocked.Edge.TargetStageId);
//        Assert.False(string.IsNullOrEmpty(blocked.Reason));
//    }

//    [Fact]
//    public async Task EvaluateTransition_Blocked_WhenNoFallback()
//    {
//        var workflow = new WorkflowGraphCompiler().Compile(new WorkflowBlueprint
//        {
//            Id = "no-fallback",
//            InitialStageId = "a",
//            Stages =
//            [
//                new StageBlueprint
//                {
//                    Id = "a",
//                    Transitions =
//                    [
//                        new TransitionBlueprint
//                        {
//                            TargetStageId = "b",
//                            Requires = [PredicateRef.AuthVerificationLevel(CallerVerificationLevel.MultiFactor)],
//                        },
//                    ],
//                },
//                new StageBlueprint { Id = "b", Terminal = true },
//            ],
//        });

//        var (executor, rendered) = NewExecutor(workflow, ServicesWithAuth(CallerVerificationLevel.MultiFactor));
//        executor.EnterInitialStage();

//        var result = await executor.EvaluateTransitionAsync("b");

//        Assert.IsType<TransitionEvaluation.Blocked>(result);
//    }

//    [Fact]
//    public async Task EvaluateTransition_Invalid_WhenNoEdge()
//    {
//        var workflow = Compile();
//        var (executor, rendered) = NewExecutor(workflow);
//        executor.EnterInitialStage();

//        var result = await executor.EvaluateTransitionAsync("nonexistent");

//        var invalid = Assert.IsType<TransitionEvaluation.Invalid>(result);
//        Assert.Contains("nonexistent", invalid.Reason);
//    }

//    [Fact]
//    public async Task ApplyTransition_AdvancesCurrentStageAndState()
//    {
//        var workflow = Compile();
//        var (executor, rendered) = NewExecutor(workflow, ServicesWithAuth(CallerVerificationLevel.MultiFactor));
//        executor.EnterInitialStage();

//        var evaluation = await executor.EvaluateTransitionAsync("transfer");
//        var allowed = Assert.IsType<TransitionEvaluation.Allowed>(evaluation);

//        var newStage = executor.ApplyTransition(allowed.Edge);

//        Assert.Equal("transfer", newStage.Id);
//        Assert.Equal("transfer", executor.State.CurrentStepName);
//        Assert.True(executor.IsComplete);
//        Assert.Equal(IvrWorkflowStatus.Completed, executor.State.Status);
//    }

//    [Fact]
//    public async Task EvaluateTransition_BeforeEnterInitial_Throws()
//    {
//        var (executor, rendered) = NewExecutor(Compile(), ServicesWithAuth(CallerVerificationLevel.MultiFactor));
//        await Assert.ThrowsAsync<InvalidOperationException>(async () => await executor.EvaluateTransitionAsync("balance"));
//    }

//    [Fact]
//    public void TierSwap_StatePreserved_NavigatorResumesAtCorrectStage()
//    {
//        var workflow = Compile();
//        var state = new IvrWorkflowState();
//        state.Set("intent", "balance");

//        var (executor1, rendered1) = NewExecutor(workflow, ServicesWithAuth(CallerVerificationLevel.MultiFactor), state);
//        executor1.EnterInitialStage();
//        executor1.ApplyTransition(executor1.CurrentStage!.FindEdgeTo("transfer")!);

//        // Simulate tier swap: new navigator instance, same state.
//        var (executor2, rendered2) = NewExecutor(workflow, ServicesWithAuth(CallerVerificationLevel.MultiFactor), state);
//        var resumed = executor2.EnterInitialStage();

//        Assert.Equal("transfer", resumed.Id);
//        Assert.Equal("balance", state.Get<string>("intent"));
//    }
//}

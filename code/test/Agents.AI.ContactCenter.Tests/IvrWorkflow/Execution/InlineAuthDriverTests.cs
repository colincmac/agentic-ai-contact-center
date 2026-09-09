using System.Threading.Channels;
using Agents.AI.ContactCenter.Calling;
using global::Agents.AI.ContactCenter.Authentication;
using global::Agents.AI.ContactCenter.IvrWorkflow;
using global::Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using global::Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using global::Agents.AI.ContactCenter.IvrWorkflow.Execution;
using global::Agents.AI.ContactCenter.Configuration;
using global::Agents.AI.ContactCenter.State;
using global::Agents.AI.ContactCenter.State.Projections;
using global::Agents.AI.ContactCenter.State.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Execution;

public sealed class InlineAuthDriverTests
{
    private sealed class FakeCredentialAuthenticator(string name, string acceptValue, CallerVerificationLevel elevatesTo)
        : ICredentialAuthenticator
    {
        private string? _stashed;

        public string Name => name;
        public CallerVerificationLevel ElevatesTo => elevatesTo;
        public CallerVerificationLevel RequiredPriorLevel => CallerVerificationLevel.None;

        public CredentialRequest DescribeRequest(AuthenticationContext context) => new()
        {
            AuthenticatorName = name,
            Kind = CredentialKind.Digits,
            Purpose = "a code",
            MinLength = 4,
            MaxLength = 4,
        };

        public void StashInput(AuthenticationContext context, CredentialInput input) => _stashed = input.Value;

        public Task<AuthenticationOutcome> AuthenticateAsync(AuthenticationContext context, CancellationToken cancellationToken = default)
        {
            var value = _stashed;
            _stashed = null;
            if (value is null)
            {
                return Task.FromResult<AuthenticationOutcome>(new AuthenticationOutcome.NotApplicable("no input"));
            }
            if (value == acceptValue)
            {
                var identity = CallerIdentity.Anonymous with
                {
                    UserId = "u",
                    VerificationLevel = elevatesTo,
                    AuthenticatedBy = name,
                };
                return Task.FromResult<AuthenticationOutcome>(new AuthenticationOutcome.Authenticated(identity));
            }
            return Task.FromResult<AuthenticationOutcome>(new AuthenticationOutcome.Failed("wrong value"));
        }
    }

    private static (WorkflowExecutor Executor, List<AuthStepRender> AuthRenders, List<string> BusinessRenders, CallStateProjector Projector) Build(
        AuthenticationPlanBlueprint plan,
        params ICallerAuthenticator[] authenticators)
    {
        var projector = new CallStateProjector(
            "call-test",
            [new AuthStateProjection(), new IvrStateProjection()],
            new InMemoryCallStateStore(),
            new CallStateOptions());
        var events = Channel.CreateUnbounded<StrategyEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var emit = new StateFoldingChannelWriter(events.Writer, () => projector);

        var services = new ServiceCollection();
        foreach (var a in authenticators)
        {
            // Registered as the interface so the elevation dispatcher resolves them by name.
            services.AddSingleton<ICallerAuthenticator>(a);
        }
        services.AddSingleton(projector);
        services.AddScoped<ICallerElevationDispatcher, CallerElevationDispatcher>();
        var sp = services.BuildServiceProvider();

        var blueprint = new WorkflowBlueprint
        {
            Id = "t",
            InitialStageId = "secure",
            Stages =
            [
                new StageBlueprint
                {
                    Id = "secure",
                    Authentication = new AuthenticationPlanBlueprint
                    {
                        Steps = plan.Steps,
                        MaxAttemptsPerStep = plan.MaxAttemptsPerStep,
                        EvidenceMaxAge = plan.EvidenceMaxAge,
                        FailureStageId = "denied",
                    },
                },
                new StageBlueprint { Id = "denied", Terminal = true, TerminalOutcome = BlueprintTerminalOutcome.Failure },
            ],
        };
        var workflow = new WorkflowGraphCompiler().Compile(blueprint);
        var session = new CallWorkflowSession(
            workflow,
            sp,
            sp.GetRequiredService<ICallerElevationDispatcher>(),
            authenticators.OfType<ICredentialAuthenticator>());

        var authRenders = new List<AuthStepRender>();
        var businessRenders = new List<string>();
        var executor = new WorkflowExecutor(
            session,
            emit,
            (stage, _) => { businessRenders.Add(stage.Id); return ValueTask.CompletedTask; },
            (render, _) => { authRenders.Add(render); return ValueTask.CompletedTask; },
            projectorAccessor: () => projector);

        return (executor, authRenders, businessRenders, projector);
    }

    [Fact]
    public async Task Enter_WithUnsatisfiedPlan_RendersAuthNotBusiness()
    {
        var plan = new AuthenticationPlanBlueprint { Steps = [new AuthStepGroup(["Fake"])] };
        var (executor, authRenders, businessRenders, _) = Build(
            plan, new FakeCredentialAuthenticator("Fake", "1234", CallerVerificationLevel.KnowledgeBased));

        await executor.EnterAsync();

        Assert.Single(authRenders);
        Assert.Empty(businessRenders);
        Assert.Equal("Fake", authRenders[0].Requests[0].AuthenticatorName);
    }

    [Fact]
    public async Task Submit_CorrectCredential_ElevatesAndRendersBusiness()
    {
        var plan = new AuthenticationPlanBlueprint { Steps = [new AuthStepGroup(["Fake"])] };
        var (executor, _, businessRenders, projector) = Build(
            plan, new FakeCredentialAuthenticator("Fake", "1234", CallerVerificationLevel.KnowledgeBased));

        await executor.EnterAsync();
        await executor.SubmitCredentialAsync("Fake", new CredentialInput("1234"));

        Assert.Equal(CallerVerificationLevel.KnowledgeBased, projector.Get<AuthSnapshot>().Level);
        Assert.Equal(["secure"], businessRenders);
    }

    [Fact]
    public async Task Submit_WrongCredential_ReRendersAuthAndStaysUnverified()
    {
        var plan = new AuthenticationPlanBlueprint { Steps = [new AuthStepGroup(["Fake"])] };
        var (executor, authRenders, businessRenders, projector) = Build(
            plan, new FakeCredentialAuthenticator("Fake", "1234", CallerVerificationLevel.KnowledgeBased));

        await executor.EnterAsync();
        await executor.SubmitCredentialAsync("Fake", new CredentialInput("0000"));

        Assert.Equal(2, authRenders.Count);
        Assert.Empty(businessRenders);
        Assert.Equal(CallerVerificationLevel.None, projector.Get<AuthSnapshot>().Level);
    }

    [Fact]
    public async Task Plan_TwoSteps_RunInOrderThenBusiness()
    {
        var plan = new AuthenticationPlanBlueprint
        {
            Steps = [new AuthStepGroup(["Identify"]), new AuthStepGroup(["Confirm"])],
        };
        var (executor, authRenders, businessRenders, projector) = Build(
            plan,
            new FakeCredentialAuthenticator("Identify", "1111", CallerVerificationLevel.KnowledgeBased),
            new FakeCredentialAuthenticator("Confirm", "2222", CallerVerificationLevel.MultiFactor));

        await executor.EnterAsync();
        Assert.Equal("Identify", authRenders[^1].Requests[0].AuthenticatorName);

        await executor.SubmitCredentialAsync("Identify", new CredentialInput("1111"));
        Assert.Equal("Confirm", authRenders[^1].Requests[0].AuthenticatorName);
        Assert.Empty(businessRenders);

        await executor.SubmitCredentialAsync("Confirm", new CredentialInput("2222"));
        Assert.Equal(["secure"], businessRenders);
        Assert.Equal(CallerVerificationLevel.MultiFactor, projector.Get<AuthSnapshot>().Level);
    }

    [Fact]
    public async Task AnyOf_EitherAuthenticatorSatisfiesStep()
    {
        var plan = new AuthenticationPlanBlueprint { Steps = [new AuthStepGroup(["Pin", "Otp"])] };
        var (executor, authRenders, businessRenders, _) = Build(
            plan,
            new FakeCredentialAuthenticator("Pin", "1234", CallerVerificationLevel.KnowledgeBased),
            new FakeCredentialAuthenticator("Otp", "9999", CallerVerificationLevel.MultiFactor));

        await executor.EnterAsync();
        Assert.Equal(2, authRenders[^1].Requests.Count); // both choices surfaced

        await executor.SubmitCredentialAsync("Otp", new CredentialInput("9999"));
        Assert.Equal(["secure"], businessRenders);
    }

    [Fact]
    public async Task Plan_ExhaustsRetries_RoutesToExplicitFailure()
    {
        var plan = new AuthenticationPlanBlueprint
        {
            Steps = [new AuthStepGroup(["Fake"])],
            MaxAttemptsPerStep = 2,
        };
        var (executor, _, businessRenders, projector) = Build(
            plan, new FakeCredentialAuthenticator("Fake", "1234", CallerVerificationLevel.KnowledgeBased));

        await executor.EnterAsync();
        await executor.SubmitCredentialAsync("Fake", new CredentialInput("0000"));
        Assert.Empty(businessRenders);
        await executor.SubmitCredentialAsync("Fake", new CredentialInput("0000"));

        Assert.Equal(["denied"], businessRenders);
        Assert.Equal(CallerVerificationLevel.None, projector.Get<AuthSnapshot>().Level);
    }

    [Fact]
    public async Task EqualRank_DifferentMethod_DoesNotSatisfyNextStep()
    {
        var (executor, renders, business, _) = Build(
            new AuthenticationPlanBlueprint { Steps = [new(["Identify"]), new(["Pin"])] },
            new FakeCredentialAuthenticator("Identify", "1111", CallerVerificationLevel.KnowledgeBased),
            new FakeCredentialAuthenticator("Pin", "2222", CallerVerificationLevel.KnowledgeBased));
        await executor.EnterAsync();
        await executor.SubmitCredentialAsync("Identify", new("1111"));
        Assert.Empty(business);
        Assert.Equal("Pin", renders[^1].Requests[0].AuthenticatorName);
        await executor.SubmitCredentialAsync("Pin", new("2222"));
        Assert.Equal(["secure"], business);
    }

    [Fact]
    public async Task OutOfOrderCredential_IsRejected()
    {
        var (executor, _, business, _) = Build(
            new AuthenticationPlanBlueprint { Steps = [new(["Identify"]), new(["Pin"])] },
            new FakeCredentialAuthenticator("Identify", "1111", CallerVerificationLevel.KnowledgeBased),
            new FakeCredentialAuthenticator("Pin", "2222", CallerVerificationLevel.MultiFactor));
        await executor.EnterAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.SubmitCredentialAsync("Pin", new("2222")));
        Assert.Empty(business);
    }

    [Fact]
    public async Task ExpiredEvidence_RequiresVerificationAgain()
    {
        var (executor, renders, business, projector) = Build(
            new AuthenticationPlanBlueprint { Steps = [new(["Pin"])] },
            new FakeCredentialAuthenticator("Pin", "2222", CallerVerificationLevel.KnowledgeBased));
        projector.Fold(new StrategyEvent.CallerIdentified(CallerIdentity.Anonymous with
        {
            UserId = "u", VerificationLevel = CallerVerificationLevel.KnowledgeBased,
        }, "Pin", DateTimeOffset.UtcNow.AddHours(-1)));
        projector.Fold(new StrategyEvent.CredentialAttempted("Pin", true, null, DateTimeOffset.UtcNow.AddHours(-1), "u"));
        await executor.EnterAsync();
        Assert.Empty(business);
        Assert.Single(renders);
    }
}

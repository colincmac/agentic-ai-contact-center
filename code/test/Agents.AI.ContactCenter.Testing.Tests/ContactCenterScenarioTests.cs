using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Exceptions;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Agents.AI.ContactCenter.Testing;

namespace Agents.AI.ContactCenter.Testing.Tests;

public sealed class ContactCenterScenarioTests
{
    [Fact]
    public async Task Yaml_scenario_executes_real_transition_and_projection()
    {
        const string yaml = """
            id: customer-service
            version: 1
            initialStage: welcome
            stages:
              - id: welcome
                transitions:
                  - to: complete
                    label: finish
              - id: complete
                terminal: true
                terminalOutcome: success
            """;
        await using var scenario = await ContactCenterScenario
            .FromYaml(yaml, "customer-service.yaml")
            .BuildAsync(TestContext.Current.CancellationToken);

        var entered = await scenario.EnterAsync(TestContext.Current.CancellationToken);
        entered
            .ShouldHaveReachedStage("welcome")
            .ShouldBeAtStage("welcome")
            .ShouldHaveWorkflowStatus(IvrWorkflowStatus.Running);

        var outcome = await scenario.AdvanceAsync("finish", TestContext.Current.CancellationToken);
        Assert.IsType<AdvanceOutcome.Advanced>(outcome);
        scenario.Snapshot()
            .ShouldHaveReachedStage("complete")
            .ShouldBeAtStage("complete")
            .ShouldHaveWorkflowStatus(IvrWorkflowStatus.Completed)
            .ShouldContainEvent<StrategyEvent.WorkflowCompleted>();
    }

    [Fact]
    public async Task Blocked_transition_uses_real_predicate_and_does_not_render_target()
    {
        var workflow = new WorkflowBlueprint
        {
            Id = "guarded",
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
                            TargetStageId = "secure",
                            Label = "continue",
                            Requires = [PredicateRef.StateHas("approved")]
                        }
                    ]
                },
                new StageBlueprint { Id = "secure", Terminal = true }
            ]
        };
        await using var scenario = await ContactCenterScenario
            .ForWorkflow(workflow)
            .BuildAsync(TestContext.Current.CancellationToken);
        await scenario.EnterAsync(TestContext.Current.CancellationToken);

        var outcome = await scenario.AdvanceAsync("continue", TestContext.Current.CancellationToken);

        Assert.IsType<AdvanceOutcome.Denied>(outcome);
        scenario.Snapshot().ShouldBeAtStage("start");
        Assert.DoesNotContain("secure", scenario.Snapshot().RenderedStages);
    }

    [Fact]
    public async Task Inline_credential_uses_real_auth_dispatch_and_projection()
    {
        var workflow = new WorkflowBlueprint
        {
            Id = "authenticated",
            InitialStageId = "secure",
            Stages =
            [
                new StageBlueprint
                {
                    Id = "secure",
                    Authentication = new AuthenticationPlanBlueprint
                    {
                        Steps = [new AuthStepGroup(["TestPin"])]
                    }
                }
            ]
        };
        await using var scenario = await ContactCenterScenario
            .ForWorkflow(workflow)
            .AddAuthenticator(new TestCredentialAuthenticator())
            .BuildAsync(TestContext.Current.CancellationToken);

        var entered = await scenario.EnterAsync(TestContext.Current.CancellationToken);
        entered.ShouldRequestCredential("TestPin");

        var result = await scenario.SubmitCredentialAsync(
            "TestPin",
            "1234",
            TestContext.Current.CancellationToken);

        result
            .ShouldHaveVerificationLevel(CallerVerificationLevel.KnowledgeBased)
            .ShouldHaveReachedStage("secure")
            .ShouldContainEvent<StrategyEvent.CredentialAttempted>();
    }

    [Fact]
    public async Task Missing_workflow_tool_fails_during_scenario_build()
    {
        var workflow = new WorkflowBlueprint
        {
            Id = "tools",
            InitialStageId = "start",
            Stages = [new StageBlueprint { Id = "start", ToolNames = ["missing_tool"] }]
        };

        var exception = await Assert.ThrowsAsync<WorkflowCompilationException>(() =>
            ContactCenterScenario.ForWorkflow(workflow).BuildAsync(TestContext.Current.CancellationToken));

        Assert.Contains("missing_tool", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Assertion_failures_are_test_framework_neutral_and_descriptive()
    {
        var workflow = new WorkflowBlueprint
        {
            Id = "assertions",
            InitialStageId = "start",
            Stages = [new StageBlueprint { Id = "start" }]
        };
        await using var scenario = await ContactCenterScenario
            .ForWorkflow(workflow)
            .BuildAsync(TestContext.Current.CancellationToken);
        var result = await scenario.EnterAsync(TestContext.Current.CancellationToken);

        var exception = Assert.Throws<ScenarioAssertionException>(() =>
            result.ShouldHaveReachedStage("missing"));

        Assert.Contains("Expected stage 'missing'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("start", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Builder_and_workflow_entry_are_one_shot()
    {
        var workflow = new WorkflowBlueprint
        {
            Id = "lifecycle",
            InitialStageId = "start",
            Stages = [new StageBlueprint { Id = "start" }]
        };
        var builder = ContactCenterScenario.ForWorkflow(workflow);
        await using var scenario = await builder.BuildAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.BuildAsync(TestContext.Current.CancellationToken));
        await scenario.EnterAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scenario.EnterAsync(TestContext.Current.CancellationToken));
    }

    private sealed class TestCredentialAuthenticator : ICredentialAuthenticator
    {
        private string? _value;

        public string Name => "TestPin";
        public CallerVerificationLevel ElevatesTo => CallerVerificationLevel.KnowledgeBased;
        public CallerVerificationLevel RequiredPriorLevel => CallerVerificationLevel.None;

        public CredentialRequest DescribeRequest(AuthenticationContext context) => new()
        {
            AuthenticatorName = Name,
            Kind = CredentialKind.Digits,
            Purpose = "test PIN",
            MinLength = 4,
            MaxLength = 4
        };

        public void StashInput(AuthenticationContext context, CredentialInput input)
            => _value = input.Value;

        public Task<AuthenticationOutcome> AuthenticateAsync(
            AuthenticationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_value != "1234")
            {
                return Task.FromResult<AuthenticationOutcome>(
                    new AuthenticationOutcome.Failed("wrong PIN"));
            }

            return Task.FromResult<AuthenticationOutcome>(
                new AuthenticationOutcome.Authenticated(CallerIdentity.Anonymous with
                {
                    UserId = "customer",
                    VerificationLevel = CallerVerificationLevel.KnowledgeBased,
                    AuthenticatedBy = Name
                }));
        }
    }
}

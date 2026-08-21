using global::Agents.AI.ContactCenter.Authentication;
using global::Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using global::Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using global::Agents.AI.ContactCenter.IvrWorkflow.Loading;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Loading;

public sealed class CallWorkflowYamlReaderTests
{
    private const string MinimalYaml = """
        id: minimal
        initialStage: start
        stages:
          - id: start
            terminal: true
            terminalOutcome: success
        """;

    [Fact]
    public void Read_Minimal_ProducesBlueprint()
    {
        var blueprint = CallWorkflowYamlReader.Read(MinimalYaml);

        Assert.Equal("minimal", blueprint.Id);
        Assert.Equal("start", blueprint.InitialStageId);
        Assert.Single(blueprint.Stages);
        Assert.True(blueprint.Stages[0].Terminal);
        Assert.Equal(BlueprintTerminalOutcome.Success, blueprint.Stages[0].TerminalOutcome);
    }

    [Fact]
    public void Read_MissingId_Throws()
    {
        var yaml = """
            initialStage: a
            stages:
              - id: a
                terminal: true
            """;
        var ex = Assert.Throws<CallWorkflowYamlException>(() => CallWorkflowYamlReader.Read(yaml));
        Assert.Contains("`id` is required", ex.Message);
    }

    [Fact]
    public void Read_MissingInitialStage_Throws()
    {
        var yaml = """
            id: bad
            stages:
              - id: a
                terminal: true
            """;
        var ex = Assert.Throws<CallWorkflowYamlException>(() => CallWorkflowYamlReader.Read(yaml));
        Assert.Contains("`initialStage` is required", ex.Message);
    }

    [Fact]
    public void Read_NoStages_Throws()
    {
        var yaml = """
            id: bad
            initialStage: a
            """;
        var ex = Assert.Throws<CallWorkflowYamlException>(() => CallWorkflowYamlReader.Read(yaml));
        Assert.Contains("`stages`", ex.Message);
    }

    [Fact]
    public void Read_AuthRequirement_BindsToPredicate()
    {
        var yaml = """
            id: auth
            initialStage: welcome
            stages:
              - id: welcome
                transitions:
                  - to: balance
                    requires:
                      - { type: auth, level: multiFactor, message: needs MFA }
              - id: balance
                terminal: true
            """;

        var blueprint = CallWorkflowYamlReader.Read(yaml);
        var welcome = blueprint.Stages[0];
        var transition = welcome.Transitions[0];
        Assert.Single(transition.Requires);
        Assert.Equal(PredicateKind.AuthLevel, transition.Requires[0].Kind);
        Assert.Equal(CallerVerificationLevel.MultiFactor, transition.Requires[0].AuthLevel);
        Assert.Equal("needs MFA", transition.Requires[0].FailureMessage);
    }

    [Fact]
    public void Read_AuthenticatePlan_BindsOrderedSteps()
    {
        var yaml = """
            id: plan
            initialStage: balance
            stages:
              - id: balance
                authenticate:
                  - use: IdentifyLast4
                  - anyOf: [Pin, SmsOtp]
                terminal: true
            """;

        var blueprint = CallWorkflowYamlReader.Read(yaml);
        var plan = blueprint.Stages[0].Authentication;
        Assert.NotNull(plan);
        Assert.Equal(2, plan!.Steps.Count);
        Assert.Equal(["IdentifyLast4"], plan.Steps[0].AuthenticatorNames);
        Assert.False(plan.Steps[0].IsAnyOf);
        Assert.Equal(["Pin", "SmsOtp"], plan.Steps[1].AuthenticatorNames);
        Assert.True(plan.Steps[1].IsAnyOf);
    }

    [Fact]
    public void Read_StateRequirement_AcceptsHasAndEquals()
    {
        var yaml = """
            id: state
            initialStage: a
            stages:
              - id: a
                transitions:
                  - to: b
                    requires:
                      - { type: state, key: verified }
                      - { type: state, key: intent, equals: balance }
              - id: b
                terminal: true
            """;

        var blueprint = CallWorkflowYamlReader.Read(yaml);
        var requires = blueprint.Stages[0].Transitions[0].Requires;
        Assert.Equal(2, requires.Count);
        Assert.Equal(PredicateKind.StateHas, requires[0].Kind);
        Assert.Equal(PredicateKind.StateEquals, requires[1].Kind);
        Assert.Equal("balance", requires[1].ExpectedValue);
    }

    [Fact]
    public void Read_NamedPredicate_BindsToId()
    {
        var yaml = """
            id: named
            initialStage: a
            stages:
              - id: a
                transitions:
                  - to: b
                    requires:
                      - { type: predicate, id: isVip }
              - id: b
                terminal: true
            """;

        var blueprint = CallWorkflowYamlReader.Read(yaml);
        var pred = blueprint.Stages[0].Transitions[0].Requires[0];
        Assert.Equal(PredicateKind.Named, pred.Kind);
        Assert.Equal("isVip", pred.NamedId);
    }

    [Fact]
    public void Read_UnknownPredicateType_AggregatesError()
    {
        var yaml = """
            id: bad
            initialStage: a
            stages:
              - id: a
                transitions:
                  - to: b
                    requires:
                      - { type: weird }
              - id: b
                terminal: true
            """;

        var ex = Assert.Throws<CallWorkflowYamlException>(() => CallWorkflowYamlReader.Read(yaml));
        Assert.Contains("unknown `type` 'weird'", ex.Message);
    }

    [Fact]
    public void Read_StageChannels_BindToBlueprint()
    {
        var yaml = """
            id: channels
            initialStage: welcome
            stages:
              - id: welcome
                realtime:
                  instructions: ["Greet", "Capture intent"]
                  examples: ["Welcome!"]
                  tools: [record-caller-name]
                scripted:
                  ssml: "<speak>hi</speak>"
                  menu:
                    '1': { label: balance, transition: balance }
                nlu:
                  instructions: Classify the caller.
                  intents:
                    - { name: get_balance, description: balance, transition: balance }
                transitions:
                  - to: balance
                    label: balance
              - id: balance
                terminal: true
            """;

        var blueprint = CallWorkflowYamlReader.Read(yaml);
        var welcome = blueprint.Stages[0];

        Assert.NotNull(welcome.Channels.Realtime);
        Assert.Equal(2, welcome.Channels.Realtime!.Instructions.Count);
        Assert.Equal(["record-caller-name"], welcome.Channels.Realtime.ToolNames);

        Assert.NotNull(welcome.Channels.Scripted);
        Assert.Equal("<speak>hi</speak>", welcome.Channels.Scripted!.SsmlPrompt);
        Assert.Single(welcome.Channels.Scripted.MenuOptions);
        Assert.Equal("balance", welcome.Channels.Scripted.MenuOptions['1'].TransitionLabel);

        Assert.NotNull(welcome.Channels.Nlu);
        Assert.Single(welcome.Channels.Nlu!.Intents);
        Assert.Equal("get_balance", welcome.Channels.Nlu.Intents[0].Name);
    }

    [Fact]
    public void Read_SampleAuthenticatedRealtime_CompilesCleanly()
    {
        var samplePath = Path.Combine(
            AppContext.BaseDirectory,
            "IvrWorkflow",
            "Samples",
            "authenticated-realtime-bank.callworkflow.yaml");

        Assert.True(File.Exists(samplePath), $"Sample not found at {samplePath}");

        var blueprint = CallWorkflowYamlReader.Read(File.ReadAllText(samplePath), samplePath);
        var compiled = new WorkflowGraphCompiler().Compile(blueprint);

        Assert.Equal("authenticated-realtime-bank", compiled.Id);
        Assert.Equal("welcome", compiled.InitialStage.Id);

        // Privileged stages carry inline authenticate plans instead of verify-stage detours.
        var balance = compiled.GetStage("balance");
        Assert.NotNull(balance.AuthenticationPlan);
        Assert.Equal(2, balance.AuthenticationPlan!.Steps.Count);
        Assert.Equal(["IdentifyLast4"], balance.AuthenticationPlan.Steps[0].AuthenticatorNames);
        Assert.True(balance.AuthenticationPlan.Steps[1].IsAnyOf);

        Assert.NotNull(compiled.GetStage("activate_card").AuthenticationPlan);
        Assert.True(compiled.GetStage("transfer").Terminal);
        Assert.True(compiled.GetStage("closing").Terminal);
    }
}

using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using global::Agents.AI.ContactCenter.Authentication;
using global::Agents.AI.ContactCenter.IvrWorkflow;
using global::Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using global::Agents.AI.ContactCenter.IvrWorkflow.Compilation;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Navigation;

//public sealed class StagePromptRendererTests
//{
    //private static CompiledCallWorkflow Compile() => new WorkflowGraphCompiler().Compile(new WorkflowBlueprint
    //{
    //    Id = "renderer-test",
    //    BasePrompt = "You are ACME Bank concierge.",
    //    InitialStageId = "welcome",
    //    Stages =
    //    [
    //        new StageBlueprint
    //        {
    //            Id = "welcome",
    //            Goal = "Greet and capture intent.",
    //            Description = "Initial entry.",
    //            ExitCondition = "Caller stated intent.",
    //            Channels = new StageChannelConfig
    //            {
    //                Realtime = new StageRealtimePrompt
    //                {
    //                    Instructions = ["Greet warmly.", "Capture intent."],
    //                    Examples = ["Welcome to ACME Bank."],
    //                },
    //            },
    //            Transitions =
    //            [
    //                new TransitionBlueprint { TargetStageId = "next", Label = "balance", When = "Caller wants balance." },
    //            ],
    //        },
    //        new StageBlueprint { Id = "next", Terminal = true },
    //    ],
    //});

    //[Fact]
    //public void RenderRealtimePrompt_IncludesBasePromptAndStageGoal()
    //{
    //    var workflow = Compile();
    //    var rendered = StagePromptRenderer.RenderRealtimePrompt(workflow, workflow.InitialStage);

    //    Assert.Contains("ACME Bank concierge", rendered);
    //    Assert.Contains("# Current Stage: welcome", rendered);
    //    Assert.Contains("Greet and capture intent", rendered);
    //    Assert.Contains("Exit when: Caller stated intent", rendered);
    //}

    //[Fact]
    //public void RenderRealtimePrompt_IncludesInstructionsAndExamples()
    //{
    //    var workflow = Compile();
    //    var rendered = StagePromptRenderer.RenderRealtimePrompt(workflow, workflow.InitialStage);

    //    Assert.Contains("## Instructions", rendered);
    //    Assert.Contains("- Greet warmly.", rendered);
    //    Assert.Contains("## Example utterances", rendered);
    //    Assert.Contains("Welcome to ACME Bank.", rendered);
    //}

    //[Fact]
    //public void RenderRealtimePrompt_IncludesAvailableTransitionsWithLabels()
    //{
    //    var workflow = Compile();
    //    var rendered = StagePromptRenderer.RenderRealtimePrompt(workflow, workflow.InitialStage);

    //    Assert.Contains("## Available transitions", rendered);
    //    Assert.Contains("`balance`", rendered);
    //    Assert.Contains("Caller wants balance.", rendered);
    //}

    //[Fact]
    //public void RenderRealtimePrompt_OmitsTransitionsSectionOnTerminalStages()
    //{
    //    var workflow = Compile();
    //    var rendered = StagePromptRenderer.RenderRealtimePrompt(workflow, workflow.GetStage("next"));

    //    Assert.DoesNotContain("## Available transitions", rendered);
    //}

    // Decide whether to keep these tests now that the prompt rendering logic has been simplified and moved to the IPromptStateRenderer implementation.

    //[Fact]
    //public void RenderRealtimePrompt_IncludesCollectedState()
    //{
    //    var workflow = Compile();
    //    var state = new IvrWorkflowState();
    //    state.Set("caller_name", "Jordan");

    //    var rendered = StagePromptRenderer.RenderRealtimePrompt(workflow, workflow.InitialStage, state);

    //    Assert.Contains("## Collected information", rendered);
    //    Assert.Contains("caller_name: Jordan", rendered);
    //}

    //[Fact]
    //public void RenderRealtimePrompt_OmitsCallerHint_WhenIdentityIsNull()
    //{
    //    var workflow = Compile();
    //    var rendered = StagePromptRenderer.RenderRealtimePrompt(workflow, workflow.InitialStage);

    //    Assert.DoesNotContain("Caller hint", rendered);
    //}

    //[Fact]
    //public void RenderRealtimePrompt_OmitsCallerHint_WhenIdentityIsAnonymous()
    //{
    //    var workflow = Compile();
    //    var rendered = StagePromptRenderer.RenderRealtimePrompt(
    //        workflow, workflow.InitialStage, state: null, identity: CallerIdentity.Anonymous);

    //    Assert.DoesNotContain("Caller hint", rendered);
    //}

    //[Fact]
    //public void RenderRealtimePrompt_OmitsCallerHint_WhenVerificationLevelIsNone()
    //{
    //    var workflow = Compile();
    //    var identity = MakeIdentity(CallerVerificationLevel.None);

    //    var rendered = StagePromptRenderer.RenderRealtimePrompt(
    //        workflow, workflow.InitialStage, state: null, identity: identity);

    //    Assert.DoesNotContain("Caller hint", rendered);
    //}

    //[Fact]
    //public void RenderRealtimePrompt_IncludesCallerHint_WhenIdentityIsAniMatch()
    //{
    //    var workflow = Compile();
    //    var identity = MakeIdentity(CallerVerificationLevel.AniMatch);

    //    var rendered = StagePromptRenderer.RenderRealtimePrompt(
    //        workflow, workflow.InitialStage, state: null, identity: identity);

    //    Assert.Contains("## Caller hint (unverified)", rendered);
    //    Assert.Contains("- Name: Jordan Reyes", rendered);
    //    Assert.Contains("- Phone: +14123236796", rendered);
    //    Assert.Contains("- Verification level: AniMatch", rendered);
    //    Assert.Contains("- Source: AniLookup", rendered);
    //}

    //[Fact]
    //public void RenderRealtimePrompt_CallerHintGuidance_TellsModelToConfirmAndWarnsAboutSpoofing()
    //{
    //    var workflow = Compile();
    //    var identity = MakeIdentity(CallerVerificationLevel.AniMatch);

    //    var rendered = StagePromptRenderer.RenderRealtimePrompt(
    //        workflow, workflow.InitialStage, state: null, identity: identity);

    //    // The guidance paragraph must call out spoofing/sharing risk and instruct the
    //    // model to confirm rather than assume, and to call record_caller_name once the
    //    // caller has stated their name. These assertions guard against silent regressions
    //    // in the hint wording.
    //    Assert.Contains("spoofed", rendered, StringComparison.OrdinalIgnoreCase);
    //    Assert.Contains("shared", rendered, StringComparison.OrdinalIgnoreCase);
    //    Assert.Contains("confirm", rendered, StringComparison.OrdinalIgnoreCase);
    //    Assert.Contains("record_caller_name", rendered);
    //}

    //[Fact]
    //public void RenderRealtimePrompt_CallerHint_OmitsPhoneLine_WhenIdentityHasNoPhone()
    //{
    //    var workflow = Compile();
    //    var identity = MakeIdentity(CallerVerificationLevel.AniMatch) with { PhoneNumber = null };

    //    var rendered = StagePromptRenderer.RenderRealtimePrompt(
    //        workflow, workflow.InitialStage, state: null, identity: identity);

    //    Assert.Contains("## Caller hint (unverified)", rendered);
    //    Assert.Contains("- Name: Jordan Reyes", rendered);
    //    Assert.DoesNotContain("- Phone:", rendered);
    //}

    //private static CallerIdentity MakeIdentity(CallerVerificationLevel level) => new(
    //    UserId: "cust-001",
    //    DisplayName: "Jordan Reyes",
    //    PhoneNumber: "+14123236796",
    //    Email: null,
    //    EntraObjectId: null,
    //    VerificationLevel: level,
    //    AuthenticatedAt: DateTimeOffset.UtcNow,
    //    AuthenticatedBy: "AniLookup",
    //    Claims: new Dictionary<string, object?>());
//}

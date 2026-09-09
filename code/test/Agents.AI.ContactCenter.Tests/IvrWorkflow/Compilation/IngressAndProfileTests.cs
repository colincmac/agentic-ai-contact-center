using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.IvrWorkflow.Catalog;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Compilation;

public sealed class IngressAndProfileTests
{
    [Fact]
    public void TrustedCalledIdentity_SelectsPinnedRevisionAndLocale()
    {
        var flow = new WorkflowGraphCompiler().Compile(new()
        {
            Id = "billing", Version = 2, InitialStageId = "start", Stages = [new() { Id = "start" }],
        });
        var router = new CallIngressRouter(new CallWorkflowCatalog([flow]), Options.Create(new CallIngressOptions
        {
            Routes = new() { ["trusted-target"] = new() { WorkflowId = "billing@2", Locale = "fr-CA", PreferredTier = AgentTier.IntentNlu } },
        }));
        var request = router.Route(new() { CallId = "call", CallerIdentifier = "caller", CallTargetIdentifier = "trusted-target" });
        Assert.Equal("billing@2", request.WorkflowId);
        Assert.Equal("fr-CA", request.CallContext.Locale);
        Assert.Equal(AgentTier.IntentNlu, request.PreferredTier);
        Assert.Throws<InvalidOperationException>(() => router.Route(request.CallContext with { CallTargetIdentifier = "unknown" }));
    }

    [Fact]
    public void Profile_RequiresMatchingStageAndPhysicalEdgeCapabilities()
    {
        var options = Options.Create(new CallInteractionOptions
        {
            Profiles =
            [
                new() { Name = "recorded", Tier = AgentTier.DtmfOnly, MaxConcurrent = 10, RequiredEdgeCapabilities = EdgeCapabilities.PlayFile | EdgeCapabilities.CollectDtmf },
            ],
        });
        var policy = new CallInteractionPolicy(options);
        var flow = new WorkflowGraphCompiler().Compile(new()
        {
            Id = "test", InitialStageId = "end",
            Stages = [new() { Id = "end", Terminal = true, InteractionProfiles = ["recorded"],
                Channels = new() { Scripted = new() { AudioFile = new("https://example.org/goodbye.wav") } } }],
        });
        Assert.True(policy.Allows(AgentTier.DtmfOnly, flow.InitialStage, EdgeCapabilities.Verb));
        Assert.False(policy.Allows(AgentTier.DtmfOnly, flow.InitialStage, EdgeCapabilities.Streaming));
        Assert.False(policy.Allows(AgentTier.RealtimeVoice, flow.InitialStage, EdgeCapabilities.Streaming));
    }
}

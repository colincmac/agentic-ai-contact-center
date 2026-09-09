using System.Threading.Channels;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Calling.Strategies;

internal static class CallWorkflowEligibility
{
    public static async ValueTask<bool> CheckAsync(IServiceProvider services, AgentTier tier, CompiledStage stage,
        EdgeCapabilities? edge, ChannelWriter<StrategyEvent> events, CancellationToken ct)
    {
        var policy = services.GetService<CallInteractionPolicy>();
        if (policy is null && stage.Blueprint.InteractionProfiles.Count == 0
            || policy?.Allows(tier, stage, edge) == true) { return true; }
        await events.WriteAsync(new StrategyEvent.Faulted(
            $"Interaction profile is not eligible for stage '{stage.Id}'.", null, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
        return false;
    }
}

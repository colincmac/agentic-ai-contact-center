using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Calling.Strategies;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.Configuration;

public sealed class CallInteractionOptions
{
    public const string SectionName = "CallInteraction";
    public List<CallInteractionProfile> Profiles { get; set; } = [];
    public bool AllowMidCallDegradation { get; set; } = true;
}

public sealed class CallInteractionProfile
{
    public string Name { get; set; } = string.Empty;
    public AgentTier Tier { get; set; }
    public bool Enabled { get; set; } = true;
    public int MaxConcurrent { get; set; }
    public EdgeCapabilities RequiredEdgeCapabilities { get; set; } = EdgeCapabilities.Streaming;
}

/// <summary>Capability eligibility is independent of the distributed capacity reservation.</summary>
public sealed class CallInteractionPolicy(IOptions<CallInteractionOptions> options)
{
    public bool Allows(AgentTier tier, CompiledStage stage, EdgeCapabilities? edge)
    {
        if (options.Value.Profiles.Count == 0) { return stage.Blueprint.InteractionProfiles.Count == 0; }
        var profile = options.Value.Profiles.SingleOrDefault(p => p.Tier == tier && p.Enabled);
        return profile is not null
            && (stage.Blueprint.InteractionProfiles.Count == 0
                || stage.Blueprint.InteractionProfiles.Contains(profile.Name, StringComparer.Ordinal))
            && (edge is null || (edge.Value & profile.RequiredEdgeCapabilities) == profile.RequiredEdgeCapabilities)
            && SupportsStage(profile, stage);
    }

    private static bool SupportsStage(CallInteractionProfile profile, CompiledStage stage)
    {
        if (stage.Blueprint.Action is not null) { return true; }
        return profile.Tier switch
        {
            AgentTier.RealtimeVoice => stage.Blueprint.Channels.Realtime is not null,
            AgentTier.IntentNlu => stage.Terminal || stage.AuthenticationPlan is not null || stage.Blueprint.Channels.Nlu?.Intents.Count > 0,
            AgentTier.DtmfOnly => stage.AuthenticationPlan is not null || stage.Blueprint.Channels.Scripted is { } scripted
                && ((profile.RequiredEdgeCapabilities & EdgeCapabilities.PlayFile) == 0 || scripted.AudioFile is not null)
                && (stage.Terminal || scripted.MenuOptions.Count > 0),
            _ => true,
        };
    }
}

internal sealed class CallInteractionOptionsValidator(IServiceProviderIsKeyedService services)
    : IValidateOptions<CallInteractionOptions>
{
    public ValidateOptionsResult Validate(string? name, CallInteractionOptions value)
    {
        var errors = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var tiers = new HashSet<AgentTier>();
        foreach (var profile in value.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Name) || !names.Add(profile.Name)
                || !tiers.Add(profile.Tier) || !Enum.IsDefined(profile.Tier)
                || (profile.Enabled && profile.MaxConcurrent < 1))
            {
                errors.Add("Profiles require unique names/tiers and positive explicitly configured enabled capacities.");
            }
            if (profile.Enabled && !services.IsKeyedService(typeof(ILeafConversationStrategyFactory), profile.Tier))
            {
                errors.Add($"Enabled profile '{profile.Name}' has no registered strategy.");
            }
        }
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

namespace Agents.AI.ContactCenter.Configuration;

/// <summary>
/// Defines the available agent processing tiers, ordered from highest quality/cost
/// to lowest quality/cost. Used for capacity-aware graceful degradation.
/// </summary>
public enum AgentTier
{
    /// <summary>
    /// Tier 0: Full OpenAI Realtime voice model with native audio-to-audio.
    /// Lowest latency, highest quality, most expensive. Requires persistent WebSocket per session.
    /// </summary>
    RealtimeVoice = 0,

    /// <summary>
    /// Tier 1: STT → standard chat completion (e.g., GPT-4o) → TTS pipeline.
    /// Same LLM quality as Tier 0 but with higher latency (~1-2s) and lower cost.
    /// </summary>
    ChatCompletionTts = 1,

    /// <summary>
    /// Tier 2: STT → small language model (e.g., Phi-4-mini) → TTS pipeline.
    /// Lower quality but self-hosted with massive throughput capacity.
    /// </summary>
    SmallLanguageModel = 2,

    /// <summary>
    /// Tier 3: STT → NLU/intent classification (e.g., Azure CLU) → TTS pipeline.
    /// Deterministic intent mapping, no generative AI. Near-zero AI cost.
    /// </summary>
    IntentNlu = 3,

    /// <summary>
    /// Tier 4: Pure DTMF menu navigation. No AI, no speech processing.
    /// No generative AI dependency; admission still requires an explicit capacity.
    /// </summary>
    DtmfOnly = 4
}

/// <summary>
/// Configuration for a single agent tier, defining capacity limits and enablement.
/// </summary>
public sealed class AgentTierConfig
{
    /// <summary>
    /// Maximum number of concurrent sessions allowed for this tier.
    /// Null means unconfigured and prevents admission. Zero temporarily stops admissions.
    /// </summary>
    public int? MaxConcurrent { get; set; }

    /// <summary>
    /// Whether this tier is enabled. Disabled tiers are skipped during resolution.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Optional keyed service name for resolving the AI model/client for this tier.
    /// For example, "gpt-4o" for Tier 1, "phi-4" for Tier 2.
    /// </summary>
    public string? ServiceKey { get; set; }
}

/// <summary>
/// Configuration options for the tiered agent degradation system.
/// Operators can tune these at runtime to control capacity allocation across tiers.
/// </summary>
public sealed class AgentTierOptions
{
    public const string SectionName = "AgentTiers";

    /// <summary>
    /// Per-tier configuration keyed by <see cref="AgentTier"/>.
    /// Missing tiers and tiers without an explicit capacity cannot admit sessions.
    /// Configure limits from the deployed backend's validated capacity before enabling traffic.
    /// </summary>
    public Dictionary<AgentTier, AgentTierConfig> Tiers { get; set; } = new()
    {
        [AgentTier.RealtimeVoice] = new AgentTierConfig { Enabled = true },
        [AgentTier.IntentNlu] = new AgentTierConfig { Enabled = true },
        [AgentTier.DtmfOnly] = new AgentTierConfig { Enabled = true },
    };

    /// <summary>
    /// Ordered list of tiers to try when resolving capacity. The resolver walks this
    /// list and selects the first tier that is enabled and under its capacity limit.
    /// </summary>
    public List<AgentTier> FallbackOrder { get; set; } =
    [
        AgentTier.RealtimeVoice,
        AgentTier.IntentNlu,
        AgentTier.DtmfOnly,
    ];

    /// <summary>
    /// When true, active sessions can be downgraded to a lower tier mid-call
    /// if the current tier's transport fails. When false, only new sessions
    /// are subject to tier selection.
    /// </summary>
    public bool AllowMidCallDegradation { get; set; } = true;

    internal void Validate()
    {
        var failures = new List<string>();
        if (FallbackOrder is null || FallbackOrder.Count == 0)
        {
            failures.Add("AgentTiers:FallbackOrder must contain at least one tier.");
        }
        else
        {
            for (var i = 0; i < FallbackOrder.Count; i++)
            {
                if (!Enum.IsDefined(FallbackOrder[i])
                    || (i > 0 && (int)FallbackOrder[i] <= (int)FallbackOrder[i - 1]))
                {
                    failures.Add("AgentTiers:FallbackOrder must contain known, unique tiers in degradation order.");
                    break;
                }
            }
        }

        if (Tiers is null || Tiers.Any(pair =>
            !Enum.IsDefined(pair.Key) || pair.Value is null || pair.Value.MaxConcurrent is < 0))
        {
            failures.Add("AgentTiers:Tiers must contain known tiers with nonnegative explicit capacities.");
        }

        if (failures.Count != 0)
        {
            throw new Microsoft.Extensions.Options.OptionsValidationException(
                Microsoft.Extensions.Options.Options.DefaultName, typeof(AgentTierOptions), failures);
        }
    }
}

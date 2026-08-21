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
    /// Infinite scale, zero AI dependency.
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
    /// Null means unlimited.
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
    /// Tiers not present in this dictionary use default settings (enabled, unlimited).
    /// </summary>
    public Dictionary<AgentTier, AgentTierConfig> Tiers { get; set; } = new()
    {
        [AgentTier.RealtimeVoice] = new AgentTierConfig { MaxConcurrent = 50_000, Enabled = true },
        [AgentTier.ChatCompletionTts] = new AgentTierConfig { MaxConcurrent = 150_000, Enabled = true },
        [AgentTier.SmallLanguageModel] = new AgentTierConfig { MaxConcurrent = 200_000, Enabled = true },
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
        AgentTier.ChatCompletionTts,
        AgentTier.SmallLanguageModel,
        AgentTier.IntentNlu,
        AgentTier.DtmfOnly,
    ];

    /// <summary>
    /// When true, active sessions can be downgraded to a lower tier mid-call
    /// if the current tier's transport fails. When false, only new sessions
    /// are subject to tier selection.
    /// </summary>
    public bool AllowMidCallDegradation { get; set; } = true;
}

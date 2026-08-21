namespace Extensions.AI.RealtimeVoice.Configuration;

/// <summary>Tool choice configuration.</summary>
public sealed class RealtimeToolChoice
{
    /// <summary>Gets or sets the tool choice type.</summary>
    public RealtimeToolChoiceType Type { get; set; }

    /// <summary>Gets or sets the specific function name (when Type is Function).</summary>
    public string? FunctionName { get; set; }

    /// <summary>Auto tool choice.</summary>
    public static RealtimeToolChoice Auto => new() { Type = RealtimeToolChoiceType.Auto };

    /// <summary>No tools.</summary>
    public static RealtimeToolChoice None => new() { Type = RealtimeToolChoiceType.None };

    /// <summary>Required tool choice.</summary>
    public static RealtimeToolChoice Required => new() { Type = RealtimeToolChoiceType.Required };
}


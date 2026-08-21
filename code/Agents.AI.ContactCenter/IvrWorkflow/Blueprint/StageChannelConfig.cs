namespace Agents.AI.ContactCenter.IvrWorkflow.Blueprint;

/// <summary>Channel-shaped business config for a stage. Code wires executors to render these.</summary>
public sealed class StageChannelConfig
{
    /// <summary>Realtime LLM-facing prompt fragments and tool overrides. Null when the stage isn't rendered by the realtime tier.</summary>
    public StageRealtimePrompt? Realtime { get; init; }

    /// <summary>NLU-facing prompt + intent classifier config. Null when NLU isn't supported for this stage.</summary>
    public StageNluConfig? Nlu { get; init; }

    /// <summary>DTMF / SSML config. Null when the stage cannot be rendered as a touch-tone menu.</summary>
    public StageScriptedConfig? Scripted { get; init; }
}

/// <summary>Realtime-LLM-facing prompt fragments authored in YAML.</summary>
public sealed class StageRealtimePrompt
{
    /// <summary>Numbered instructions appended to the base prompt.</summary>
    public IReadOnlyList<string> Instructions { get; init; } = [];

    /// <summary>Example utterances surfaced to the model.</summary>
    public IReadOnlyList<string> Examples { get; init; } = [];

    /// <summary>Tool names available to the realtime model on top of the workflow's commonTools.</summary>
    public IReadOnlyList<string> ToolNames { get; init; } = [];
}

/// <summary>NLU classifier hints + intent → transition labels.</summary>
public sealed class StageNluConfig
{
    /// <summary>System-prompt fragment for the small-LLM classifier.</summary>
    public string? Instructions { get; init; }

    /// <summary>Intents this stage classifies for. Each maps to a transition label.</summary>
    public IReadOnlyList<NluIntent> Intents { get; init; } = [];
}

/// <summary>Single intent the NLU classifier can produce.</summary>
public sealed record NluIntent(string Name, string Description, string TransitionLabel);

/// <summary>DTMF + SSML config for a touch-tone-rendered stage.</summary>
public sealed class StageScriptedConfig
{
    /// <summary>SSML the TTS engine renders.</summary>
    public string? SsmlPrompt { get; init; }

    /// <summary>Digit → transition-label mapping. Pressing a digit invokes the named transition.</summary>
    public IReadOnlyDictionary<char, ScriptedMenuOption> MenuOptions { get; init; } =
        new Dictionary<char, ScriptedMenuOption>();
}

/// <summary>A single DTMF menu option.</summary>
public sealed record ScriptedMenuOption(string Label, string TransitionLabel);

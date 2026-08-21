using YamlDotNet.Serialization;

namespace Agents.AI.ContactCenter.IvrWorkflow.Loading;

/// <summary>
/// Root YAML document for the new <see cref="Blueprint.WorkflowBlueprint"/> schema. Plain
/// POCO bound by YamlDotNet using camelCase. Validation + structural checks happen later in
/// <see cref="CallWorkflowYamlReader.Read(string, string?)"/> + the
/// <see cref="Compilation.WorkflowGraphCompiler"/>.
/// </summary>
/// <remarks>
/// Distinct from the legacy <see cref="Definition.IvrWorkflowDocument"/> — that schema
/// retains imports/subflows/authResolvers; this one does not. Each workflow is self-contained;
/// authentication is expressed as an inline <c>authenticate:</c> plan on the stage that gates
/// the privileged action.
/// </remarks>
public sealed class CallWorkflowDocument
{
    public string? Id { get; set; }
    public int Version { get; set; } = 1;
    public string? Description { get; set; }

    [YamlMember(Alias = "basePrompt")]
    public string? BasePrompt { get; set; }

    [YamlMember(Alias = "commonTools")]
    public List<string>? CommonTools { get; set; }

    [YamlMember(Alias = "initialStage")]
    public string? InitialStage { get; set; }

    public List<CallWorkflowStageDocument>? Stages { get; set; }
}

public sealed class CallWorkflowStageDocument
{
    public string? Id { get; set; }
    public string? Goal { get; set; }
    public string? Description { get; set; }
    public bool Terminal { get; set; }

    [YamlMember(Alias = "terminalOutcome")]
    public string? TerminalOutcome { get; set; }

    [YamlMember(Alias = "exitWhen")]
    public string? ExitWhen { get; set; }

    public List<string>? Tools { get; set; }

    /// <summary>Inline authentication plan: ordered credential steps satisfied on stage entry.</summary>
    public List<CallWorkflowAuthStepDocument>? Authenticate { get; set; }

    public CallWorkflowRealtimeDocument? Realtime { get; set; }
    public CallWorkflowNluDocument? Nlu { get; set; }
    public CallWorkflowScriptedDocument? Scripted { get; set; }

    public List<CallWorkflowTransitionDocument>? Transitions { get; set; }
}

/// <summary>
/// One step in a stage's <c>authenticate:</c> plan. Provide either <c>use</c> (a single
/// authenticator) or <c>anyOf</c> (a list the caller may choose between).
/// </summary>
public sealed class CallWorkflowAuthStepDocument
{
    public string? Use { get; set; }

    [YamlMember(Alias = "anyOf")]
    public List<string>? AnyOf { get; set; }
}

public sealed class CallWorkflowRealtimeDocument
{
    public List<string>? Instructions { get; set; }
    public List<string>? Examples { get; set; }
    public List<string>? Tools { get; set; }
}

public sealed class CallWorkflowNluDocument
{
    public string? Instructions { get; set; }
    public List<CallWorkflowIntentDocument>? Intents { get; set; }
}

public sealed class CallWorkflowIntentDocument
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Transition { get; set; }
}

public sealed class CallWorkflowScriptedDocument
{
    public string? Ssml { get; set; }

    public Dictionary<string, CallWorkflowMenuOptionDocument>? Menu { get; set; }
}

public sealed class CallWorkflowMenuOptionDocument
{
    public string? Label { get; set; }
    public string? Transition { get; set; }
}

public sealed class CallWorkflowTransitionDocument
{
    [YamlMember(Alias = "to")]
    public string? Target { get; set; }

    public string? Label { get; set; }
    public string? When { get; set; }

    public List<CallWorkflowRequirementDocument>? Requires { get; set; }
}

public sealed class CallWorkflowTransitionIntentDocument
{
    public List<string>? Intents { get; set; }

    public string? DtmfDigit { get; set; }

    public string? Description { get; set; }
}

/// <summary>
/// Predicate reference. <c>type</c> selects the kind; remaining fields are the operands.
/// Supported types: <c>auth</c> (with <c>level</c>), <c>state</c> (with <c>key</c> and
/// optional <c>equals</c>), <c>predicate</c> (with <c>id</c>).
/// </summary>
public sealed class CallWorkflowRequirementDocument
{
    public string? Type { get; set; }
    public string? Level { get; set; }
    public string? Key { get; set; }

    [YamlMember(Alias = "equals")]
    public object? EqualsValue { get; set; }

    public string? Id { get; set; }
    public string? Message { get; set; }
}

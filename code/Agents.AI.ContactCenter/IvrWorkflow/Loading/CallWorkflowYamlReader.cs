using System.Text;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Agents.AI.ContactCenter.IvrWorkflow.Loading;

/// <summary>
/// Reads new-model workflow YAML into a <see cref="WorkflowBlueprint"/>. Validates that
/// required fields are present and that operand fields match each predicate <c>type</c>;
/// further structural validation (unknown transition targets, missing initial stage, etc.)
/// happens in <see cref="Compilation.WorkflowGraphCompiler"/>.
/// </summary>
public static class CallWorkflowYamlReader
{
    private static readonly IDeserializer deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>Parse and materialize a blueprint from YAML text.</summary>
    public static WorkflowBlueprint Read(string yaml, string? sourceName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yaml);

        CallWorkflowDocument doc;
        try
        {
            doc = deserializer.Deserialize<CallWorkflowDocument>(yaml)
                ?? throw new CallWorkflowYamlException(
                    $"YAML document from '{sourceName ?? "<unknown>"}' deserialized to null.");
        }
        catch (YamlException ex)
        {
            var sb = new StringBuilder()
                .Append("Failed to parse call workflow YAML");
            if (!string.IsNullOrWhiteSpace(sourceName))
            {
                sb.Append(" from '").Append(sourceName).Append('\'');
            }
            sb.Append(" at ").Append(ex.Start).Append(": ").Append(ex.Message);
            throw new CallWorkflowYamlException(sb.ToString(), ex);
        }

        return Build(doc, sourceName);
    }

    private static WorkflowBlueprint Build(CallWorkflowDocument doc, string? sourceName)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(doc.Id))
        {
            errors.Add("`id` is required at the document root.");
        }
        if (string.IsNullOrWhiteSpace(doc.InitialStage))
        {
            errors.Add("`initialStage` is required at the document root.");
        }
        if (doc.Stages is null or { Count: 0 })
        {
            errors.Add("`stages` must contain at least one stage.");
        }

        if (errors.Count > 0)
        {
            throw new CallWorkflowYamlException(BuildErrorMessage(sourceName, doc.Id, errors));
        }

        var stages = new List<StageBlueprint>(doc.Stages!.Count);
        foreach (var stageDoc in doc.Stages)
        {
            stages.Add(BuildStage(stageDoc, errors));
        }

        if (errors.Count > 0)
        {
            throw new CallWorkflowYamlException(BuildErrorMessage(sourceName, doc.Id, errors));
        }

        return new WorkflowBlueprint
        {
            Id = doc.Id!,
            Version = doc.Version > 0 ? doc.Version : 1,
            Description = doc.Description,
            BasePrompt = doc.BasePrompt,
            CommonToolNames = doc.CommonTools is { Count: > 0 } ct ? [.. ct] : [],
            InitialStageId = doc.InitialStage!,
            Stages = stages,
        };
    }

    private static StageBlueprint BuildStage(CallWorkflowStageDocument stage, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(stage.Id))
        {
            errors.Add("Every stage requires an `id`.");
        }

        var terminalOutcome = ParseTerminalOutcome(stage.TerminalOutcome, errors, stage.Id);

        var transitions = new List<TransitionBlueprint>();
        if (stage.Transitions is { Count: > 0 })
        {
            foreach (var t in stage.Transitions)
            {
                transitions.Add(BuildTransition(t, stage.Id, errors));
            }
        }

        return new StageBlueprint
        {
            Id = stage.Id ?? string.Empty,
            Goal = stage.Goal,
            Description = stage.Description,
            Terminal = stage.Terminal,
            TerminalOutcome = terminalOutcome,
            ExitCondition = stage.ExitWhen,
            ToolNames = stage.Tools is { Count: > 0 } st ? [.. st] : [],
            Channels = BuildChannels(stage),
            Authentication = BuildAuthenticationPlan(stage, errors),
            Transitions = transitions,
        };
    }

    private static AuthenticationPlanBlueprint? BuildAuthenticationPlan(
        CallWorkflowStageDocument stage,
        List<string> errors)
    {
        if (stage.Authenticate is not { Count: > 0 } steps)
        {
            return null;
        }

        var groups = new List<AuthStepGroup>(steps.Count);
        foreach (var step in steps)
        {
            if (step.AnyOf is { Count: > 0 } anyOf)
            {
                var names = anyOf.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
                if (names.Count == 0)
                {
                    errors.Add($"Stage '{stage.Id}' authenticate step has an empty `anyOf`.");
                    continue;
                }
                groups.Add(new AuthStepGroup(names));
            }
            else if (!string.IsNullOrWhiteSpace(step.Use))
            {
                groups.Add(new AuthStepGroup([step.Use.Trim()]));
            }
            else
            {
                errors.Add($"Stage '{stage.Id}' authenticate step must set either `use` or `anyOf`.");
            }
        }

        return groups.Count > 0 ? new AuthenticationPlanBlueprint { Steps = groups } : null;
    }

    private static StageChannelConfig BuildChannels(CallWorkflowStageDocument stage)
    {
        StageRealtimePrompt? realtime = null;
        if (stage.Realtime is { } rt)
        {
            realtime = new StageRealtimePrompt
            {
                Instructions = rt.Instructions is { Count: > 0 } i ? [.. i] : [],
                Examples = rt.Examples is { Count: > 0 } e ? [.. e] : [],
                ToolNames = rt.Tools is { Count: > 0 } t ? [.. t] : [],
            };
        }

        StageNluConfig? nlu = null;
        if (stage.Nlu is { } nluDoc)
        {
            var intents = new List<NluIntent>();
            if (nluDoc.Intents is { Count: > 0 })
            {
                foreach (var intent in nluDoc.Intents)
                {
                    if (string.IsNullOrEmpty(intent.Name)
                        || string.IsNullOrEmpty(intent.Transition))
                    {
                        continue;
                    }
                    intents.Add(new NluIntent(
                        intent.Name!,
                        intent.Description ?? string.Empty,
                        intent.Transition!));
                }
            }
            nlu = new StageNluConfig
            {
                Instructions = nluDoc.Instructions,
                Intents = intents,
            };
        }

        StageScriptedConfig? scripted = null;
        if (stage.Scripted is { } sc)
        {
            var menu = new Dictionary<char, ScriptedMenuOption>();
            if (sc.Menu is { Count: > 0 })
            {
                foreach (var (digit, option) in sc.Menu)
                {
                    if (digit.Length != 1
                        || string.IsNullOrEmpty(option.Transition))
                    {
                        continue;
                    }
                    menu[digit[0]] = new ScriptedMenuOption(
                        option.Label ?? digit,
                        option.Transition!);
                }
            }
            scripted = new StageScriptedConfig
            {
                SsmlPrompt = sc.Ssml,
                MenuOptions = menu,
            };
        }

        return new StageChannelConfig
        {
            Realtime = realtime,
            Nlu = nlu,
            Scripted = scripted,
        };
    }

    private static TransitionBlueprint BuildTransition(
        CallWorkflowTransitionDocument t,
        string? stageId,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(t.Target))
        {
            errors.Add($"Stage '{stageId}' transition is missing `to`.");
        }

        var requires = new List<PredicateRef>();
        if (t.Requires is { Count: > 0 })
        {
            foreach (var req in t.Requires)
            {
                if (TryBuildPredicate(req, stageId, errors) is { } predicate)
                {
                    requires.Add(predicate);
                }
            }
        }

        return new TransitionBlueprint
        {
            TargetStageId = t.Target ?? string.Empty,
            Label = t.Label,
            When = t.When,
            Requires = requires,
        };
    }

    private static PredicateRef? TryBuildPredicate(
        CallWorkflowRequirementDocument req,
        string? stageId,
        List<string> errors)
    {
        var kind = req.Type?.Trim().ToLowerInvariant();
        switch (kind)
        {
            case "auth":
                if (!Enum.TryParse<CallerVerificationLevel>(req.Level, ignoreCase: true, out var level))
                {
                    errors.Add($"Stage '{stageId}' auth requirement has invalid `level`: '{req.Level}'.");
                    return null;
                }
                return PredicateRef.AuthVerificationLevel(level, req.Message);

            case "state":
                if (string.IsNullOrWhiteSpace(req.Key))
                {
                    errors.Add($"Stage '{stageId}' state requirement is missing `key`.");
                    return null;
                }
                return req.EqualsValue is null
                    ? PredicateRef.StateHas(req.Key!, req.Message)
                    : PredicateRef.StateEquals(req.Key!, req.EqualsValue, req.Message);

            case "predicate":
                if (string.IsNullOrWhiteSpace(req.Id))
                {
                    errors.Add($"Stage '{stageId}' predicate requirement is missing `id`.");
                    return null;
                }
                return PredicateRef.Named(req.Id!, req.Message);

            default:
                errors.Add($"Stage '{stageId}' requirement has unknown `type` '{req.Type}'.");
                return null;
        }
    }

    private static BlueprintTerminalOutcome ParseTerminalOutcome(
        string? raw,
        List<string> errors,
        string? stageId)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return BlueprintTerminalOutcome.None;
        }

        if (Enum.TryParse<BlueprintTerminalOutcome>(raw, ignoreCase: true, out var outcome))
        {
            return outcome;
        }

        errors.Add($"Stage '{stageId}' has invalid `terminalOutcome`: '{raw}'.");
        return BlueprintTerminalOutcome.None;
    }

    private static string BuildErrorMessage(string? source, string? id, IReadOnlyList<string> errors)
    {
        var sb = new StringBuilder()
            .Append("Call workflow YAML");
        if (!string.IsNullOrWhiteSpace(source)) { sb.Append(" '").Append(source).Append('\''); }
        if (!string.IsNullOrWhiteSpace(id)) { sb.Append(" (id='").Append(id).Append("')"); }
        sb.Append(" failed validation with ").Append(errors.Count).AppendLine(" error(s):");
        foreach (var err in errors)
        {
            sb.Append("  - ").AppendLine(err);
        }
        return sb.ToString();
    }
}

/// <summary>Thrown when a new-model call workflow YAML document fails to parse or validate.</summary>
public sealed class CallWorkflowYamlException : Exception
{
    public CallWorkflowYamlException(string message) : base(message) { }
    public CallWorkflowYamlException(string message, Exception inner) : base(message, inner) { }
}

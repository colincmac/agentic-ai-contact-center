using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Transactions;

namespace Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

/// <summary>
/// Builds a Realtime AI system prompt from a <see cref="RealtimePrompt"/>.
/// </summary>
public class RealtimeAIPromptTemplate
{
    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>
    /// Renders the prompt to a formatted system instruction string.
    /// </summary>
    public static string Render(RealtimePrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var sb = new StringBuilder();

        RenderRoleAndObjective(sb, prompt.Role);
        RenderPersonalityAndTone(sb, prompt.Personality);
        RenderContext(sb, prompt.Context);
        RenderReferencePronunciations(sb, prompt.ReferencePronunciations);
        RenderTools(sb, prompt.Tools);
        RenderInstructions(sb, prompt.Instructions);
        RenderConversationFlow(sb, prompt.ConversationFlow);
        RenderSamplePhrases(sb, prompt.Phrases);
        RenderSafetyAndEscalation(sb, prompt.Safety);

        return sb.ToString().TrimEnd();
    }

    private static void RenderRoleAndObjective(StringBuilder sb, RoleAndObjective? role)
    {
        if (role is null)
        {
            return;
        }

        sb.AppendLine("# Role & Objective");
        sb.AppendLine($"- {role.Identity}");
        sb.AppendLine($"- {role.Objective}");

        if (!string.IsNullOrWhiteSpace(role.CharacterTraits))
        {
            sb.AppendLine($"- {role.CharacterTraits}");
        }

        sb.AppendLine();
    }

    private static void RenderPersonalityAndTone(StringBuilder sb, PersonalityAndTone? personality)
    {
        if (personality is null)
        {
            return;
        }

        sb.AppendLine("# Personality & Tone");
        sb.AppendLine("## Personality");
        sb.AppendLine($"- {personality.Personality}");
        sb.AppendLine();

        sb.AppendLine("## Tone");
        sb.AppendLine($"- {personality.Tone}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(personality.Length))
        {
            sb.AppendLine("## Length");
            sb.AppendLine($"- {personality.Length}");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(personality.Pacing))
        {
            sb.AppendLine("## Pacing");
            sb.AppendLine($"- {personality.Pacing}");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(personality.Enthusiasm))
        {
            sb.AppendLine("## Level of Enthusiasm");
            sb.AppendLine($"- {personality.Enthusiasm}");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(personality.Formality))
        {
            sb.AppendLine("## Level of Formality");
            sb.AppendLine($"- {personality.Formality}");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(personality.Emotion))
        {
            sb.AppendLine("## Level of Emotion");
            sb.AppendLine($"- {personality.Emotion}");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(personality.FillerWords))
        {
            sb.AppendLine("## Filler Words");
            sb.AppendLine($"- {personality.FillerWords}");
            sb.AppendLine();
        }

        if (personality.Language is not null)
        {
            sb.AppendLine("## Language");
            sb.AppendLine($"- The conversation will be only in {personality.Language.PrimaryLanguage}.");

            if (!personality.Language.AllowOtherLanguages)
            {
                sb.AppendLine("- Do not respond in any other language even if the user asks.");

                if (!string.IsNullOrWhiteSpace(personality.Language.NonPrimaryLanguageResponse))
                {
                    sb.AppendLine($"- {personality.Language.NonPrimaryLanguageResponse}");
                }
            }

            if (!string.IsNullOrWhiteSpace(personality.Language.CodeSwitchingRules))
            {
                sb.AppendLine($"- {personality.Language.CodeSwitchingRules}");
            }

            sb.AppendLine();
        }

        if (personality.EnforceVariety)
        {
            sb.AppendLine("## Variety");
            sb.AppendLine("- Do not repeat the same sentence twice.");
            sb.AppendLine("- Vary your responses so it doesn't sound robotic.");
            sb.AppendLine();
        }
    }

    private static void RenderContext(StringBuilder sb, string? context)
    {
        if (string.IsNullOrWhiteSpace(context))
        {
            return;
        }

        sb.AppendLine("# Context");
        sb.AppendLine(context);
        sb.AppendLine();
    }

    private static void RenderReferencePronunciations(StringBuilder sb, IReadOnlyList<ReferencePronunciation>? pronunciations)
    {
        if (pronunciations is null || pronunciations.Count == 0)
        {
            return;
        }

        sb.AppendLine("# Reference Pronunciations");
        sb.AppendLine("When voicing these words, use the respective pronunciations:");

        foreach (var p in pronunciations)
        {
            sb.AppendLine($"- Pronounce \"{p.Word}\" as \"{p.Pronunciation}.\"");
        }

        sb.AppendLine();
    }

    private static void RenderTools(StringBuilder sb, ToolConfiguration? tools)
    {
        if (tools is null)
        {
            return;
        }

        sb.AppendLine("# Tools");

        if (!string.IsNullOrWhiteSpace(tools.GlobalPreamble))
        {
            sb.AppendLine($"- {tools.GlobalPreamble}");
        }

        if (!tools.RequireConfirmation)
        {
            sb.AppendLine("- When calling a tool, do not ask for any user confirmation unless specified below in specific tool instructions or in the description of the tool. Be proactive.");
        }

        sb.AppendLine();

        if (tools.ToolRules is not null)
        {
            foreach (var rule in tools.ToolRules)
            {
                var behaviorLabel = rule.Behavior switch
                {
                    ToolBehavior.Proactive => " — PROACTIVE",
                    ToolBehavior.ConfirmationFirst => " — CONFIRMATION FIRST",
                    ToolBehavior.Preambles => " — PREAMBLES",
                    _ => string.Empty
                };

                sb.AppendLine($"## {rule.Name}{behaviorLabel}");
                sb.AppendLine($"Use when: {rule.UseWhen}");

                if (!string.IsNullOrWhiteSpace(rule.DoNotUseWhen))
                {
                    sb.AppendLine($"Do NOT use when: {rule.DoNotUseWhen}");
                }

                if (rule.PreamblePhrases is { Count: > 0 })
                {
                    sb.AppendLine("Preamble sample phrases:");

                    foreach (var phrase in rule.PreamblePhrases)
                    {
                        sb.AppendLine($"- {phrase}");
                    }
                }

                if (!string.IsNullOrWhiteSpace(rule.ConfirmationPhrase))
                {
                    sb.AppendLine($"Confirmation phrase: \"{rule.ConfirmationPhrase}\"");
                }

                sb.AppendLine();
            }
        }

        if (tools.SupervisorTool is not null)
        {
            RenderSupervisorTool(sb, tools.SupervisorTool);
        }
    }

    private static void RenderSupervisorTool(StringBuilder sb, SupervisorToolConfig supervisor)
    {
        sb.AppendLine("## Supervisor Tool");
        sb.AppendLine($"Name: {supervisor.ToolName}(relevantContextFromLastUserMessage: string)");
        sb.AppendLine();
        sb.AppendLine("When to call:");

        foreach (var condition in supervisor.CallWhen)
        {
            sb.AppendLine($"- {condition}");
        }

        sb.AppendLine();
        sb.AppendLine("When not to call:");

        foreach (var condition in supervisor.DoNotCallWhen)
        {
            sb.AppendLine($"- {condition}");
        }

        if (supervisor.ApprovedFillers is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Usage rules:");
            sb.AppendLine("1) Say a neutral filler phrase to the user, then immediately call the tool.");
            sb.Append("   Approved fillers: ");
            sb.AppendLine(string.Join(", ", supervisor.ApprovedFillers.Select(f => $"\"{f}\"")));
            sb.AppendLine("2) Do not mention the \"Supervisor\" when responding with filler phrase.");
        }

        if (!string.IsNullOrWhiteSpace(supervisor.RephraseInstructions))
        {
            sb.AppendLine();
            sb.AppendLine("### Rephrase Supervisor");
            sb.AppendLine(supervisor.RephraseInstructions);
        }

        sb.AppendLine();
    }

    private static void RenderInstructions(StringBuilder sb, InstructionRules? instructions)
    {
        if (instructions is null)
        {
            return;
        }

        sb.AppendLine("# Instructions/Rules");

        if (instructions.GeneralRules is { Count: > 0 })
        {
            foreach (var rule in instructions.GeneralRules)
            {
                sb.AppendLine($"- {rule}");
            }
        }

        if (!string.IsNullOrWhiteSpace(instructions.AlphanumericPronunciation))
        {
            sb.AppendLine($"- {instructions.AlphanumericPronunciation}");
        }

        if (instructions.SuppressSounds is { Count: > 0 })
        {
            foreach (var sound in instructions.SuppressSounds)
            {
                sb.AppendLine($"- {sound}");
            }
        }

        if (instructions.UnclearAudio is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Unclear audio");
            sb.AppendLine("- Always respond in the same language the user is speaking in, if unintelligible.");
            sb.AppendLine("- Only respond to clear audio or text.");

            if (instructions.UnclearAudio.AskForClarification)
            {
                sb.AppendLine("- If the user's audio is not clear (e.g. ambiguous input/background noise/silent/unintelligible) or if you did not fully hear or understand the user, ask for clarification.");
            }

            if (instructions.UnclearAudio.RepeatLastQuestion)
            {
                sb.AppendLine("- If audio is unclear, repeat the last question.");
            }

            if (instructions.UnclearAudio.ClarificationPhrases is { Count: > 0 })
            {
                sb.AppendLine("Sample clarification phrases:");

                foreach (var phrase in instructions.UnclearAudio.ClarificationPhrases)
                {
                    sb.AppendLine($"- \"{phrase}\"");
                }
            }
        }

        sb.AppendLine();
    }

    private static void RenderConversationFlow(StringBuilder sb, IReadOnlyList<ConversationState>? states)
    {
        if (states is null || states.Count == 0)
        {
            return;
        }

        sb.AppendLine("# Conversation Flow");

        foreach (var state in states)
        {
            sb.AppendLine($"## {state.Id}");

            if (!string.IsNullOrWhiteSpace(state.Goal))
            {
                sb.AppendLine($"Goal: {state.Goal}");
            }

            sb.AppendLine($"Description: {state.Description}");
            sb.AppendLine("How to respond:");

            foreach (var instruction in state.Instructions)
            {
                sb.AppendLine($"- {instruction}");
            }

            if (state.Examples is { Count: > 0 })
            {
                sb.AppendLine("Sample phrases (do not always repeat the same phrases, vary your responses):");

                foreach (var example in state.Examples)
                {
                    sb.AppendLine($"- \"{example}\"");
                }
            }

            if (!string.IsNullOrWhiteSpace(state.ExitWhen))
            {
                sb.AppendLine($"Exit when: {state.ExitWhen}");
            }

            sb.AppendLine($"Valid Next Steps (formatted `<step_name>: <condition>`)");

            if (state.Transitions is { Count: > 0 })
            {
                foreach (var transition in state.Transitions)
                {
                    sb.AppendLine($"→ {transition.NextStep}: {transition.Condition}");
                }
            }
            else
            {
                sb.AppendLine($"→ No additional steps. End the call politely.");
            }

            sb.AppendLine();
        }
    }

    private static void RenderSamplePhrases(StringBuilder sb, SamplePhrases? phrases)
    {
        if (phrases is null)
        {
            return;
        }

        sb.AppendLine("# Sample Phrases");
        sb.AppendLine("- Below are sample examples that you should use for inspiration. DO NOT ALWAYS USE THESE EXAMPLES, VARY YOUR RESPONSES.");
        sb.AppendLine();

        if (phrases.Acknowledgements is { Count: > 0 })
        {
            sb.AppendLine($"Acknowledgements: {string.Join(" ", phrases.Acknowledgements.Select(p => $"\"{p}\""))}");
        }

        if (phrases.Clarifiers is { Count: > 0 })
        {
            sb.AppendLine($"Clarifiers: {string.Join(" ", phrases.Clarifiers.Select(p => $"\"{p}\""))}");
        }

        if (phrases.Bridges is { Count: > 0 })
        {
            sb.AppendLine($"Bridges: {string.Join(" ", phrases.Bridges.Select(p => $"\"{p}\""))}");
        }

        if (phrases.Empathy is { Count: > 0 })
        {
            sb.AppendLine($"Empathy (brief): {string.Join(" ", phrases.Empathy.Select(p => $"\"{p}\""))}");
        }

        if (phrases.Closers is { Count: > 0 })
        {
            sb.AppendLine($"Closers: {string.Join(" ", phrases.Closers.Select(p => $"\"{p}\""))}");
        }

        sb.AppendLine();
    }

    private static void RenderSafetyAndEscalation(StringBuilder sb, SafetyAndEscalation? safety)
    {
        if (safety is null)
        {
            return;
        }

        sb.AppendLine("# Safety & Escalation");
        sb.AppendLine("When to escalate (no extra troubleshooting):");

        foreach (var condition in safety.EscalateWhen)
        {
            sb.AppendLine($"- {condition}");
        }

        if (safety.MaxFailedToolAttempts.HasValue)
        {
            sb.AppendLine($"- {safety.MaxFailedToolAttempts} failed tool attempts on the same task.");
        }

        if (safety.MaxNoMatchEvents.HasValue)
        {
            sb.AppendLine($"- {safety.MaxNoMatchEvents} consecutive no-match/no-input events.");
        }

        if (safety.EscalationPhrases is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("What to say when escalating (MANDATORY):");

            foreach (var phrase in safety.EscalationPhrases)
            {
                sb.AppendLine($"- \"{phrase}\"");
            }
        }

        if (safety.EscalationExamples is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Examples that would require escalation:");

            foreach (var example in safety.EscalationExamples)
            {
                sb.AppendLine($"- \"{example}\"");
            }
        }

        sb.AppendLine();
    }

    /// <summary>
    /// Renders the conversation flow as JSON for state machine implementations.
    /// </summary>
    public static string RenderConversationFlowAsJson(IReadOnlyList<ConversationState> states)
    {
        return JsonSerializer.Serialize(states, jsonOptions);
    }
}

using Microsoft.Extensions.AI;

namespace Agents.AI.ContactCenter.Agents.IntentAgent;

/// <summary>
/// Per-classification context passed by <see cref="IvrIntentAgent"/> into its internal
/// classification pipeline. Bundles the utterance with everything the agent needs to
/// build the chat prompt and dispatch any resulting tool call locally.
/// </summary>
/// <param name="Utterance">The caller utterance to classify.</param>
/// <param name="ValidIntents">The closed set of candidate intents the SLM must choose from.</param>
/// <param name="Tools">Tools the agent may dispatch locally after classification.</param>
/// <param name="IntentToolMap">Optional explicit intent-name → tool-name map.</param>
public sealed record IvrIntentClassificationContext(
    string Utterance,
    IReadOnlyList<string> ValidIntents,
    IReadOnlyList<AITool> Tools,
    IReadOnlyDictionary<string, string>? IntentToolMap);

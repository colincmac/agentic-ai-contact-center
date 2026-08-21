using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agents.AI.ContactCenter.Agents.IntentAgent;

/// <summary>
/// Configuration for <see cref="IvrIntentAgent"/>.
/// </summary>
/// <remarks>
/// The agent uses an <see cref="IChatClient"/> backed by a small instruct-style model
/// (for example <c>phi-4-mini-instruct</c>) to perform intent classification. Because
/// the SLM cannot invoke tools, the agent itself dispatches tools locally based on the
/// classified intent.
/// </remarks>
public sealed class IvrIntentAgentOptions
{
    /// <summary>Optional stable identifier exposed via <see cref="AIAgent.Id"/>.</summary>
    public string? Id { get; set; }

    /// <summary>Display name exposed via <see cref="AIAgent.Name"/>. Defaults to <c>IntentAgent</c>.</summary>
    public string Name { get; set; } = "IntentAgent";

    /// <summary>Free-form description exposed via <see cref="AIAgent.Description"/>.</summary>
    public string? Description { get; set; }
        = "Routes caller utterances to IVR intents via an IChatClient-backed classifier.";

    /// <summary>
    /// Default candidate intents used when a caller invokes <see cref="AIAgent.RunAsync"/>
    /// without supplying an <see cref="IvrIntentRunOptions"/> override. Optional.
    /// </summary>
    public IReadOnlyList<string>? DefaultIntents { get; set; }

    /// <summary>
    /// System prompt prepended to every classification call. The agent appends the
    /// candidate intent list and any per-intent examples after this prompt.
    /// </summary>
    public string SystemPrompt { get; set; } =
        "You are an intent classifier for a contact-center IVR. " +
        "Given a caller utterance and a closed set of candidate intents, " +
        "respond with a single JSON object — no prose, no markdown, no code fences — " +
        "with the schema: " +
        "{\"intent\": <one of the candidates or \"none\">, " +
        "\"confidence\": <number between 0 and 1>, " +
        "\"entities\": <flat object of string → string, omit if empty>}. " +
        "If the utterance does not clearly match any candidate, return intent=\"none\".";

    /// <summary>
    /// Optional per-intent example utterances appended as few-shot context. Keys must
    /// match candidate intent names; unknown keys are ignored at classify time.
    /// </summary>
    public IDictionary<string, IReadOnlyList<string>> IntentExamples { get; set; }
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Minimum confidence required to return an intent. Classifications below this
    /// threshold are coerced to <see cref="IntentResult.None"/>. Defaults to 0 (no floor).
    /// </summary>
    public double MinimumConfidence { get; set; }

    /// <summary>
    /// Temperature passed to the chat client. Lower values yield more deterministic
    /// routing decisions. Defaults to 0.
    /// </summary>
    public float? Temperature { get; set; } = 0f;

    /// <summary>
    /// Max output tokens passed to the chat client. Defaults to 128 — enough for the
    /// JSON envelope plus a handful of small entities.
    /// </summary>
    public int? MaxOutputTokens { get; set; } = 128;

    /// <summary>
    /// Language tag forwarded into the system prompt to give the model a hint when
    /// callers speak in non-English locales. Defaults to <c>en-US</c>.
    /// </summary>
    public string Language { get; set; } = "en-US";

    /// <summary>
    /// When true, asks the underlying chat client to respond in JSON via
    /// <see cref="ChatResponseFormat.Json"/>. Some backends ignore the hint; the agent
    /// still parses the response leniently regardless. Defaults to true.
    /// </summary>
    public bool RequestJsonResponseFormat { get; set; } = true;

    /// <summary>
    /// Tools the agent can invoke locally when an intent is recognized. The SLM never
    /// sees these tools; the agent dispatches them by mapping the recognized intent
    /// to a tool via <see cref="DefaultIntentToolMap"/> (preferred) or, failing that,
    /// by matching the intent name against <see cref="AITool.Name"/>.
    /// </summary>
    public IReadOnlyList<AITool>? DefaultTools { get; set; }

    /// <summary>
    /// Explicit intent-name → tool-name map used to dispatch tools after classification.
    /// Takes precedence over name-based matching against <see cref="DefaultTools"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? DefaultIntentToolMap { get; set; }
}

/// <summary>
/// Per-run override for <see cref="IvrIntentAgent"/> that lets callers specify the
/// candidate intent set, tool catalog, and intent-tool map for a single
/// <see cref="AIAgent.RunAsync"/> invocation.
/// </summary>
public sealed class IvrIntentRunOptions : AgentRunOptions
{
    /// <summary>Candidate intents this run is restricted to.</summary>
    public IReadOnlyList<string>? ValidIntents { get; set; }

    /// <summary>
    /// Tools available to this run for local dispatch. Overrides
    /// <see cref="IvrIntentAgentOptions.DefaultTools"/> when supplied.
    /// </summary>
    public IReadOnlyList<AITool>? Tools { get; set; }

    /// <summary>
    /// Explicit intent-name → tool-name map for this run. Overrides
    /// <see cref="IvrIntentAgentOptions.DefaultIntentToolMap"/> when supplied.
    /// </summary>
    public IReadOnlyDictionary<string, string>? IntentToolMap { get; set; }
}

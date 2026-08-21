using Agents.AI.ContactCenter.Media.Transcription;

namespace Agents.AI.ContactCenter.Agents.IntentAgent;

/// <summary>
/// One classification event emitted by <see cref="IvrIntentAgent.ClassifyAudioStreamAsync"/>.
/// Pairs the final transcript segment with the intent classifier's verdict so callers
/// can drive routing decisions or surface insights alongside a primary realtime agent.
/// </summary>
/// <param name="Transcript">The final transcript segment that produced this classification.</param>
/// <param name="Intent">The intent classifier verdict for <paramref name="Transcript"/>.</param>
/// <param name="At">UTC timestamp the classification completed.</param>
/// <param name="ToolInvocation">
/// Optional details of a tool that was invoked locally as a result of this classification.
/// Null when no tool mapping fired (no intent matched, no tool resolved, or invocation skipped).
/// </param>
public sealed record IvrIntentEvent(
    TranscriptSegment Transcript,
    IntentResult Intent,
    DateTimeOffset At,
    IvrIntentToolInvocation? ToolInvocation = null);

/// <summary>
/// Outcome of a local tool invocation triggered by an intent classification.
/// </summary>
/// <param name="ToolName">The name of the tool that was invoked.</param>
/// <param name="Result">The tool's return value, if any.</param>
/// <param name="Error">The exception thrown by the tool, if invocation failed.</param>
public sealed record IvrIntentToolInvocation(
    string ToolName,
    object? Result,
    Exception? Error = null);

namespace Agents.AI.ContactCenter.Calling;

// SKETCH — optional capability strategies opt into when they can accept
// an out-of-band supervisor whisper (e.g., a system message injected mid-call).
//
// AgentEnsembleStrategy and RealtimeVoiceStrategy implement this by forwarding
// to IRealtimeVoiceBackend.UpdateSystemPromptAsync. DtmfStrategy / NluStrategy
// do not implement it — supervisor whisper is silently dropped for those tiers,
// which is the right behavior (no language model to whisper to).

/// <summary>
/// Optional capability for strategies that can accept supervisor whisper input
/// without exposing it on the caller's audio path.
/// </summary>
public interface IWhisperableStrategy
{
    /// <summary>
    /// Inject a supervisor utterance (transcribed text or raw audio) so the AI
    /// brain can incorporate it into its next response without the caller hearing.
    /// Implementations may choose to ignore audio they can't transcribe.
    /// </summary>
    ValueTask InjectWhisperAsync(SupervisorWhisper whisper, CancellationToken cancellationToken = default);
}

public sealed record SupervisorWhisper
{
    public required string SupervisorId { get; init; }
    public string? Text { get; init; }
    public ReadOnlyMemory<byte>? Audio { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

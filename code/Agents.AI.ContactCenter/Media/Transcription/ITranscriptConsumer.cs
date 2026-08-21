namespace Agents.AI.ContactCenter.Media.Transcription;

/// <summary>
/// A transport that can deliver outbound transcript segments to its remote endpoint
/// (e.g., live captions pushed to a UI via SignalR, or injected into an AI agent context).
/// </summary>
public interface ITranscriptConsumer
{
    /// <summary>Send a transcript segment to the transport.</summary>
    Task SendTranscriptAsync(TranscriptSegment segment, CancellationToken cancellationToken = default);
}

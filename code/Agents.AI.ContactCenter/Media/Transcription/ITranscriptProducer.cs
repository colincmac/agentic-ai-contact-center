namespace Agents.AI.ContactCenter.Media.Transcription;

/// <summary>
/// A transport that receives inbound transcript segments (from STT or an AI model)
/// and forwards them into the session via a registered callback.
/// </summary>
public interface ITranscriptProducer
{
    /// <summary>
    /// Register an inbound transcript handler invoked for every recognized segment.
    /// </summary>
    void SetOnTranscriptReceivedCallback(Func<string, TranscriptSegment, CancellationToken, Task> handler);
}

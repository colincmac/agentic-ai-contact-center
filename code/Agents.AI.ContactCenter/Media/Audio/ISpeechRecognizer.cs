using Agents.AI.ContactCenter.Media.Transcription;

namespace Agents.AI.ContactCenter.Media.Audio;

/// <summary>
/// Abstraction for continuous speech-to-text recognition from a raw audio stream.
/// Implementations wrap platform-specific SDKs (e.g., Azure Speech SDK) while
/// keeping transport code testable and SDK-independent.
/// </summary>
public interface ISpeechRecognizer : IAsyncDisposable
{
    /// <summary>
    /// Writes raw audio data into the recognition pipeline.
    /// Call this continuously as audio frames arrive from the transport.
    /// </summary>
    /// <param name="audioData">Raw PCM audio data.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task WriteAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns an async stream of transcript segments as they are recognized.
    /// Includes both interim hypotheses and final results based on the
    /// <see cref="TranscriptSegment.IsFinal"/> property.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the stream.</param>
    IAsyncEnumerable<TranscriptSegment> GetTranscriptsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Signals that no more audio will be written, allowing the recognizer
    /// to flush any remaining buffered speech.
    /// </summary>
    Task CompleteAsync(CancellationToken cancellationToken = default);
}

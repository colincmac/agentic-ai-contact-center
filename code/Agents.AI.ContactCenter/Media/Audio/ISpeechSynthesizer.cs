namespace Agents.AI.ContactCenter.Media.Audio;

/// <summary>
/// Abstraction for text-to-speech synthesis that produces streaming audio frames.
/// Implementations wrap platform-specific SDKs (e.g., Azure Speech SDK) while
/// keeping transport code testable and SDK-independent.
/// </summary>
/// <remarks>
/// The streaming return type (<see cref="IAsyncEnumerable{T}"/>) enables transports
/// to begin audio playback before synthesis completes, reducing perceived latency.
/// </remarks>
public interface ISpeechSynthesizer
{
    /// <summary>
    /// Synthesizes text into a stream of raw audio frames (PCM).
    /// Frames are yielded as they become available, enabling low-latency playback.
    /// </summary>
    /// <param name="text">The text to synthesize into speech.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An async stream of raw audio frames.</returns>
    IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(string text, SynthesizerInputFormat inputFormat = SynthesizerInputFormat.Text, CancellationToken cancellationToken = default);
}
public enum SynthesizerInputFormat
{
    Text,
    SSML
}

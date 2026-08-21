namespace Agents.AI.ContactCenter.Media.Audio;

/// <summary>
/// A transport that can deliver outbound audio frames (raw PCM or encoded)
/// to its remote endpoint.
/// </summary>
public interface IAudioConsumer
{
    /// <summary>Send a discrete audio frame (raw PCM or encoded) to the transport.</summary>
    Task SendAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default);
}

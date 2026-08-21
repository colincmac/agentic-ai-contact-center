namespace Agents.AI.ContactCenter.Media.Audio;

/// <summary>
/// Provides hold-music or prompt audio from a pre-recorded file, sending
/// frames to the transport via <see cref="IAudioConsumer.SendAudioAsync"/>.
/// </summary>
public class AudioFileProvider(Guid fileId, string fileUri, string fileVersion) : IAudioProducer
{
    public Guid FileId { get; } = fileId;
    public string FileUri { get; } = fileUri;
    public string FileVersion { get; } = fileVersion;

    public void SetOnAudioReceivedCallback(Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> handler)
    {
        throw new NotImplementedException();
    }
}

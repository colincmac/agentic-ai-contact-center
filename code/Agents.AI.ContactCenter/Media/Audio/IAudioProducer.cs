namespace Agents.AI.ContactCenter.Media.Audio;

/// <summary>
/// A transport that receives inbound audio frames and forwards them into the
/// session via a registered callback.
/// </summary>
public interface IAudioProducer
{
    /// <summary>
    /// Register an inbound audio handler. The router calls this to hook audio pushed from the transport.
    /// Transport implementation invokes the callback for every received frame.
    /// </summary>
    void SetOnAudioReceivedCallback(Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> handler);
}

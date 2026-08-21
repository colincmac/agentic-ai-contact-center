namespace Agents.AI.ContactCenter.Media.Signaling;

/// <summary>
/// A transport that can deliver outbound control signals (DTMF, call-control)
/// to its remote endpoint.
/// </summary>
public interface ISignalConsumer
{
    /// <summary>Send a control signal to the transport.</summary>
    Task SendSignalAsync(SessionSignal signal, CancellationToken cancellationToken = default);
}

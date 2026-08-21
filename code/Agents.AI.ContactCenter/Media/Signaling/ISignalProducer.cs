namespace Agents.AI.ContactCenter.Media.Signaling;

/// <summary>
/// A transport that receives inbound control signals (DTMF, call-control) and
/// forwards them into the session via a registered callback.
/// </summary>
public interface ISignalProducer
{
    /// <summary>
    /// Register an inbound signal handler invoked for every received control signal.
    /// </summary>
    void SetOnSignalReceivedCallback(Func<string, SessionSignal, CancellationToken, Task> handler);
}

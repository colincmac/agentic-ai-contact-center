using Azure.Communication.CallAutomation;

namespace Agents.AI.ContactCenter.Calling.Core;

/// <summary>
/// Production adapter that forwards directly to <see cref="CallConnection"/>.
/// </summary>
public sealed class CallControlClient : ICallControlClient
{
    private readonly CallConnection _connection;

    public CallControlClient(CallConnection connection)
    {
        _connection = connection;
    }

    public Task HangUpAsync(bool hangUpForEveryone, CancellationToken cancellationToken)
        => _connection.HangUpAsync(hangUpForEveryone, cancellationToken);

    public Task TransferAsync(TransferToParticipantOptions options, CancellationToken cancellationToken)
        => _connection.TransferCallToParticipantAsync(options, cancellationToken);
}

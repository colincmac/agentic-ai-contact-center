using System.Net.WebSockets;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Calling.Core;
using Agents.AI.ContactCenter.Telemetry;
using Azure.Communication;
using Azure.Communication.CallAutomation;
using ContactCenter.AIAgent.Configuration;
using Microsoft.Extensions.Options;
using AcsAudioFormat = Azure.Communication.CallAutomation.AudioFormat;
using CallAudioFormat = Agents.AI.ContactCenter.Calling.AudioFormat;

namespace ContactCenter.AIAgent.Services;

public interface IAcsCallGateway
{
    Task<CallConnectionProperties> AnswerAsync(string incomingContext, Uri callback, Uri media, CancellationToken ct);
    Task RedirectAsync(string incomingContext, string target, CancellationToken ct);
    ICallEdge CreateEdge(WebSocket socket, CallConnectionProperties connection, IncomingCallContext caller, CancellationToken ct);
    Task PlayAsync(string connectionId, string text, string operation, CancellationToken ct);
    Task TransferAsync(string connectionId, string target, string contextId, CancellationToken ct);
    Task HangUpAsync(string connectionId, CancellationToken ct);
}

public sealed class AcsCallGateway(CallAutomationClient client, IOptions<BankingDemoOptions> options,
    ILoggerFactory loggerFactory, CallingTelemetry telemetry) : IAcsCallGateway
{
    public Task RedirectAsync(string incomingContext, string target, CancellationToken ct)
        => client.RedirectCallAsync(incomingContext, new CallInvite(new PhoneNumberIdentifier(target),
            new PhoneNumberIdentifier(options.Value.RedirectCallerId)), ct);

    public async Task<CallConnectionProperties> AnswerAsync(string incomingContext, Uri callback, Uri media, CancellationToken ct)
    {
        var response = await client.AnswerCallAsync(new AnswerCallOptions(incomingContext, callback)
        {
            MediaStreamingOptions = new MediaStreamingOptions(MediaStreamingAudioChannel.Unmixed)
            {
                TransportUri = media, EnableBidirectional = true, EnableDtmfTones = true,
                StartMediaStreaming = true, AudioFormat = AcsAudioFormat.Pcm24KMono,
            },
            CallIntelligenceOptions = new CallIntelligenceOptions { CognitiveServicesEndpoint = options.Value.SpeechEndpoint },
        }, ct).ConfigureAwait(false);
        return response.Value.CallConnectionProperties;
    }

    public ICallEdge CreateEdge(WebSocket socket, CallConnectionProperties connection, IncomingCallContext caller, CancellationToken ct)
        => new AcsWebSocketEdge(socket, connection, ct, client, loggerFactory.CreateLogger<AcsWebSocketEdge>(), telemetry,
            new CallEdgeMetadata
            {
                RawIdentifier = caller.CallerIdentifier, DisplayName = caller.CallerDisplayName ?? "Caller",
                ServerCallId = caller.CallId, CorrelationId = caller.CorrelationId,
                InboundFormat = CallAudioFormat.Pcm24Khz16BitMono, OutboundFormat = CallAudioFormat.Pcm24Khz16BitMono,
            });

    public Task PlayAsync(string connectionId, string text, string operation, CancellationToken ct)
        => client.GetCallConnection(connectionId).GetCallMedia().PlayToAllAsync(
            new PlayToAllOptions(new TextSource(text, options.Value.VoiceName)) { OperationContext = operation }, ct);

    public Task TransferAsync(string connectionId, string target, string contextId, CancellationToken ct)
    {
        var transfer = new TransferToParticipantOptions(new PhoneNumberIdentifier(target));
        transfer.CustomCallingContext.AddVoip("context_id", contextId);
        transfer.CustomCallingContext.AddSipUui(contextId);
        transfer.OperationContext = contextId;
        return client.GetCallConnection(connectionId).TransferCallToParticipantAsync(transfer, ct);
    }

    public Task HangUpAsync(string connectionId, CancellationToken ct) => client.GetCallConnection(connectionId).HangUpAsync(true, ct);
}

using Azure.Communication;
using Azure.Communication.CallAutomation;
using AcsDtmfTone = Azure.Communication.CallAutomation.DtmfTone;

namespace Agents.AI.ContactCenter.Calling.Core;

/// <summary>
/// Production adapter that forwards directly to <see cref="CallMedia"/> on a real
/// <see cref="CallConnection"/>. The recognize verb targets the caller leg via the
/// supplied <see cref="Azure.Communication.CommunicationIdentifier"/>.
/// </summary>
public sealed class CallMediaClient : ICallMediaClient
{
    private readonly CallMedia _media;
    private readonly CommunicationIdentifier _target;

    public CallMediaClient(CallConnection connection, CommunicationIdentifier targetParticipant)
    {
        _media = connection.GetCallMedia();
        _target = targetParticipant;
    }

    public Task PlayTextAsync(string text, string? voiceName, string? operationContext, CancellationToken cancellationToken)
    {
        var source = string.IsNullOrEmpty(voiceName)
            ? new TextSource(text)
            : new TextSource(text, voiceName);
        var options = new PlayToAllOptions(source) { OperationContext = operationContext };
        return _media.PlayToAllAsync(options, cancellationToken);
    }

    public Task PlayFileAsync(Uri fileUri, string? operationContext, CancellationToken cancellationToken)
    {
        var source = new FileSource(fileUri);
        var options = new PlayToAllOptions(source) { OperationContext = operationContext };
        return _media.PlayToAllAsync(options, cancellationToken);
    }

    public Task CancelAllAsync(CancellationToken cancellationToken)
        => _media.CancelAllMediaOperationsAsync(cancellationToken);

    public Task RecognizeDtmfAsync(
        int maxTones,
        char? stopTone,
        TimeSpan? interToneTimeout,
        TimeSpan? initialSilenceTimeout,
        string? operationContext,
        CancellationToken cancellationToken)
    {
        var options = new CallMediaRecognizeDtmfOptions(_target, maxTones)
        {
            OperationContext = operationContext,
        };
        if (stopTone is char stop)
        {
            options.StopTones.Add(MapStopTone(stop));
        }
        if (interToneTimeout is TimeSpan it)
        {
            options.InterToneTimeout = it;
        }
        if (initialSilenceTimeout is TimeSpan ist)
        {
            options.InitialSilenceTimeout = ist;
        }
        return _media.StartRecognizingAsync(options, cancellationToken);
    }

    private static AcsDtmfTone MapStopTone(char digit) => digit switch
    {
        '0' => AcsDtmfTone.Zero,
        '1' => AcsDtmfTone.One,
        '2' => AcsDtmfTone.Two,
        '3' => AcsDtmfTone.Three,
        '4' => AcsDtmfTone.Four,
        '5' => AcsDtmfTone.Five,
        '6' => AcsDtmfTone.Six,
        '7' => AcsDtmfTone.Seven,
        '8' => AcsDtmfTone.Eight,
        '9' => AcsDtmfTone.Nine,
        '*' => AcsDtmfTone.Asterisk,
        '#' => AcsDtmfTone.Pound,
        'A' or 'a' => AcsDtmfTone.A,
        'B' or 'b' => AcsDtmfTone.B,
        'C' or 'c' => AcsDtmfTone.C,
        'D' or 'd' => AcsDtmfTone.D,
        _ => throw new ArgumentOutOfRangeException(nameof(digit), digit, "Unsupported DTMF stop tone")
    };
}

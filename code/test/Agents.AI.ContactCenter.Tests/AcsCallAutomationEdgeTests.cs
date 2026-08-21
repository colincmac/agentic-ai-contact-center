using System.Threading.Channels;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Calling.Core;
using Agents.AI.ContactCenter.Media.Audio;
using AcsCallAutomationModelFactory = global::Azure.Communication.CallAutomation.CallAutomationModelFactory;
using AcsCallMediaRecognitionType = global::Azure.Communication.CallAutomation.CallMediaRecognitionType;
using AcsDtmfTone = global::Azure.Communication.CallAutomation.DtmfTone;
using AcsRecognizeCompleted = global::Azure.Communication.CallAutomation.RecognizeCompleted;

namespace Agents.AI.RealtimeVoice.Azure.Tests.Proposed;

/// <summary>
/// Focused tests for the verb-based <see cref="AcsCallAutomationEdge"/>:
/// directives dispatch via REST verbs, and ACS callback events surface as
/// <see cref="ICallEdge.InboundDtmf"/>. The strategy-side coverage (DTMF menu
/// navigation, mismatched-strategy capability checks) was removed when the legacy
/// strategies were deleted in Phase 7b; equivalent end-to-end coverage will come
/// back via the call-session integration tests once <see cref="CallSessionFactory"/>
/// has a fully wired new-model fixture.
/// </summary>
public class AcsCallAutomationEdgeTests
{
    [Fact]
    public async Task AcsCallAutomationEdge_dispatches_directives_to_CallMedia()
    {
        var media = new RecordingCallMediaClient();
        await using var edge = new AcsCallAutomationEdge(
            "call-conn-1",
            media,
            new CallEdgeMetadata { DisplayName = "Caller", RawIdentifier = "+15555550001" },
            TestTelemetry.LoggerFor<AcsCallAutomationEdge>(),
            TestTelemetry.Calling);

        await edge.ConnectAsync();

        await edge.DispatchAsync(new OutboundDirective.SpeakText("Hello world", DateTimeOffset.UtcNow));
        await edge.DispatchAsync(new OutboundDirective.PlayFile(new Uri("https://example.com/menu.wav"), DateTimeOffset.UtcNow));
        await edge.DispatchAsync(new OutboundDirective.CollectDtmf(MaxTones: 4, At: DateTimeOffset.UtcNow, StopTone: '#'));
        await edge.DispatchAsync(new OutboundDirective.StopPlayback(DateTimeOffset.UtcNow));

        // Audio is unsupported on a verb edge — should not reach the media client.
        await edge.DispatchAsync(new OutboundDirective.Audio(
            new AudioFrame(new byte[] { 0x01 }, DateTimeOffset.UtcNow)));

        Assert.Equal("Hello world", media.LastSpokenText);
        Assert.Equal(new Uri("https://example.com/menu.wav"), media.LastPlayedFile);
        Assert.Equal(4, media.LastRecognizeMaxTones);
        Assert.Equal('#', media.LastRecognizeStopTone);
        Assert.Equal(1, media.CancelAllCount);
        Assert.Equal(0, media.AudioFrameCount); // Audio dropped, never reached CallMedia
    }

    [Fact]
    public async Task AcsCallAutomationEdge_translates_RecognizeCompleted_to_InboundDtmf()
    {
        var media = new RecordingCallMediaClient();
        await using var edge = new AcsCallAutomationEdge(
            "call-conn-2",
            media,
            new CallEdgeMetadata { DisplayName = "Caller", RawIdentifier = "+15555550002" },
            TestTelemetry.LoggerFor<AcsCallAutomationEdge>(),
            TestTelemetry.Calling);

        await edge.ConnectAsync();

        // The webhook handler simulates ACS posting a RecognizeCompleted event.
        var evt = TestEvents.RecognizeCompletedWithDtmf("call-conn-2", "step-1", "23#");
        edge.OnRecognizeCompleted(evt);

        var first = await ReadDtmfAsync(edge.InboundDtmf, TimeSpan.FromSeconds(2));
        var second = await ReadDtmfAsync(edge.InboundDtmf, TimeSpan.FromSeconds(2));
        var third = await ReadDtmfAsync(edge.InboundDtmf, TimeSpan.FromSeconds(2));

        Assert.Equal('2', first.Digit);
        Assert.Equal('3', second.Digit);
        Assert.Equal('#', third.Digit);
    }

    private static async Task<DtmfTone> ReadDtmfAsync(ChannelReader<DtmfTone> reader, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await reader.ReadAsync(cts.Token);
    }
}

/// <summary>
/// Test double for <see cref="ICallMediaClient"/> that records every call so the
/// test can assert the verb edge translated directives correctly.
/// </summary>
internal sealed class RecordingCallMediaClient : ICallMediaClient
{
    public List<string> SpokenTexts { get; } = [];
    public string? LastSpokenText { get; private set; }
    public Uri? LastPlayedFile { get; private set; }
    public int RecognizeCalls { get; private set; }
    public int? LastRecognizeMaxTones { get; private set; }
    public char? LastRecognizeStopTone { get; private set; }
    public int CancelAllCount { get; private set; }
    public int AudioFrameCount { get; private set; }

    public Task PlayTextAsync(string text, string? voiceName, string? operationContext, CancellationToken cancellationToken)
    {
        LastSpokenText = text;
        SpokenTexts.Add(text);
        return Task.CompletedTask;
    }

    public Task PlayFileAsync(Uri fileUri, string? operationContext, CancellationToken cancellationToken)
    {
        LastPlayedFile = fileUri;
        return Task.CompletedTask;
    }

    public Task CancelAllAsync(CancellationToken cancellationToken)
    {
        CancelAllCount++;
        return Task.CompletedTask;
    }

    public Task RecognizeDtmfAsync(int maxTones, char? stopTone, TimeSpan? interToneTimeout, TimeSpan? initialSilenceTimeout, string? operationContext, CancellationToken cancellationToken)
    {
        RecognizeCalls++;
        LastRecognizeMaxTones = maxTones;
        LastRecognizeStopTone = stopTone;
        return Task.CompletedTask;
    }
}

/// <summary>Helpers to construct realistic ACS event payloads for the webhook hooks.</summary>
internal static class TestEvents
{
    public static AcsRecognizeCompleted RecognizeCompletedWithDtmf(
        string callConnectionId, string operationContext, string digits)
    {
        var tones = digits.Select(MapDigit).ToList();
        var dtmfResult = AcsCallAutomationModelFactory.DtmfResult(tones);

        return AcsCallAutomationModelFactory.RecognizeCompleted(
            callConnectionId: callConnectionId,
            serverCallId: "server-call",
            correlationId: "corr",
            operationContext: operationContext,
            resultInformation: null,
            recognitionType: AcsCallMediaRecognitionType.Dtmf,
            recognizeResult: dtmfResult);
    }

    private static AcsDtmfTone MapDigit(char digit) => digit switch
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
        _ => throw new ArgumentOutOfRangeException(nameof(digit))
    };
}

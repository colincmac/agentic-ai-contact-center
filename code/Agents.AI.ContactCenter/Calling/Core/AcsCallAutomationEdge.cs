using System.Diagnostics;
using System.Threading.Channels;
using Agents.AI.ContactCenter.Media.Signaling;
using Agents.AI.ContactCenter.Telemetry;
using Azure.Communication.CallAutomation;
using Microsoft.Extensions.Logging;
using AcsDtmfTone = Azure.Communication.CallAutomation.DtmfTone;

namespace Agents.AI.ContactCenter.Calling.Core;

/// <summary>
/// Verb-based ACS caller edge. Owns no media WebSocket — it dispatches Play /
/// Recognize / Cancel verbs against ACS Call Automation and receives results
/// out-of-band on the application's callback webhook (see <c>CallingApi</c>).
/// <para>
/// Pair with strategies that emit <see cref="OutboundDirective.SpeakText"/>,
/// <see cref="OutboundDirective.PlayFile"/>, and <see cref="OutboundDirective.CollectDtmf"/>.
/// Audio directives are dropped — there's no streaming channel to write them to.
/// </para>
/// </summary>
public sealed class AcsCallAutomationEdge : ICallEdge, ICallControl
{
    private readonly ICallMediaClient _media;
    private readonly ICallControlClient? _control;
    private readonly ILogger<AcsCallAutomationEdge> _logger;
    private readonly CallingTelemetry _telemetry;
    private readonly CancellationTokenSource _cts = new();

    // Caller-side audio is never produced by this edge — verb-based calls have no
    // streaming inbound channel. The reader stays empty for the lifetime of the edge.
    private readonly Channel<AudioFrame> _inboundAudio = Channel.CreateUnbounded<AudioFrame>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private readonly Channel<DtmfTone> _inboundDtmf = Channel.CreateUnbounded<DtmfTone>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private readonly Channel<SessionSignal> _inboundSignals = Channel.CreateUnbounded<SessionSignal>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private bool _connected;
    private int _disconnectFired;

    public AcsCallAutomationEdge(
        string callConnectionId,
        ICallMediaClient media,
        CallEdgeMetadata metadata,
        ILogger<AcsCallAutomationEdge> logger,
        CallingTelemetry telemetry,
        ICallControlClient? control = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(telemetry);

        EdgeId = callConnectionId;
        _media = media;
        _control = control;
        Metadata = metadata;
        _logger = logger;
        _telemetry = telemetry;
    }

    public string EdgeId { get; }

    public CallEdgeKind Kind => CallEdgeKind.Caller;

    public CallEdgeMetadata Metadata { get; }

    public bool IsConnected => _connected;

    public ChannelReader<AudioFrame> InboundAudio => _inboundAudio.Reader;

    public ChannelReader<DtmfTone> InboundDtmf => _inboundDtmf.Reader;

    public ChannelReader<SessionSignal> InboundSignals => _inboundSignals.Reader;

    public EdgeCapabilities Capabilities => EdgeCapabilities.Verb | (_control is null ? EdgeCapabilities.None : EdgeCapabilities.TransferCall);

    public bool CanControl => _control is not null;

    public event Func<EdgeDisconnectedReason, ValueTask>? Disconnected;

    public Task HangUpAsync(bool hangUpForEveryone, CancellationToken cancellationToken = default)
    {
        if (_control is null)
        {
            throw new InvalidOperationException(
                $"{nameof(AcsCallAutomationEdge)} {EdgeId} cannot hang up: no ICallControlClient was provided.");
        }
        return _control.HangUpAsync(hangUpForEveryone, cancellationToken);
    }

    public Task TransferAsync(TransferRequest request, CancellationToken cancellationToken = default)
    {
        if (_control is null)
        {
            throw new InvalidOperationException(
                $"{nameof(AcsCallAutomationEdge)} {EdgeId} cannot transfer: no ICallControlClient was provided.");
        }
        var options = AcsCallControl.BuildTransferOptions(request);
        return _control.TransferAsync(options, cancellationToken);
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _connected = true;
        _telemetry.EdgeConnected(EdgeId, Kind);
        return Task.CompletedTask;
    }

    public async ValueTask DispatchAsync(OutboundDirective directive, CancellationToken cancellationToken = default)
    {
        var directiveKind = directive.GetType().Name;
        var dispatchStart = Stopwatch.GetTimestamp();
        try
        {
            switch (directive)
            {
                case OutboundDirective.SpeakText speak:
                    await _media.PlayTextAsync(speak.Text, speak.VoiceName, speak.OperationContext, cancellationToken).ConfigureAwait(false);
                    _telemetry.DirectiveDispatched(EdgeId, directiveKind, Stopwatch.GetElapsedTime(dispatchStart));
                    break;

                case OutboundDirective.PlayFile play:
                    await _media.PlayFileAsync(play.FileUri, play.OperationContext, cancellationToken).ConfigureAwait(false);
                    _telemetry.DirectiveDispatched(EdgeId, directiveKind, Stopwatch.GetElapsedTime(dispatchStart));
                    break;

                case OutboundDirective.StopPlayback:
                    await _media.CancelAllAsync(cancellationToken).ConfigureAwait(false);
                    _telemetry.DirectiveDispatched(EdgeId, directiveKind, Stopwatch.GetElapsedTime(dispatchStart));
                    break;

                case OutboundDirective.CollectDtmf recognize:
                    await _media.RecognizeDtmfAsync(
                        recognize.MaxTones,
                        recognize.StopTone,
                        recognize.InterToneTimeout,
                        recognize.InitialSilenceTimeout,
                        recognize.OperationContext,
                        cancellationToken).ConfigureAwait(false);
                    _telemetry.DirectiveDispatched(EdgeId, directiveKind, Stopwatch.GetElapsedTime(dispatchStart));
                    break;

                case OutboundDirective.TransferCall transfer:
                    _logger.LogInformation(
                        "Verb edge {EdgeId} transferring call to {Target} ({Kind}); reason: {Reason}",
                        EdgeId, transfer.TargetIdentifier, transfer.Kind, transfer.Reason ?? "(none)");
                    var transferRequest = new TransferRequest(
                        transfer.TargetIdentifier,
                        transfer.Kind,
                        transfer.Reason is { Length: > 0 }
                            ? new Dictionary<string, string> { ["reason"] = transfer.Reason }
                            : null);
                    await TransferAsync(transferRequest, cancellationToken).ConfigureAwait(false);
                    _telemetry.DirectiveDispatched(EdgeId, directiveKind, Stopwatch.GetElapsedTime(dispatchStart));
                    break;

                default:
                    _logger.LogWarning(
                        "Verb ACS edge {EdgeId} cannot dispatch {DirectiveKind}; pair this strategy with an AcsCallerEdge instead",
                        EdgeId, directiveKind);
                    _telemetry.DirectiveUnsupported(EdgeId, directiveKind, Capabilities);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _telemetry.DirectiveDispatchFailed(EdgeId, directiveKind, ex);
            _logger.LogWarning(ex, "Verb ACS edge {EdgeId} failed to dispatch {DirectiveKind}", EdgeId, directiveKind);
        }
    }

    /// <summary>
    /// Hook the application webhook calls when ACS posts a <see cref="RecognizeCompleted"/>
    /// CloudEvent for this call. Writes the recognized DTMF digits to <see cref="InboundDtmf"/>.
    /// </summary>
    public void OnRecognizeCompleted(RecognizeCompleted evt)
    {
        var result = evt.RecognizeResult;
        if (result is DtmfResult dtmf && dtmf.Tones is { Count: > 0 } tones)
        {
            var at = DateTimeOffset.UtcNow;
            foreach (var tone in tones)
            {
                if (TryMapTone(tone, out var digit))
                {
                    _inboundDtmf.Writer.TryWrite(new DtmfTone(digit, at));
                    _telemetry.InboundDtmfTone(EdgeId);
                }
            }
        }
    }

    /// <summary>
    /// Hook the application webhook calls when ACS posts a <see cref="RecognizeFailed"/>.
    /// Surfaced as a <see cref="SessionSignalKind.Custom"/> signal so strategies can react
    /// (re-prompt, increment retry counter, escalate).
    /// </summary>
    public void OnRecognizeFailed(RecognizeFailed evt)
    {
        _inboundSignals.Writer.TryWrite(new SessionSignal
        {
            Kind = SessionSignalKind.Custom,
            Value = $"recognize-failed:{evt.ReasonCode}"
        });
    }

    /// <summary>Hook for ACS PlayCompleted callbacks. Drops the event today; reserved for future correlation.</summary>
    public void OnPlayCompleted(PlayCompleted evt) => _logger.LogDebug("Play completed for {EdgeId} ({Context})", EdgeId, evt.OperationContext);

    /// <summary>Hook for ACS PlayFailed callbacks. Logged as a warning; no signal raised by default.</summary>
    public void OnPlayFailed(PlayFailed evt) => _logger.LogWarning("Play failed for {EdgeId}: {Reason}", EdgeId, evt.ReasonCode);

    /// <summary>Hook for ACS CallDisconnected callbacks. Fires <see cref="Disconnected"/>.</summary>
    public void OnCallDisconnected(EdgeDisconnectedReason reason = EdgeDisconnectedReason.CallerHangup)
        => _ = RaiseDisconnectedAsync(reason);

    public async ValueTask DisposeAsync()
    {
        _connected = false;
        await _cts.CancelAsync().ConfigureAwait(false);
        _inboundAudio.Writer.TryComplete();
        _inboundDtmf.Writer.TryComplete();
        _inboundSignals.Writer.TryComplete();
        _cts.Dispose();
        await RaiseDisconnectedAsync(EdgeDisconnectedReason.SessionEnded).ConfigureAwait(false);
    }

    private async ValueTask RaiseDisconnectedAsync(EdgeDisconnectedReason reason)
    {
        if (Interlocked.Exchange(ref _disconnectFired, 1) != 0)
        {
            return;
        }

        _telemetry.EdgeDisconnected(EdgeId, Kind, reason);

        var handlers = Disconnected;
        if (handlers is null)
        {
            return;
        }
        foreach (var handler in handlers.GetInvocationList().Cast<Func<EdgeDisconnectedReason, ValueTask>>())
        {
            try { await handler(reason).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Disconnected handler threw"); }
        }
    }

    private static bool TryMapTone(AcsDtmfTone acsTone, out char digit)
    {
        if (acsTone == AcsDtmfTone.Zero)     { digit = '0'; return true; }
        if (acsTone == AcsDtmfTone.One)      { digit = '1'; return true; }
        if (acsTone == AcsDtmfTone.Two)      { digit = '2'; return true; }
        if (acsTone == AcsDtmfTone.Three)    { digit = '3'; return true; }
        if (acsTone == AcsDtmfTone.Four)     { digit = '4'; return true; }
        if (acsTone == AcsDtmfTone.Five)     { digit = '5'; return true; }
        if (acsTone == AcsDtmfTone.Six)      { digit = '6'; return true; }
        if (acsTone == AcsDtmfTone.Seven)    { digit = '7'; return true; }
        if (acsTone == AcsDtmfTone.Eight)    { digit = '8'; return true; }
        if (acsTone == AcsDtmfTone.Nine)     { digit = '9'; return true; }
        if (acsTone == AcsDtmfTone.Pound)    { digit = '#'; return true; }
        if (acsTone == AcsDtmfTone.Asterisk) { digit = '*'; return true; }
        if (acsTone == AcsDtmfTone.A)        { digit = 'A'; return true; }
        if (acsTone == AcsDtmfTone.B)        { digit = 'B'; return true; }
        if (acsTone == AcsDtmfTone.C)        { digit = 'C'; return true; }
        if (acsTone == AcsDtmfTone.D)        { digit = 'D'; return true; }
        digit = '\0';
        return false;
    }
}

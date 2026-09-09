using System.Threading.Channels;
using Agents.AI.ContactCenter.Agents.IntentAgent;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.State;
using Agents.AI.ContactCenter.State.Projections;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;

namespace Agents.AI.ContactCenter.Calling.Strategies.Nlu;

/// <summary>
/// Phase-5 successor to <see cref="NluConversationStrategy"/> built on the new
/// <see cref="CompiledCallWorkflow"/> + <see cref="WorkflowExecutor"/> model. Pumps caller
/// audio into an <see cref="IvrIntentAgent"/>, advances the workflow when a final intent
/// classification maps to a transition label, and renders stage prompts through the
/// scripted SSML / NLU configuration on the blueprint.
/// </summary>
/// <remarks>
/// The intent classifier's candidate set is derived from the current stage's
/// <see cref="StageNluConfig.Intents"/> plus the well-known
/// <see cref="TransferIntentName"/>. The intent name resolves to a transition label on
/// the active stage; the executor handles guards and inline authentication uniformly.
/// </remarks>
public sealed class NluCallWorkflowStrategy : IConversationStrategy
{
    /// <summary>Reserved intent name. Classifying this emits a <see cref="OutboundDirective.TransferCall"/>.</summary>
    public const string TransferIntentName = "transfer_to_agent";

    private readonly CallWorkflowSession _session;
    private readonly IvrIntentAgent _intentAgent;
    private readonly ISpeechSynthesizer _synthesizer;
    private readonly WorkflowExecutor _executor;
    private readonly ILogger _logger;

    private readonly Channel<OutboundDirective> _outbound = Channel.CreateBounded<OutboundDirective>(
        new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

    private readonly Channel<StrategyEvent> _events = Channel.CreateUnbounded<StrategyEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    // Option A emit funnel: folds every emitted event into the call projector synchronously before it
    // reaches the session pump. _projector is captured from StrategyStartContext in StartAsync.
    private CallStateProjector? _projector;
    private readonly ChannelWriter<StrategyEvent> _emit;

    private readonly Channel<AudioFrame> _audioFrames = Channel.CreateBounded<AudioFrame>(
        new BoundedChannelOptions(512)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    private readonly CancellationTokenSource _cts = new();
    private Task? _audioPump;
    private Task? _dtmfPump;
    private Task? _classifyLoop;
    private bool _suspended;
    private string _callId = string.Empty;
    private EdgeCapabilities? _edgeCapabilities;
    private int _disposed;

    private readonly CredentialCapture _capture;

    public NluCallWorkflowStrategy(
        CallWorkflowSession session,
        IvrIntentAgent intentAgent,
        ISpeechSynthesizer synthesizer,
        TransferEscalationTarget? escalationTarget = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(intentAgent);
        ArgumentNullException.ThrowIfNull(synthesizer);

        _session = session;
        _capture = session.CredentialCapture;
        _intentAgent = intentAgent;
        _synthesizer = synthesizer;
        EscalationTarget = escalationTarget;
        _logger = loggerFactory?.CreateLogger<NluCallWorkflowStrategy>()
            ?? NullLogger<NluCallWorkflowStrategy>.Instance;

        _emit = new StateFoldingChannelWriter(_events.Writer, () => _projector);
        _executor = new WorkflowExecutor(_session, _emit, RenderStageAsync, RenderAuthAsync, () => _projector);
    }

    public StrategyKind Kind => StrategyKind.Nlu;

    public AgentTier Tier => AgentTier.IntentNlu;

    public IvrSnapshot WorkflowState => _projector?.Get<IvrSnapshot>() ?? IvrSnapshot.Empty;

    public EdgeCapabilities EmittedDirectives =>
        EdgeCapabilities.Audio | EdgeCapabilities.StopPlayback | EdgeCapabilities.TransferCall;

    public ChannelReader<OutboundDirective> Outbound => _outbound.Reader;

    public ChannelReader<StrategyEvent> Events => _events.Reader;

    public TransferEscalationTarget? EscalationTarget { get; }

    public async Task StartAsync(StrategyStartContext context, CancellationToken cancellationToken = default)
    {
        if (_classifyLoop is not null)
        {
            return;
        }

        _callId = context.CallId;
        _edgeCapabilities = context.EdgeCapabilities;
        _projector = context.StateProjector;

        // Run the call-start authenticator chain (ANI lookup, etc.) BEFORE entering
        // the workflow so per-step verification guards can read a populated
        // CallerAuthenticationState. See Authentication/README.md "Call-start helper".
        await CallerAuthenticationRunner.RunAsync(
            context,
            _session.Services,
            _emit,
            _logger,
            cancellationToken).ConfigureAwait(false);

        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        _audioPump = Task.Run(() => PumpInboundAudioAsync(context, linked.Token), CancellationToken.None);
        _dtmfPump = Task.Run(() => PumpInboundDtmfAsync(context, linked.Token), CancellationToken.None);
        _classifyLoop = Task.Run(() => RunAsync(linked.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _audioFrames.Writer.TryComplete();

        if (_audioPump is not null) { try { await _audioPump.ConfigureAwait(false); } catch { } }
        if (_dtmfPump is not null) { try { await _dtmfPump.ConfigureAwait(false); } catch { } }
        if (_classifyLoop is not null) { try { await _classifyLoop.ConfigureAwait(false); } catch { } }

        _outbound.Writer.TryComplete();
        _events.Writer.TryComplete();
    }

    public ValueTask SuspendAsync(CancellationToken cancellationToken = default) { _suspended = true; return ValueTask.CompletedTask; }
    public ValueTask ResumeAsync(CancellationToken cancellationToken = default) { _suspended = false; return ValueTask.CompletedTask; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private async ValueTask RenderStageAsync(CompiledStage stage, CancellationToken ct)
    {
        if (!await CallWorkflowEligibility.CheckAsync(_session.Services, Tier, stage, _edgeCapabilities, _emit, ct).ConfigureAwait(false)) { return; }
        _capture.End();

        await _emit.WriteAsync(
            new StrategyEvent.WorkflowStepEntered(stage.Id, DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);

        var ssml = stage.Blueprint.Channels.Scripted?.SsmlPrompt;
        if (!string.IsNullOrWhiteSpace(ssml))
        {
            var format = ssml.TrimStart().StartsWith("<speak", StringComparison.OrdinalIgnoreCase)
                ? SynthesizerInputFormat.SSML : SynthesizerInputFormat.Text;
            await SpeakAsync(ssml, format, ct).ConfigureAwait(false);
            return;
        }

        var text = stage.Blueprint.Channels.Nlu?.Instructions
            ?? stage.Blueprint.Goal
            ?? stage.Blueprint.Description;
        if (!string.IsNullOrWhiteSpace(text))
        {
            await SpeakAsync(text, SynthesizerInputFormat.Text, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Render an inline authentication step on the NLU surface: speak the credential prompt and
    /// enter digit-collect mode. Digits are collected through the existing DTMF pump.
    /// </summary>
    private async ValueTask RenderAuthAsync(AuthStepRender render, CancellationToken ct)
    {
        if (!await CallWorkflowEligibility.CheckAsync(_session.Services, Tier, render.Stage, _edgeCapabilities, _emit, ct).ConfigureAwait(false)) { return; }

        var prompt = _capture.Begin(render);
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            var format = prompt.TrimStart().StartsWith("<speak", StringComparison.OrdinalIgnoreCase)
                ? SynthesizerInputFormat.SSML
                : SynthesizerInputFormat.Text;
            await SpeakAsync(prompt, format, ct).ConfigureAwait(false);
        }
    }

    private async Task PumpInboundAudioAsync(StrategyStartContext context, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in context.InboundAudio.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (_suspended || _executor.IsAuthenticating || _capture.SuppressesAudio(frame.Timestamp)) { continue; }
                await _audioFrames.Writer.WriteAsync(frame, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NLU inbound audio pump terminated for call {CallId}", _callId);
        }
        finally { _audioFrames.Writer.TryComplete(); }
    }

    private async Task PumpInboundDtmfAsync(StrategyStartContext context, CancellationToken ct)
    {
        try
        {
            await foreach (var tone in context.InboundDtmf.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (_suspended) { continue; }
                var current = _executor.CurrentStage;
                if (_executor.IsAuthenticating)
                {
                    if (!_capture.IsActive) { continue; }
                    var capture = _capture.Accept(tone.Digit);
                    if (capture.Prompt is { } prompt) { await SpeakAsync(prompt, SynthesizerInputFormat.Text, ct).ConfigureAwait(false); }
                    if (capture.Input is { } input)
                    {
                        await _executor.SubmitCredentialAsync(capture.AuthenticatorName!, input, ct).ConfigureAwait(false);
                    }
                    continue;
                }
                await _emit.WriteAsync(
                    new StrategyEvent.DtmfRecognized(tone.Digit.ToString(), current?.Id, DateTimeOffset.UtcNow),
                    ct).ConfigureAwait(false);

                if (current?.Blueprint.Channels.Scripted is { MenuOptions: { Count: > 0 } menu }
                    && menu.TryGetValue(tone.Digit, out var option)
                    && current.FindEdgeByLabel(option.TransitionLabel) is { } edge)
                {
                    await _executor.AdvanceAlongAsync(edge, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NLU inbound DTMF pump terminated for call {CallId}", _callId);
        }
    }

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadEligibleAudioAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var frame in _audioFrames.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (!_executor.IsAuthenticating && !_capture.SuppressesAudio(frame.Timestamp)) { yield return frame.Pcm; }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await _executor.EnterAsync(ct).ConfigureAwait(false);

            await foreach (var evt in _intentAgent
                .ClassifyAudioStreamAsync(ReadEligibleAudioAsync(ct), BuildContext, ct)
                .ConfigureAwait(false))
            {
                if (_executor.IsAuthenticating || !evt.Transcript.IsFinal || string.IsNullOrWhiteSpace(evt.Transcript.Text))
                {
                    continue;
                }

                await _emit.WriteAsync(
                    new StrategyEvent.Transcript("caller", evt.Transcript.Text, IsFinal: true, DateTimeOffset.UtcNow),
                    ct).ConfigureAwait(false);

                if (_suspended) { continue; }
                await ProcessIntentEventAsync(evt, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NLU strategy faulted for call {CallId}", _callId);
            await _emit.WriteAsync(
                new StrategyEvent.Faulted(ex.Message, ex, DateTimeOffset.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _outbound.Writer.TryComplete();
            _events.Writer.TryComplete();
        }
    }

    private IvrIntentClassificationContext BuildContext()
    {
        var stage = _executor.CurrentStage;
        var intents = stage?.Blueprint.Channels.Nlu?.Intents ?? [];
        var valid = new List<string>(intents.Count + 1);
        foreach (var intent in intents) { valid.Add(intent.Name); }
        if (EscalationTarget is not null && !valid.Contains(TransferIntentName))
        {
            valid.Add(TransferIntentName);
        }

        return new IvrIntentClassificationContext(
            Utterance: string.Empty,
            ValidIntents: valid,
            Tools: Array.Empty<AITool>(),
            IntentToolMap: null);
    }

    private async Task ProcessIntentEventAsync(IvrIntentEvent evt, CancellationToken ct)
    {
        var result = evt.Intent;
        if (result.IsNone)
        {
            await SpeakAsync("I didn't understand that. Could you say it another way?", SynthesizerInputFormat.Text, ct).ConfigureAwait(false);
            return;
        }

        await _emit.WriteAsync(
            new StrategyEvent.IntentClassified(result.IntentName!, result.Confidence, DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);

        if (result.Entities is { Count: > 0 })
        {
            _logger.LogDebug("Unvalidated classifier entities are not written to authoritative workflow state.");
        }

        if (string.Equals(result.IntentName, TransferIntentName, StringComparison.Ordinal))
        {
            await EscalateAsync(result.Entities?.GetValueOrDefault("reason") ?? "Caller requested an agent.", ct).ConfigureAwait(false);
            return;
        }

        var stage = _executor.CurrentStage;
        var nluIntents = stage?.Blueprint.Channels.Nlu?.Intents ?? [];
        var match = nluIntents.FirstOrDefault(i => string.Equals(i.Name, result.IntentName, StringComparison.Ordinal));
        if (match is null)
        {
            _logger.LogWarning(
                "Intent '{Intent}' classified on stage '{Stage}' but blueprint has no matching entry.",
                result.IntentName, stage?.Id);
            return;
        }

        if (stage!.FindEdgeByLabel(match.TransitionLabel) is not { } edge)
        {
            _logger.LogWarning(
                "Intent '{Intent}' maps to label '{Label}' which has no outgoing edge on stage '{Stage}'.",
                result.IntentName, match.TransitionLabel, stage.Id);
            return;
        }

        var outcome = await _executor.AdvanceAlongAsync(edge, ct).ConfigureAwait(false);
        if (outcome is AdvanceOutcome.Denied denied)
        {
            await SpeakAsync($"I can't do that right now: {denied.Reason}", SynthesizerInputFormat.Text, ct).ConfigureAwait(false);
        }
    }

    private async Task EscalateAsync(string reason, CancellationToken ct)
    {
        if (EscalationTarget is null)
        {
            await SpeakAsync("I'm unable to transfer you right now.", SynthesizerInputFormat.Text, ct).ConfigureAwait(false);
            return;
        }

        await _emit.WriteAsync(
            new StrategyEvent.EscalationRequested(reason, DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);
        await _outbound.Writer.WriteAsync(
            new OutboundDirective.TransferCall(
                EscalationTarget.TargetIdentifier,
                EscalationTarget.Kind,
                DateTimeOffset.UtcNow,
                reason),
            ct).ConfigureAwait(false);
    }

    private async Task SpeakAsync(string text, SynthesizerInputFormat format, CancellationToken ct)
    {
        try
        {
            await foreach (var pcm in _synthesizer.SynthesizeAsync(text, format, ct).ConfigureAwait(false))
            {
                if (_suspended) { break; }
                await _outbound.Writer.WriteAsync(
                    new OutboundDirective.Audio(new AudioFrame(pcm, DateTimeOffset.UtcNow, SourceEdgeId: null)),
                    ct).ConfigureAwait(false);
            }

            await _emit.WriteAsync(
                new StrategyEvent.AgentUtterance("nlu", text, DateTimeOffset.UtcNow),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TTS synthesis failed in NLU strategy for call {CallId}", _callId);
            throw;
        }
    }
}

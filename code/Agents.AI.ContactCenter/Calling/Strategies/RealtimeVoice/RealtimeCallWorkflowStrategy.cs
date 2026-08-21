using System.ComponentModel;
using System.Text;
using System.Threading.Channels;
using Agents.AI.ContactCenter.Agents.AuthorizationAgent;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Agents.AI.ContactCenter.State;
using Agents.AI.ContactCenter.State.Projections;
using Agents.AI.ContactCenter.Telemetry;
using Agents.AI.Realtime;
using Extensions.AI.Contents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Calling.Strategies.RealtimeVoice;


public sealed class RealtimeCallWorkflowStrategy : IConversationStrategy
{
    private readonly AuthorizingAIAgent _agent;
    private RealtimeAIAgentSession? _agentSession;

    private readonly CallWorkflowSession _callWorkflowSession;
    private readonly WorkflowExecutor _executor;
    private readonly CallingTelemetry _telemetry;
    private readonly ILogger _logger;
    private string _callId = string.Empty;
    //private readonly bool _enableSessionPerTurn = false;
    private readonly Channel<OutboundDirective> _outbound = Channel.CreateBounded<OutboundDirective>(
        new BoundedChannelOptions(500)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    private readonly Channel<StrategyEvent> _events = Channel.CreateUnbounded<StrategyEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    // Option A emit funnel: folds every emitted event into the call projector synchronously before it
    // reaches the session pump. _projector is captured from StrategyStartContext in StartAsync.
    private CallStateProjector? _projector;
    private readonly ChannelWriter<StrategyEvent> _emit;

    private readonly CancellationTokenSource _cts = new();
    private Task? _agentLoop;
    private Task? _audioPump;
    private Task? _dtmfPump;
    private bool _suspended;
    private int _disposed;

    public RealtimeCallWorkflowStrategy(
        AuthorizingAIAgent agent,
        CallWorkflowSession callWorkflowSession,
        CallingTelemetry telemetry,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(callWorkflowSession);
        ArgumentNullException.ThrowIfNull(telemetry);

        _agent = agent;
        _callWorkflowSession = callWorkflowSession;
        _telemetry = telemetry;
        _logger = loggerFactory?.CreateLogger<RealtimeCallWorkflowStrategy>()
            ?? NullLogger<RealtimeCallWorkflowStrategy>.Instance;

        _emit = new StateFoldingChannelWriter(_events.Writer, () => _projector);
        _executor = new WorkflowExecutor(_callWorkflowSession, _emit, RenderStageAsync, RenderAuthAsync, () => _projector);
    }

    public StrategyKind Kind => StrategyKind.RealtimeVoice;

    public AgentTier Tier => AgentTier.RealtimeVoice;

    public IvrSnapshot WorkflowState => _projector?.Get<IvrSnapshot>() ?? IvrSnapshot.Empty;

    public EdgeCapabilities EmittedDirectives => EdgeCapabilities.Audio | EdgeCapabilities.StopPlayback;

    public ChannelReader<OutboundDirective> Outbound => _outbound.Reader;

    public ChannelReader<StrategyEvent> Events => _events.Reader;

    public async Task StartAsync(StrategyStartContext context, CancellationToken cancellationToken = default)
    {
        if (_agentLoop is not null)
        {
            return;
        }

        _callId = context.CallId;
        _projector = context.StateProjector;
        await ConnectBackendAsync(cancellationToken).ConfigureAwait(false);

        // Run the call-start authenticator chain (ANI lookup, etc.) BEFORE entering
        // the workflow so per-step / per-tool verification gates can read a populated
        // CallerAuthenticationState. See Authentication/README.md "Call-start helper".
        await CallerAuthenticationRunner.RunAsync(
            context,
            _callWorkflowSession.Services,
            _emit,
            _logger,
            cancellationToken).ConfigureAwait(false);

        await _executor.EnterAsync(cancellationToken).ConfigureAwait(false);

        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        _audioPump = Task.Run(() => PumpInboundAudioAsync(context, linked.Token), CancellationToken.None);
        _dtmfPump = Task.Run(() => PumpInboundDtmfAsync(context, linked.Token), CancellationToken.None);
        _agentLoop = Task.Run(() => RunAgentLoopAsync(linked.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        if (_audioPump is not null)
        {
            try { await _audioPump.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        if (_dtmfPump is not null)
        {
            try { await _dtmfPump.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        if (_agentLoop is not null)
        {
            try { await _agentLoop.ConfigureAwait(false); } catch { /* shutdown */ }
        }

        _outbound.Writer.TryComplete();
        _events.Writer.TryComplete();
    }

    public ValueTask SuspendAsync(CancellationToken cancellationToken = default)
    {
        _suspended = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask ResumeAsync(CancellationToken cancellationToken = default)
    {
        _suspended = false;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        if (_agentSession?.ClientSession is not null) { await _agentSession.ClientSession.DisposeAsync().ConfigureAwait(false); }

        _cts.Dispose();
    }

    private async Task ConnectBackendAsync(CancellationToken cancellationToken)
    {
        using var connectSpan = _telemetry.StartChildActivity("contact_center.strategy.backend.connect", _callId);
        try
        {
            _agentSession = await _agent.CreateSessionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CallingActivitySource.SetError(connectSpan, ex);
            throw;
        }
    }

    /// <summary>
    /// Render <paramref name="stage"/>'s prompt + tool surface onto the realtime backend.
    /// Invoked by <see cref="WorkflowExecutor"/> on initial entry and after every
    /// successful transition.
    /// </summary>
    private async ValueTask RenderStageAsync(CompiledStage stage, CancellationToken cancellationToken)
    {
        var (tools, prompt) = stage.GetStageToolsAndPrompt(_projector is null ? null : [_projector]);

        await StartResponseAsync(tools, prompt, cancellationToken).ConfigureAwait(false);

        await _emit.WriteAsync(
            new StrategyEvent.WorkflowStepEntered(stage.Id, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);

        await _emit.WriteAsync(
            new StrategyEvent.AgentSpeakingChanged(_agent.Id, _agent.Name, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);

        if (_executor.IsComplete)
        {
            await EndSessionAsync($"terminal stage '{stage.Id}' reached", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Render an inline authentication step: push the credential instruction + a
    /// <c>submit_credential</c> tool onto the realtime backend. The model asks the caller
    /// conversationally and calls the tool, which drives <see cref="WorkflowExecutor.SubmitCredentialAsync"/>.
    /// </summary>
    private async ValueTask RenderAuthAsync(AuthStepRender render, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Identity verification required");
        sb.AppendLine("You must verify the caller before continuing. Do NOT fulfil the caller's request until verification succeeds.");
        sb.AppendLine();

        if (render.Challenge is { } challenge)
        {
            sb.AppendLine(challenge.Prompt);
        }

        foreach (var request in render.Requests)
        {
            var line = request.RealtimeInstruction
                ?? $"Ask the caller for {request.Purpose}, then call submit_credential with authenticator \"{request.AuthenticatorName}\" and the value they gave.";
            sb.AppendLine($"- {line}");
        }

        if (render.Requests.Count > 1)
        {
            var names = string.Join("\" or \"", render.Requests.Select(r => r.AuthenticatorName));
            sb.AppendLine($"The caller may verify with any of: \"{names}\".");
        }

        var submitTool = AIFunctionFactory.Create(
            SubmitCredentialToolAsync,
            name: "submit_credential");

        await StartResponseAsync([submitTool], sb.ToString(), cancellationToken).ConfigureAwait(false);

        await _emit.WriteAsync(
            new StrategyEvent.AgentSpeakingChanged(_agent.Id, _agent.Name, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
    }

    [Description("Submit a credential value the caller provided (PIN digits, account last-4, or one-time code) to verify their identity.")]
    private async Task<string> SubmitCredentialToolAsync(
        [Description("The authenticator to satisfy, e.g. \"Pin\", \"SmsOtp\", or \"IdentifyLast4\".")] string authenticator,
        [Description("The value the caller supplied (digits or one-time code).")] string value,
        CancellationToken cancellationToken = default)
    {
        await _executor.SubmitCredentialAsync(authenticator, new CredentialInput(value), cancellationToken).ConfigureAwait(false);
        var progress = _projector?.Get<AuthSnapshot>().GetRequirement(authenticator) ?? new CredentialProgress();
        return progress.Satisfied ? "Verified." : (progress.LastReason ?? "Not verified — please try again.");
    }


    private async Task PumpInboundAudioAsync(StrategyStartContext context, CancellationToken ct)
    {
        try
        {
            var session = EnsureAgentSession();

            await foreach (var frame in context.InboundAudio.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (_suspended)
                {
                    continue;
                }

                var dataContent = new DataContent(frame.Pcm, "audio/pcm");

                await _agent.SendAudioAsync(session, dataContent, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Realtime inbound audio pump terminated");
        }
    }

    /// <summary>
    /// Per-stage DTMF handling. For each tone:
    /// <list type="number">
    ///   <item>Emit <c>DtmfRecognized</c> for observability.</item>
    ///   <item>If the current stage has a scripted menu and the digit matches, advance via the mapped transition label.</item>
    ///   <item>Otherwise forward the digit to the realtime backend as an inline user text turn.</item>
    /// </list>
    /// </summary>
    private async Task PumpInboundDtmfAsync(StrategyStartContext context, CancellationToken ct)
    {
        try
        {
            await foreach (var tone in context.InboundDtmf.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (_suspended)
                {
                    continue;
                }

                var current = _executor.CurrentStage;
                await _emit.WriteAsync(
                    new StrategyEvent.DtmfRecognized(tone.Digit.ToString(), current?.Id, DateTimeOffset.UtcNow),
                    ct).ConfigureAwait(false);

                if (current?.Blueprint.Channels.Scripted is { MenuOptions: { Count: > 0 } menu }
                    && menu.TryGetValue(tone.Digit, out var option))
                {
                    var edge = current.FindEdgeByLabel(option.TransitionLabel);
                    if (edge is null)
                    {
                        _logger.LogWarning(
                            "Stage '{Stage}' DTMF menu maps digit '{Digit}' to label '{Label}', but no outgoing edge matches.",
                            current.Id, tone.Digit, option.TransitionLabel);
                        await ForwardDtmfAsTextAsync(tone, ct).ConfigureAwait(false);
                        continue;
                    }

                    var outcome = await _executor.AdvanceAlongAsync(edge, ct).ConfigureAwait(false);
                    if (outcome is AdvanceOutcome.Denied or AdvanceOutcome.Invalid)
                    {
                        var reason = outcome switch
                        {
                            AdvanceOutcome.Denied d => d.Reason,
                            AdvanceOutcome.Invalid i => i.Reason,
                            _ => "unknown",
                        };
                        await SendBackendNoteAsync($"[Caller pressed {tone.Digit}; transition denied: {reason}]", ct).ConfigureAwait(false);
                    }
                    continue;
                }

                await ForwardDtmfAsTextAsync(tone, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Realtime inbound DTMF pump terminated for call {CallId}", _callId);
        }
    }

    private async ValueTask SendUserTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var session = EnsureAgentSession();
        var userMessage = new ChatMessage(ChatRole.User, text);
        await _agent.SendAsync(session, userMessage, cancellationToken).ConfigureAwait(false);
    }

    private Task ForwardDtmfAsTextAsync(DtmfTone tone, CancellationToken ct) =>
        SendBackendNoteAsync($"[Caller pressed {tone.Digit}]", ct);

    private async Task SendBackendNoteAsync(string note, CancellationToken ct)
    {
        try
        {
            await SendUserTextAsync(note, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to surface inline note to backend: {Note}", note);
        }
    }

    private async ValueTask StartResponseAsync(IEnumerable<AITool>? tools = null, string? instruction = null, CancellationToken cancellationToken = default)
    {
        var session = EnsureAgentSession();

        if (tools is not null || !string.IsNullOrEmpty(instruction))
        {
            var clientSession = session.ClientSession
                ?? throw new InvalidOperationException(
                    $"{nameof(AuthorizingAIAgent)} has no active realtime client session.");

            var updated = new RealtimeSessionOptions()
            {
                Tools = tools?.ToList(),
                Instructions = instruction
            };
            await _agent.SendAsync(
                session,
                new SessionUpdateRealtimeClientMessage(updated),
                cancellationToken).ConfigureAwait(false);
        }
        await _agent.SendAsync(session, new CreateResponseRealtimeClientMessage(), cancellationToken).ConfigureAwait(false);
    }

    private async Task RunAgentLoopAsync(CancellationToken ct)
    {
        try
        {
            var session = EnsureAgentSession();

            await foreach (var update in _agent.RunStreamingAsync(session, null, ct).ConfigureAwait(false))
            {
                var role = update.Role;
                var speakerLabel = role == ChatRole.User ? "user" : "assistant";
                var at = update.CreatedAt ?? DateTimeOffset.UtcNow;

                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case DataContent dc when !dc.Data.IsEmpty && !_suspended:
                            await _outbound.Writer.WriteAsync(new OutboundDirective.Audio(new AudioFrame(dc.Data, at, SourceEdgeId: _agent.Id)), ct).ConfigureAwait(false);
                            break;

                        case AudioTranscriptionContent atc when !string.IsNullOrWhiteSpace(atc.Text):
                            // Realtime providers stream interim transcript fragments; mark non-final
                            // so observers know not to commit them as the user's final utterance.
                            await _emit.WriteAsync(new StrategyEvent.Transcript(speakerLabel, atc.Text, false, at), ct).ConfigureAwait(false);

                            break;

                        case TextContent tc when !string.IsNullOrWhiteSpace(tc.Text):
                            if (role == ChatRole.User)
                            {
                                // Some providers surface a final user transcript as TextContent.
                                await _emit.WriteAsync(new StrategyEvent.Transcript("user", tc.Text, true, at), ct).ConfigureAwait(false);
                            }
                            else
                            {
                                await _emit.WriteAsync(new StrategyEvent.AgentUtterance(_agent.Id, tc.Text, at), ct).ConfigureAwait(false);
                            }
                            break;

                        case FunctionCallContent fcc:
                            await _emit.WriteAsync(
                                new StrategyEvent.FunctionCalled(
                                    Name: fcc.Name,
                                    Arguments: fcc.Arguments is { } args
                                    ? new Dictionary<string, object?>(args)
                                    : new Dictionary<string, object?>(),
                                    CallId: fcc.CallId,
                                    At: at),
                                ct).ConfigureAwait(false);
                            break;

                        //case FunctionResultContent frc:
                        //    yield return new RealtimeBackendUpdate.FunctionResult(
                        //        CallId: frc.CallId,
                        //        Result: frc.Result,
                        //        At: at);
                        //    break;

                        case RealtimeVadContent vad when vad.VadEvent == VadEventType.InputSpeechStarted:
                            // Surface caller speech-start so the strategy can barge-in (cancel any in-flight agent audio).
                            await _outbound.Writer.WriteAsync(new OutboundDirective.StopPlayback(at), ct).ConfigureAwait(false);
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Realtime agent loop crashed");
            using (var faultSpan = _telemetry.StartChildActivity("contact_center.strategy.agent_loop.faulted", _callId))
            {
                CallingActivitySource.SetError(faultSpan, ex);
            }
            await _emit.WriteAsync(
                new StrategyEvent.Faulted(ex.Message, ex, DateTimeOffset.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
        }
    }
    private RealtimeAIAgentSession EnsureAgentSession() => _agentSession ?? throw new InvalidOperationException(
        $"{nameof(AuthorizingAIAgent)} is not connected. Call {nameof(ConnectBackendAsync)} first.");


    private async Task EndSessionAsync(string reason, CancellationToken ct)
    {
        await _emit.WriteAsync(
            new StrategyEvent.AgentUtterance(_agent.Id, $"[session ending: {reason}]", DateTimeOffset.UtcNow),
            CancellationToken.None).ConfigureAwait(false);
        await _cts.CancelAsync().ConfigureAwait(false);
    }
}

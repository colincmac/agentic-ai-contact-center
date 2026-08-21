using System.Text;
using System.Threading.Channels;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.State;
using Agents.AI.ContactCenter.State.Projections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Calling.Strategies.Dtmf;

/// <summary>
/// Synthesizes the current stage's SSML prompt (when configured), routes inbound DTMF tones via the
/// stage's scripted menu, and falls back to ignoring unmapped digits.
/// </summary>
/// <remarks>
/// Designed to run as the bottom tier of a <see cref="Composite.CompositeFallbackStrategy"/>;
/// preserves folded per-call workflow state across swaps through the shared call scope.
/// </remarks>
public sealed class DtmfCallWorkflowStrategy : IConversationStrategy
{
    private readonly CallWorkflowSession _session;
    private readonly ISpeechSynthesizer? _synthesizer;
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

    private readonly CancellationTokenSource _cts = new();
    private Task? _dtmfPump;
    private bool _suspended;
    private string _callId = string.Empty;
    private int _disposed;

    // Inline-auth collect mode: non-null while the executor is waiting on a credential.
    private AuthStepRender? _activeAuth;
    private readonly StringBuilder _digitBuffer = new();

    public DtmfCallWorkflowStrategy(
        CallWorkflowSession session,
        ISpeechSynthesizer? synthesizer = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _synthesizer = synthesizer;
        _logger = loggerFactory?.CreateLogger<DtmfCallWorkflowStrategy>()
            ?? NullLogger<DtmfCallWorkflowStrategy>.Instance;

        _emit = new StateFoldingChannelWriter(_events.Writer, () => _projector);
        _executor = new WorkflowExecutor(_session, _emit, RenderStageAsync, RenderAuthAsync, () => _projector);
    }

    public StrategyKind Kind => StrategyKind.Dtmf;

    public AgentTier Tier => AgentTier.DtmfOnly;

    public IvrSnapshot WorkflowState => _projector?.Get<IvrSnapshot>() ?? IvrSnapshot.Empty;

    public EdgeCapabilities EmittedDirectives => EdgeCapabilities.Audio | EdgeCapabilities.StopPlayback;

    public ChannelReader<OutboundDirective> Outbound => _outbound.Reader;

    public ChannelReader<StrategyEvent> Events => _events.Reader;

    public async Task StartAsync(StrategyStartContext context, CancellationToken cancellationToken = default)
    {
        if (_dtmfPump is not null) { return; }

        _callId = context.CallId;
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

        _dtmfPump = Task.Run(async () =>
        {
            try
            {
                await _executor.EnterAsync(linked.Token).ConfigureAwait(false);

                await foreach (var tone in context.InboundDtmf.ReadAllAsync(linked.Token).ConfigureAwait(false))
                {
                    if (_suspended) { continue; }
                    await HandleDtmfAsync(tone, linked.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DTMF strategy faulted for call {CallId}", _callId);
                await _emit.WriteAsync(
                    new StrategyEvent.Faulted(ex.Message, ex, DateTimeOffset.UtcNow),
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _outbound.Writer.TryComplete();
                _events.Writer.TryComplete();
            }
        }, CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_dtmfPump is not null) { try { await _dtmfPump.ConfigureAwait(false); } catch { } }
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
        // Leaving auth collect mode (plan satisfied / business stage).
        _activeAuth = null;
        _digitBuffer.Clear();

        await _emit.WriteAsync(
            new StrategyEvent.WorkflowStepEntered(stage.Id, DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);

        var scripted = stage.Blueprint.Channels.Scripted;
        if (scripted is null || _synthesizer is null)
        {
            _logger.LogDebug(
                "Stage '{Stage}' has no scripted prompt or synthesizer unavailable; skipping TTS rendering.",
                stage.Id);
            // No scripted prompt configured or no synthesizer available; nothing to render. Let an external strategy handle it or just wait for DTMF input. Note that the workflow may still emit a prompt directive if the stage has a non-scripted prompt configured; this check is specifically for whether the DTMF strategy should attempt to synthesize an SSML prompt from the scripted configuration.
            return;
        }

        var ssml = scripted.SsmlPrompt;
        if (string.IsNullOrWhiteSpace(ssml)) { return; }

        await SynthesizeAsync(ssml, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Render an inline authentication step on the DTMF surface: synthesize the credential's
    /// SSML prompt and enter digit-collect mode. Buffered digits are submitted to the executor
    /// on '#' or when the request's max length is reached.
    /// </summary>
    private async ValueTask RenderAuthAsync(AuthStepRender render, CancellationToken ct)
    {
        _activeAuth = render;
        _digitBuffer.Clear();

        // DTMF can't present a spoken choice cleanly; satisfy an anyOf with its first option.
        var request = render.Requests[0];

        var prompt = render.Challenge?.Prompt ?? request.SsmlPrompt;
        if (!string.IsNullOrWhiteSpace(prompt) && _synthesizer is not null)
        {
            await SynthesizeAsync(prompt, ct).ConfigureAwait(false);
        }
    }

    private async Task SynthesizeAsync(string ssml, CancellationToken ct)
    {
        if (_synthesizer is null) { return; }

        var format = LooksLikeSsml(ssml) ? SynthesizerInputFormat.SSML : SynthesizerInputFormat.Text;
        try
        {
            await foreach (var pcm in _synthesizer.SynthesizeAsync(ssml, format, ct).ConfigureAwait(false))
            {
                if (_suspended) { break; }
                await _outbound.Writer.WriteAsync(
                    new OutboundDirective.Audio(new AudioFrame(pcm, DateTimeOffset.UtcNow, SourceEdgeId: null)),
                    ct).ConfigureAwait(false);
            }

            await _emit.WriteAsync(
                new StrategyEvent.AgentUtterance("dtmf", ssml, DateTimeOffset.UtcNow),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TTS synthesis failed in DTMF strategy for call {CallId}", _callId);
        }
    }

    private async Task HandleDtmfAsync(DtmfTone tone, CancellationToken ct)
    {
        var current = _executor.CurrentStage;
        await _emit.WriteAsync(
            new StrategyEvent.DtmfRecognized(tone.Digit.ToString(), current?.Id, DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);

        // Inline authentication takes priority: buffer digits until the credential is complete.
        if (_activeAuth is { } auth)
        {
            await HandleAuthDigitAsync(auth, tone, ct).ConfigureAwait(false);
            return;
        }

        if (current?.Blueprint.Channels.Scripted is not { MenuOptions: { Count: > 0 } menu })
        {
            return;
        }

        if (!menu.TryGetValue(tone.Digit, out var option))
        {
            _logger.LogDebug(
                "Stage '{Stage}' has DTMF menu but digit '{Digit}' is not mapped; ignoring.",
                current.Id, tone.Digit);
            return;
        }

        var edge = current.FindEdgeByLabel(option.TransitionLabel);
        if (edge is null)
        {
            _logger.LogWarning(
                "Stage '{Stage}' DTMF maps digit '{Digit}' to label '{Label}', but no outgoing edge matches.",
                current.Id, tone.Digit, option.TransitionLabel);
            return;
        }

        await _executor.AdvanceAlongAsync(edge, ct).ConfigureAwait(false);
    }

    private async Task HandleAuthDigitAsync(AuthStepRender auth, DtmfTone tone, CancellationToken ct)
    {
        var request = auth.Requests[0];
        var max = request.MaxLength ?? 32;
        var min = request.MinLength ?? 1;

        if (tone.Digit == '#')
        {
            if (_digitBuffer.Length >= min)
            {
                await SubmitBufferedDigitsAsync(request.AuthenticatorName, ct).ConfigureAwait(false);
            }
            return;
        }
        if (tone.Digit == '*')
        {
            _digitBuffer.Clear();
            return;
        }

        _digitBuffer.Append(tone.Digit);
        if (_digitBuffer.Length >= max)
        {
            await SubmitBufferedDigitsAsync(request.AuthenticatorName, ct).ConfigureAwait(false);
        }
    }

    private async Task SubmitBufferedDigitsAsync(string authenticatorName, CancellationToken ct)
    {
        var value = _digitBuffer.ToString();
        _digitBuffer.Clear();
        await _executor.SubmitCredentialAsync(authenticatorName, new CredentialInput(value), ct).ConfigureAwait(false);
    }

    internal static bool LooksLikeSsml(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return false; }
        var span = text.AsSpan().TrimStart();
        return span.StartsWith("<speak", StringComparison.OrdinalIgnoreCase);
    }
}

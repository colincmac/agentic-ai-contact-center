using System.Threading.Channels;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Agents.AI.ContactCenter.Media.Signaling;
using Agents.AI.ContactCenter.State;
using Agents.AI.ContactCenter.State.Projections;

namespace Agents.AI.ContactCenter.Calling.Strategies.Dtmf;

/// <summary>Speech-independent ACS-verb adapter. The host supplies reachable recorded prompts.</summary>
public sealed class RecordedDtmfCallWorkflowStrategy : IConversationStrategy
{
    private readonly CallWorkflowSession _session;
    private readonly WorkflowExecutor _executor;
    private readonly Channel<OutboundDirective> _outbound = Channel.CreateBounded<OutboundDirective>(32);
    private readonly Channel<StrategyEvent> _events = Channel.CreateUnbounded<StrategyEvent>();
    private readonly ChannelWriter<StrategyEvent> _emit;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _inputGate = new(1, 1);
    private CallStateProjector? _projector;
    private StrategyStartContext? _context;
    private Task? _run;
    private string? _operation;
    private volatile bool _suspended;
    private int _disposed;

    public RecordedDtmfCallWorkflowStrategy(CallWorkflowSession session)
    {
        _session = session;
        _emit = new StateFoldingChannelWriter(_events.Writer, () => _projector);
        _executor = new(session, _emit, RenderAsync,
            (_, _) => throw new CredentialCaptureUnavailableException("No recorded credential-capture adapter is configured."),
            () => _projector);
    }

    public StrategyKind Kind => StrategyKind.Dtmf;
    public AgentTier Tier => AgentTier.DtmfOnly;
    public IvrSnapshot WorkflowState => _projector?.Get<IvrSnapshot>() ?? IvrSnapshot.Empty;
    public EdgeCapabilities EmittedDirectives => EdgeCapabilities.PlayFile | EdgeCapabilities.CollectDtmf;
    public ChannelReader<OutboundDirective> Outbound => _outbound.Reader;
    public ChannelReader<StrategyEvent> Events => _events.Reader;

    public Task StartAsync(StrategyStartContext context, CancellationToken cancellationToken = default)
    {
        if (_run is not null) { throw new InvalidOperationException("Recorded DTMF is already started."); }
        if (context.InboundSignals is null || context.Control is not { CanControl: true })
        {
            throw new InvalidOperationException("Recorded DTMF requires callback signals and call control.");
        }
        _context = context;
        _projector = context.StateProjector;
        _run = RunAsync(cancellationToken);
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        try
        {
            await CallerAuthenticationRunner.RunAsync(_context!, _session.Services, _emit,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, linked.Token).ConfigureAwait(false);
            await _executor.EnterAsync(linked.Token).ConfigureAwait(false);
            var digits = ReadDigitsAsync(linked.Token);
            var signals = ReadSignalsAsync(linked.Token);
            await Task.WhenAny(digits, signals).ConfigureAwait(false);
            await linked.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(digits, signals).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await _emit.WriteAsync(new StrategyEvent.Faulted("Recorded DTMF failed.", ex, DateTimeOffset.UtcNow), CancellationToken.None).ConfigureAwait(false);
            await linked.CancelAsync().ConfigureAwait(false);
        }
        finally { _outbound.Writer.TryComplete(); _events.Writer.TryComplete(); }
    }

    private async ValueTask RenderAsync(CompiledStage stage, CancellationToken ct)
    {
        if (!await CallWorkflowEligibility.CheckAsync(_session.Services, Tier, stage, _context?.EdgeCapabilities, _emit, ct).ConfigureAwait(false)) { return; }
        var scripted = stage.Blueprint.Channels.Scripted;
        var file = scripted?.AudioFile
            ?? throw new InvalidOperationException($"Stage '{stage.Id}' requires recorded audio for the speech-independent profile.");
        if (!stage.Terminal && stage.Blueprint.OnInputFailure is null)
        {
            throw new InvalidOperationException($"Stage '{stage.Id}' requires an explicit input-failure route.");
        }
        _operation = $"{stage.Id}:{Guid.NewGuid():N}";
        await _outbound.Writer.WriteAsync(new OutboundDirective.PlayFile(file, DateTimeOffset.UtcNow, _operation), ct).ConfigureAwait(false);
    }

    private async Task ReadDigitsAsync(CancellationToken ct)
    {
        await foreach (var digit in _context!.InboundDtmf.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (_suspended) { continue; }
            await _inputGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (digit.OperationContext is not null && digit.OperationContext != _operation) { continue; }
                var stage = _executor.CurrentStage!;
                var option = stage.Blueprint.Channels.Scripted?.MenuOptions.GetValueOrDefault(digit.Digit);
                var edge = option is null ? null : stage.FindEdgeByLabel(option.TransitionLabel);
                if (edge is null) { await _executor.HandleInputFailureAsync(ct).ConfigureAwait(false); }
                else { await _executor.AdvanceAlongAsync(edge, ct).ConfigureAwait(false); }
            }
            finally { _inputGate.Release(); }
        }
    }

    private async Task ReadSignalsAsync(CancellationToken ct)
    {
        await foreach (var signal in _context!.InboundSignals!.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (_suspended) { continue; }
            await _inputGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (signal.OperationContext != _operation) { continue; }
                if (signal.Kind == SessionSignalKind.PlayCompleted)
                {
                    _operation = $"{_executor.CurrentStage!.Id}:{Guid.NewGuid():N}";
                    if (_executor.CurrentStage!.Terminal)
                    {
                        await _context.Control!.HangUpAsync(true, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await _outbound.Writer.WriteAsync(new OutboundDirective.CollectDtmf(1, DateTimeOffset.UtcNow,
                            InitialSilenceTimeout: TimeSpan.FromSeconds(10), OperationContext: _operation), ct).ConfigureAwait(false);
                    }
                }
                else if (signal.Kind is SessionSignalKind.PlayFailed or SessionSignalKind.RecognizeFailed)
                {
                    await _executor.HandleInputFailureAsync(ct).ConfigureAwait(false);
                }
            }
            finally { _inputGate.Release(); }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_run is not null) { await _run.WaitAsync(cancellationToken).ConfigureAwait(false); }
    }
    public ValueTask SuspendAsync(CancellationToken cancellationToken = default) { _suspended = true; return ValueTask.CompletedTask; }
    public ValueTask ResumeAsync(CancellationToken cancellationToken = default) { _suspended = false; return ValueTask.CompletedTask; }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        await StopAsync().ConfigureAwait(false);
        _stop.Dispose();
        _inputGate.Dispose();
    }
}

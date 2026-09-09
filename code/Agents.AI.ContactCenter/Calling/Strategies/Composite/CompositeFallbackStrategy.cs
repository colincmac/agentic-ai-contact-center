using System.Threading.Channels;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Agents.AI.ContactCenter.Calling.Strategies;
using Agents.AI.ContactCenter.Calling.Core;
using Agents.AI.ContactCenter.State.Projections;
using Microsoft.Shared.Diagnostics;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.Calling.Strategies.Composite;

/// <summary>
/// Wraps an ordered list of <see cref="AgentTier"/> values. Starts the first one by
/// resolving an <see cref="IConversationStrategy"/> keyed by that tier from the per-call
/// service scope (<see cref="StrategyStartContext.Services"/>); on a
/// <see cref="StrategyEvent.Faulted"/> from the active inner, transparently resolves the
/// next tier from the same scope. Per-call <see cref="IvrWorkflowState"/> is preserved
/// across tier swaps because every inner strategy in the chain reads it from the scoped
/// registration in the call scope. The caller's edge is never touched — only the brain swaps.
/// </summary>
public sealed class CompositeFallbackStrategy : IConversationStrategy
{
    private readonly IReadOnlyList<AgentTier> _orderedTiers;
    private readonly CallWorkflowSession _workflowSession;
    private readonly CallTierAdmission _tierAdmission;
    private readonly ILogger _logger;
    private readonly IOptionsMonitor<AgentTierOptions>? _options;

    private readonly Channel<OutboundDirective> _outbound = Channel.CreateBounded<OutboundDirective>(
        new BoundedChannelOptions(500)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    private readonly Channel<StrategyEvent> _events = Channel.CreateUnbounded<StrategyEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _swapLock = new();
    private readonly SemaphoreSlim _degradationGate = new(1, 1);

    private StrategyStartContext? _startContext;
    private IConversationStrategy? _active;
    private CancellationTokenSource? _activePumpCts;
    private Task? _activePumps;
    private Task? _stopTask;
    private int _tierIndex = -1;
    private int _started;
    private int _disposed;

    public CompositeFallbackStrategy(
        IEnumerable<AgentTier> orderedTiers,
        CallWorkflowSession workflowSession,
        CallTierAdmission tierAdmission,
        ILoggerFactory? loggerFactory = null,
        IOptionsMonitor<AgentTierOptions>? options = null)
    {
        _orderedTiers = Throw.IfNullOrEmpty(orderedTiers).ToList();
        _workflowSession = Throw.IfNull(workflowSession);
        _tierAdmission = Throw.IfNull(tierAdmission);
        _options = options ?? workflowSession.Services.GetService<IOptionsMonitor<AgentTierOptions>>();

        for (var i = 0; i < _orderedTiers.Count; i++)
        {
            if (!Enum.IsDefined(_orderedTiers[i])
                || (i > 0 && (int)_orderedTiers[i] <= (int)_orderedTiers[i - 1]))
            {
                throw new ArgumentException("Fallback tiers must be known, unique, and in degradation order.", nameof(orderedTiers));
            }
        }

        _logger = loggerFactory?.CreateLogger<CompositeFallbackStrategy>() ?? NullLogger<CompositeFallbackStrategy>.Instance;
    }

    public StrategyKind Kind => StrategyKind.Composite;

    public AgentTier Tier => _active?.Tier ?? _orderedTiers[Math.Clamp(_tierIndex, 0, _orderedTiers.Count - 1)];

    public IvrSnapshot WorkflowState => _active?.WorkflowState ?? IvrSnapshot.Empty;

    public ChannelReader<OutboundDirective> Outbound => _outbound.Reader;

    public EdgeCapabilities EmittedDirectives => _active?.EmittedDirectives ?? EdgeCapabilities.None;

    public ChannelReader<StrategyEvent> Events => _events.Reader;

    public async Task StartAsync(StrategyStartContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The composite strategy has already started.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        var entered = false;
        try
        {
            await _degradationGate.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            _startContext = context;
            var initialIndex = _tierAdmission.Tier is { } admitted
                ? FindTierIndex(admitted, 0)
                : 0;
            await ActivateAsync(initialIndex, "initial", linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (entered)
            {
                await FailAsync(ex, publishFault: !linked.IsCancellationRequested).ConfigureAwait(false);
            }
            else
            {
                await _tierAdmission.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            if (entered)
            {
                _degradationGate.Release();
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_swapLock)
        {
            return _stopTask ??= StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        Exception? cancellationFailure = null;
        try { await _cts.CancelAsync().ConfigureAwait(false); }
        catch (Exception ex) { cancellationFailure = ex; }
        await _degradationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            try { await CleanupActiveAsync().ConfigureAwait(false); }
            finally { await _tierAdmission.ReleaseAsync(CancellationToken.None).ConfigureAwait(false); }
            if (cancellationFailure is not null)
            {
                throw cancellationFailure;
            }
        }
        catch (Exception ex)
        {
            _events.Writer.TryWrite(new StrategyEvent.Faulted("Failed to stop the strategy or release call admission.", ex, DateTimeOffset.UtcNow));
            throw;
        }
        finally
        {
            _outbound.Writer.TryComplete();
            _events.Writer.TryComplete();
            _degradationGate.Release();
        }
    }

    public ValueTask SuspendAsync(CancellationToken cancellationToken = default)
        => _active?.SuspendAsync(cancellationToken) ?? ValueTask.CompletedTask;

    public ValueTask ResumeAsync(CancellationToken cancellationToken = default)
        => _active?.ResumeAsync(cancellationToken) ?? ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        await StopAsync().ConfigureAwait(false);
    }

    private async Task ActivateAsync(
        int targetIndex,
        string reason,
        CancellationToken ct,
        AgentTier? degradedFrom = null)
    {
        Exception? lastFailure = null;
        while (targetIndex < _orderedTiers.Count)
        {
            ct.ThrowIfCancellationRequested();
            var context = _startContext!;
            var currentStepId = context.StateProjector?.Get<IvrSnapshot>().CurrentStepId;
            var stage = currentStepId is null
                ? _workflowSession.Workflow.InitialStage
                : _workflowSession.Workflow.GetStage(currentStepId);
            var tier = _orderedTiers[targetIndex];
            _tierIndex = targetIndex;
            IConversationStrategy? next = null;
            try
            {
                if (!IsEnabled(tier))
                {
                    throw new InvalidOperationException($"Tier {tier} is disabled or unconfigured.");
                }

                var policy = _workflowSession.Services.GetService<CallInteractionPolicy>();
                if (policy is null
                    ? stage.Blueprint.InteractionProfiles.Count != 0
                    : !policy.Allows(tier, stage, context.EdgeCapabilities))
                {
                    throw new InvalidOperationException($"Tier {tier} is not eligible for stage '{stage.Id}'.");
                }

                next = _workflowSession.Services
                    .GetRequiredKeyedService<ILeafConversationStrategyFactory>(tier).Create();
                if (next.Tier != tier)
                {
                    throw new InvalidOperationException($"The strategy for {tier} reported a different tier.");
                }
                if (context.EdgeCapabilities is { } edge
                    && (edge & next.EmittedDirectives) != next.EmittedDirectives)
                {
                    throw new InvalidOperationException(
                        $"Tier {tier} emits directives unsupported by the caller edge.");
                }
                // Start the drains before StartAsync: a valid startup prompt can exceed
                // the leaf's bounded buffer. The activation gate fences fault reactions.
                lock (_swapLock)
                {
                    _active = next;
                    _activePumpCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    _activePumps = Task.WhenAll(
                        PumpAudioAsync(next, _activePumpCts.Token),
                        PumpEventsAsync(next, _activePumpCts.Token));
                }
                await next.StartAsync(context, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
            catch (Exception ex)
            {
                if (next is not null)
                {
                    if (ReferenceEquals(_active, next)) { await CleanupActiveAsync().ConfigureAwait(false); }
                    else { await StopAndDisposeAsync(next).ConfigureAwait(false); }
                }
                ct.ThrowIfCancellationRequested();
                lastFailure = ex;
                degradedFrom ??= tier;
                _logger.LogWarning(ex, "Strategy tier {Tier} failed; requesting fallback admission", tier);
                targetIndex = await GetFallbackIndexAsync(targetIndex, ct).ConfigureAwait(false);
                continue;
            }

            if (degradedFrom is { } from)
            {
                _events.Writer.TryWrite(new StrategyEvent.TierDegraded(from, tier, reason, DateTimeOffset.UtcNow));
            }
            return;
        }
        ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException("No fallback available", lastFailure);
    }

    private bool IsEnabled(AgentTier tier)
    {
        if (_options is null)
        {
            return true; // Explicit low-level composites without options retain their supplied chain.
        }
        var options = _options.CurrentValue;
        options.Validate();
        return options.FallbackOrder.Contains(tier)
            && options.Tiers.TryGetValue(tier, out var config)
            && config.Enabled && config.MaxConcurrent is > 0;
    }

    private async ValueTask<int> GetFallbackIndexAsync(int currentIndex, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_tierAdmission.UsesResolver)
        {
            return currentIndex + 1;
        }

        var next = await _tierAdmission.MoveToFallbackAsync(ct).ConfigureAwait(false);
        if (next is null)
        {
            return _orderedTiers.Count;
        }

        var index = FindTierIndex(next.Value, currentIndex + 1);
        if (index == _orderedTiers.Count)
        {
            throw new InvalidOperationException($"Admitted fallback tier {next} is not in the remaining strategy chain.");
        }
        return index;
    }

    private async Task CleanupActiveAsync()
    {
        IConversationStrategy? active;
        CancellationTokenSource? pumpCts;
        Task? pumps;
        lock (_swapLock)
        {
            active = _active;
            pumpCts = _activePumpCts;
            pumps = _activePumps;
            _active = null;
            _activePumpCts = null;
            _activePumps = null;
        }
        try
        {
            try
            {
                if (pumpCts is not null)
                {
                    await pumpCts.CancelAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                if (active is not null)
                {
                    await StopAndDisposeAsync(active).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            try
            {
                if (pumps is not null)
                {
                    await pumps.ConfigureAwait(false);
                }
            }
            finally
            {
                pumpCts?.Dispose();
            }
        }
        if (active is not null && !_cts.IsCancellationRequested)
        {
            // The old producer has stopped. Discard its queued audio before the next
            // tier starts, then stop any audio already dispatched to the physical edge.
            while (_outbound.Reader.TryRead(out _)) { }
            if (!_outbound.Writer.TryWrite(new OutboundDirective.StopPlayback(DateTimeOffset.UtcNow)))
            {
                throw new InvalidOperationException("Unable to fence output from the retired strategy.");
            }
        }
    }

    private static async Task StopAndDisposeAsync(IConversationStrategy strategy)
    {
        Exception? stopFailure = null;
        try { await strategy.StopAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { stopFailure = ex; }
        try { await strategy.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) when (stopFailure is not null)
        {
            throw new AggregateException(stopFailure, ex);
        }
        if (stopFailure is not null)
        {
            throw stopFailure;
        }
    }

    private async Task FailAsync(Exception failure, bool publishFault = true)
    {
        try
        {
            try { await CleanupActiveAsync().ConfigureAwait(false); }
            finally { await _tierAdmission.ReleaseAsync(CancellationToken.None).ConfigureAwait(false); }
        }
        catch (Exception cleanupFailure)
        {
            failure = new AggregateException(failure, cleanupFailure);
            publishFault = true;
        }
        if (publishFault)
        {
            _events.Writer.TryWrite(new StrategyEvent.Faulted(failure.Message, failure, DateTimeOffset.UtcNow));
        }
        _outbound.Writer.TryComplete();
        _events.Writer.TryComplete();
    }

    private async Task PumpAudioAsync(IConversationStrategy inner, CancellationToken ct)
    {
        try
        {
            await foreach (var directive in inner.Outbound.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await _outbound.Writer.WriteAsync(directive, ct).ConfigureAwait(false);
            }
            if (!ct.IsCancellationRequested)
            {
                RequestDegradation(inner, new StrategyEvent.Faulted(
                    "Inner outbound stream completed unexpectedly.", null, DateTimeOffset.UtcNow));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* swap or shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Composite audio pump terminated for inner {Tier}", inner.Tier);
            RequestDegradation(inner, new StrategyEvent.Faulted(
                "Inner outbound reader faulted.", ex, DateTimeOffset.UtcNow));
        }
    }

    private async Task PumpEventsAsync(IConversationStrategy inner, CancellationToken ct)
    {
        try
        {
            await foreach (var ev in inner.Events.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (ev is StrategyEvent.Faulted fault)
                {
                    // Don't surface the inner Faulted to observers as a call-killing fault —
                    // we'll emit TierDegraded after the swap completes.
                    _logger.LogInformation("Inner strategy {Tier} reported Faulted: {Message}; degrading",
                        inner.Tier, fault.Message);
                    RequestDegradation(inner, fault);
                    return;
                }
                await _events.Writer.WriteAsync(ev, ct).ConfigureAwait(false);
            }
            if (!ct.IsCancellationRequested)
            {
                RequestDegradation(inner, new StrategyEvent.Faulted(
                    "Inner event stream completed unexpectedly.", null, DateTimeOffset.UtcNow));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* swap or shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Composite event pump terminated for inner {Tier}", inner.Tier);
            RequestDegradation(inner, new StrategyEvent.Faulted(
                "Inner event reader faulted.", ex, DateTimeOffset.UtcNow));
        }
    }

    private void RequestDegradation(IConversationStrategy inner, StrategyEvent.Faulted fault)
    {
        lock (_swapLock)
        {
            if (!ReferenceEquals(_active, inner) || _cts.IsCancellationRequested)
            {
                return;
            }
        }
        _ = CompleteDegradationAsync(inner, fault);
    }

    private async Task CompleteDegradationAsync(
        IConversationStrategy inner,
        StrategyEvent.Faulted fault)
    {
        var entered = false;
        try
        {
            await _degradationGate.WaitAsync(_cts.Token).ConfigureAwait(false);
            entered = true;

            int currentIndex;
            lock (_swapLock)
            {
                if (!ReferenceEquals(_active, inner))
                {
                    return;
                }
                currentIndex = _tierIndex;
            }

            if (_options?.CurrentValue.AllowMidCallDegradation == false)
            {
                throw new InvalidOperationException("Mid-call degradation is disabled.", fault.Exception);
            }

            await CleanupActiveAsync().ConfigureAwait(false);
            var targetIndex = await GetFallbackIndexAsync(currentIndex, _cts.Token).ConfigureAwait(false);
            await ActivateAsync(
                targetIndex,
                fault.Message,
                _cts.Token,
                inner.Tier).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { /* shutdown owns cleanup */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Composite fallback failed");
            await FailAsync(ex).ConfigureAwait(false);
        }
        finally
        {
            if (entered)
            {
                _degradationGate.Release();
            }
        }
    }

    private int FindTierIndex(AgentTier tier, int startIndex)
    {
        for (var i = startIndex; i < _orderedTiers.Count; i++)
        {
            if (_orderedTiers[i] == tier)
            {
                return i;
            }
        }
        return _orderedTiers.Count;
    }
}

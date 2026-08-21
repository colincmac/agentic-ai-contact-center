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

    private readonly Channel<OutboundDirective> _outbound = Channel.CreateBounded<OutboundDirective>(
        new BoundedChannelOptions(500)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
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
    private Task? _degradationTask;
    private int _tierIndex = -1;
    private int _disposed;

    public CompositeFallbackStrategy(
        IEnumerable<AgentTier> orderedTiers,
        CallWorkflowSession workflowSession,
        CallTierAdmission tierAdmission,
        ILoggerFactory? loggerFactory = null)
    {
        _orderedTiers = Throw.IfNullOrEmpty(orderedTiers).ToList();
        _workflowSession = Throw.IfNull(workflowSession);
        _tierAdmission = Throw.IfNull(tierAdmission);

        _logger = loggerFactory?.CreateLogger<CompositeFallbackStrategy>() ?? NullLogger<CompositeFallbackStrategy>.Instance;
    }

    public StrategyKind Kind => StrategyKind.Composite;

    public AgentTier Tier => _active?.Tier ?? _orderedTiers[Math.Max(0, _tierIndex)];

    public IvrSnapshot WorkflowState => _active?.WorkflowState ?? IvrSnapshot.Empty;

    public ChannelReader<OutboundDirective> Outbound => _outbound.Reader;

    public EdgeCapabilities EmittedDirectives => _active?.EmittedDirectives ?? EdgeCapabilities.None;

    public ChannelReader<StrategyEvent> Events => _events.Reader;

    public Task StartAsync(StrategyStartContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _startContext = context;
        return ActivateAsync(targetIndex: 0, reason: "initial", cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        Task? degradation;
        lock (_swapLock) { degradation = _degradationTask; }
        if (degradation is not null)
        {
            try { await degradation.ConfigureAwait(false); } catch { /* shutdown */ }
        }

        IConversationStrategy? toStop;
        CancellationTokenSource? pumpCts;
        Task? pumps;
        lock (_swapLock)
        {
            toStop = _active;
            pumpCts = _activePumpCts;
            pumps = _activePumps;
            _active = null;
            _activePumpCts = null;
            _activePumps = null;
        }

        if (pumpCts is not null)
        {
            try { await pumpCts.CancelAsync().ConfigureAwait(false); } catch { /* shutdown */ }
        }

        if (toStop is not null)
        {
            try { await toStop.StopAsync(cancellationToken).ConfigureAwait(false); } catch { /* shutdown */ }
        }

        if (pumps is not null)
        {
            try { await pumps.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        pumpCts?.Dispose();

        _outbound.Writer.TryComplete();
        _events.Writer.TryComplete();
    }

    public ValueTask SuspendAsync(CancellationToken cancellationToken = default)
        => _active?.SuspendAsync(cancellationToken) ?? ValueTask.CompletedTask;

    public ValueTask ResumeAsync(CancellationToken cancellationToken = default)
        => _active?.ResumeAsync(cancellationToken) ?? ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        IConversationStrategy? active;
        lock (_swapLock) { active = _active; }

        await StopAsync().ConfigureAwait(false);

        if (active is not null)
        {
            try { await active.DisposeAsync().ConfigureAwait(false); } catch { /* shutdown */ }
        }

        _cts.Dispose();
        _degradationGate.Dispose();
    }

    private async Task ActivateAsync(
        int targetIndex,
        string reason,
        CancellationToken ct,
        AgentTier? degradedFrom = null)
    {
        if (_cts.IsCancellationRequested)
        {
            return;
        }

        if (targetIndex >= _orderedTiers.Count)
        {
            _logger.LogError("No fallback available; composite exhausted");
            await _events.Writer.WriteAsync(
                new StrategyEvent.Faulted("No fallback available", null, DateTimeOffset.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
            _outbound.Writer.TryComplete();
            _events.Writer.TryComplete();
            return;
        }

        var tier = _orderedTiers[targetIndex];
        IConversationStrategy next;
        try
        {
            next = _workflowSession.Services
                .GetRequiredKeyedService<ILeafConversationStrategyFactory>(tier)
                .Create();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No IConversationStrategy registered for tier {Tier}; trying next", tier);
            await ActivateAsync(targetIndex + 1, $"resolve-failed:{tier}", ct, degradedFrom).ConfigureAwait(false);
            return;
        }

        IConversationStrategy? previous;
        CancellationTokenSource? previousPumpCts;
        Task? previousPumps;
        AgentTier? previousTier;
        CancellationTokenSource nextPumpCts;
        Task nextPumps;

        lock (_swapLock)
        {
            previous = _active;
            previousPumpCts = _activePumpCts;
            previousPumps = _activePumps;
            previousTier = degradedFrom ?? previous?.Tier;
            nextPumpCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            nextPumps = Task.WhenAll(
                PumpAudioAsync(next, nextPumpCts.Token),
                PumpEventsAsync(next, nextPumpCts.Token));
            _active = next;
            _tierIndex = targetIndex;
            _activePumpCts = nextPumpCts;
            _activePumps = nextPumps;
        }

        // Stop the previous AFTER the swap so its faulted event doesn't race a new one.
        if (previousPumpCts is not null)
        {
            try { await previousPumpCts.CancelAsync().ConfigureAwait(false); } catch { /* tolerated */ }
        }
        if (previous is not null)
        {
            try { await previous.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* tolerated */ }
            if (previousPumps is not null)
            {
                try { await previousPumps.ConfigureAwait(false); } catch { /* tolerated */ }
            }
            try { await previous.DisposeAsync().ConfigureAwait(false); } catch { /* tolerated */ }
        }
        previousPumpCts?.Dispose();

        try
        {
            await next.StartAsync(_startContext!, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!_cts.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "IConversationStrategy tier {Tier} failed to start; trying next", tier);
            lock (_swapLock)
            {
                if (ReferenceEquals(_active, next))
                {
                    _active = null;
                    _activePumpCts = null;
                    _activePumps = null;
                }
            }
            try { await nextPumpCts.CancelAsync().ConfigureAwait(false); } catch { /* tolerated */ }
            try { await next.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* tolerated */ }
            try { await nextPumps.ConfigureAwait(false); } catch { /* tolerated */ }
            try { await next.DisposeAsync().ConfigureAwait(false); } catch { /* tolerated */ }
            nextPumpCts.Dispose();

            await ActivateAsync(
                targetIndex + 1,
                $"start-failed:{tier}",
                ct,
                previousTier).ConfigureAwait(false);
            return;
        }

        if (previousTier is { } from)
        {
            await _events.Writer.WriteAsync(
                new StrategyEvent.TierDegraded(from, next.Tier, reason, DateTimeOffset.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
        }
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
        TaskCompletionSource? owner = null;
        lock (_swapLock)
        {
            if (!ReferenceEquals(_active, inner) || _cts.IsCancellationRequested)
            {
                return;
            }

            if (_degradationTask is null || _degradationTask.IsCompleted)
            {
                owner = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _degradationTask = owner.Task;
            }
        }

        if (owner is not null)
        {
            _ = CompleteDegradationAsync(owner, inner, fault);
        }
    }

    private async Task CompleteDegradationAsync(
        TaskCompletionSource completion,
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

            var targetIndex = currentIndex + 1;
            if (_tierAdmission.UsesResolver)
            {
                var admittedTier = await _tierAdmission
                    .MoveToFallbackAsync(_cts.Token)
                    .ConfigureAwait(false);
                targetIndex = admittedTier is { } tier
                    ? FindTierIndex(tier, currentIndex + 1)
                    : _orderedTiers.Count;
            }

            await ActivateAsync(
                targetIndex,
                fault.Message,
                _cts.Token,
                inner.Tier).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Composite fallback handler crashed");
        }
        finally
        {
            if (entered)
            {
                _degradationGate.Release();
            }
            completion.TrySetResult();
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

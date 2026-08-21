using System.Collections.Concurrent;
using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.Coordination.Core;

/// <summary>
/// Background service that implements <see cref="IPodHeartbeat"/> per
/// ADR-0011: renews the local <see cref="IPodLeaseStore"/> entry and every
/// tracked owned-call lease at the configured heartbeat cadence, and runs
/// the cross-pod reaper sweep on a separate (slower) cadence.
/// </summary>
public sealed class PodHeartbeatService : BackgroundService, IPodHeartbeat
{
    private readonly ConcurrentDictionary<string, CallOwnershipKind> _trackedCalls
        = new(StringComparer.Ordinal);

    private readonly IPodLeaseStore _podLeases;
    private readonly ICallOwnershipDirectory _ownership;
    private readonly IOptionsMonitor<HyperscaleOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PodHeartbeatService> _logger;

    public PodHeartbeatService(
        IPodLeaseStore podLeases,
        ICallOwnershipDirectory ownership,
        IOptionsMonitor<HyperscaleOptions> options,
        TimeProvider timeProvider,
        ILogger<PodHeartbeatService> logger)
    {
        _podLeases = podLeases;
        _ownership = ownership;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public IReadOnlyDictionary<string, CallOwnershipKind> TrackedCalls => _trackedCalls;

    public void TrackOwnedCall(string callConnectionId, CallOwnershipKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callConnectionId);
        _trackedCalls[callConnectionId] = kind;
    }

    public void UntrackOwnedCall(string callConnectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callConnectionId);
        _trackedCalls.TryRemove(callConnectionId, out _);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var heartbeatInterval = _options.CurrentValue.PodHeartbeat.HeartbeatInterval;
        using var timer = new PeriodicTimer(heartbeatInterval, _timeProvider);

        await RunHeartbeatTickAsync(stoppingToken).ConfigureAwait(false);

        var nextReap = _timeProvider.GetUtcNow() + _options.CurrentValue.PodHeartbeat.ReaperInterval;

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RunHeartbeatTickAsync(stoppingToken).ConfigureAwait(false);

                var settings = _options.CurrentValue.PodHeartbeat;
                if (!settings.ReaperEnabled)
                {
                    continue;
                }

                var now = _timeProvider.GetUtcNow();
                if (now < nextReap)
                {
                    continue;
                }

                nextReap = now + settings.ReaperInterval;
                await RunReaperSweepAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        var settings = _options.CurrentValue.PodHeartbeat;
        if (!settings.ReleasePodLeaseOnStop)
        {
            return;
        }

        var drainTimeout = settings.DrainTimeout > TimeSpan.Zero
            ? settings.DrainTimeout
            : TimeSpan.FromSeconds(5);

        using var timeout = new CancellationTokenSource(drainTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await _podLeases.ReleaseAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Pod lease release exceeded drain timeout {DrainTimeout}; lease will expire via TTL.", drainTimeout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Pod lease release on shutdown failed; lease will expire via TTL.");
        }
    }

    internal async Task RunHeartbeatTickAsync(CancellationToken cancellationToken)
    {
        var leaseDuration = _options.CurrentValue.PodHeartbeat.LeaseDuration;

        try
        {
            await _podLeases.RenewAsync(leaseDuration, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Pod lease renew failed; another tick will retry.");
            return;
        }

        foreach (var entry in _trackedCalls)
        {
            try
            {
                var renewed = await _ownership.RenewAsync(entry.Key, entry.Value, cancellationToken).ConfigureAwait(false);
                if (!renewed)
                {
                    _trackedCalls.TryRemove(entry.Key, out _);
                    _logger.LogWarning("Call {CallConnectionId} ownership lease was reaped by another pod; untracked locally.", entry.Key);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Owned-call renew failed for {CallConnectionId}; will retry next tick.", entry.Key);
            }
        }
    }

    internal async Task<int> RunReaperSweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reaped = await _ownership.ReapOrphansAsync(_podLeases, cancellationToken).ConfigureAwait(false);
            if (reaped > 0)
            {
                _logger.LogInformation("Reaped {Count} orphaned call ownership leases.", reaped);
            }
            return reaped;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Pod ownership reaper sweep failed; will retry next cycle.");
            return 0;
        }
    }
}

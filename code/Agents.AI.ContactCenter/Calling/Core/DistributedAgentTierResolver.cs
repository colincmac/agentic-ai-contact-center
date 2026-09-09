using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Coordination;
using Agents.AI.ContactCenter.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Agents.AI.ContactCenter.Calling.Strategies;

namespace Agents.AI.ContactCenter.Calling.Core;

/// <summary>
/// Distributed <see cref="IAgentTierResolver"/> that composes the per-cluster
/// <see cref="ITierCeilingProvider"/> (ADR-0008) and the per-tier
/// <see cref="IDistributedCapacityTracker"/> (ADR-0004) into a single
/// atomic admit decision.
/// </summary>
/// <remarks>
/// <para>
/// On every resolve the resolver walks the configured fallback order, skips
/// tiers that are disabled or ranked above the active ceiling, computes the
/// effective per-tier cap from <see cref="AgentTierConfig.MaxConcurrent"/>,
/// and calls <see cref="IDistributedCapacityTracker.TryAdmitAsync"/> to
/// compare-and-increment in one round trip. The first tier whose admit
/// succeeds is returned; if none do, the resolver throws
/// <see cref="CapacityExhaustedException"/>.
/// </para>
/// <para>
/// "Above the ceiling" means numerically lower
/// <see cref="AgentTier"/> (higher quality) per ADR-0008. A ceiling of
/// <c>ChatCompletionTts</c> bars admission to <c>RealtimeVoice</c> but
/// permits <c>ChatCompletionTts</c> and everything below.
/// </para>
/// </remarks>
public sealed class DistributedAgentTierResolver : IAgentTierResolver
{
    private readonly IOptionsMonitor<AgentTierOptions> _options;
    private readonly ITierCeilingProvider _ceilingProvider;
    private readonly IDistributedCapacityTracker _capacityTracker;
    private readonly ILogger<DistributedAgentTierResolver> _logger;
    private readonly IOptionsMonitor<HyperscaleOptions>? _hyperscaleOptions;
    private readonly IServiceProviderIsKeyedService? _strategyServices;

    public DistributedAgentTierResolver(
        IOptionsMonitor<AgentTierOptions> options,
        ITierCeilingProvider ceilingProvider,
        IDistributedCapacityTracker capacityTracker,
        ILogger<DistributedAgentTierResolver> logger,
        IOptionsMonitor<HyperscaleOptions>? hyperscaleOptions = null,
        IServiceProviderIsKeyedService? strategyServices = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _ceilingProvider = ceilingProvider ?? throw new ArgumentNullException(nameof(ceilingProvider));
        _capacityTracker = capacityTracker ?? throw new ArgumentNullException(nameof(capacityTracker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hyperscaleOptions = hyperscaleOptions;
        _strategyServices = strategyServices;
    }

    public async ValueTask<AgentTier> ResolveAsync(AgentTier? preferredTier = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var opts = _options.CurrentValue;
        opts.Validate();
        var ceiling = _ceilingProvider.Current;
        var order = BuildResolveOrder(opts.FallbackOrder, preferredTier);

        foreach (var tier in order)
        {
            var admit = await TryAdmitTierAsync(tier, opts, ceiling, cancellationToken).ConfigureAwait(false);
            if (admit.HasValue)
            {
                return admit.Value;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new CapacityExhaustedException();
    }

    public async ValueTask<AgentTier?> ResolveFallbackAsync(AgentTier currentTier, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var opts = _options.CurrentValue;
        opts.Validate();
        var ceiling = _ceilingProvider.Current;

        foreach (var tier in opts.FallbackOrder)
        {
            if ((int)tier <= (int)currentTier)
            {
                continue;
            }

            var admit = await TryAdmitTierAsync(tier, opts, ceiling, cancellationToken).ConfigureAwait(false);
            if (admit.HasValue)
            {
                return admit.Value;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }

    public ValueTask ReleaseAsync(AgentTier tier, CancellationToken cancellationToken = default)
        => new(_capacityTracker.ReleaseAsync(tier, cancellationToken));

    private async ValueTask<AgentTier?> TryAdmitTierAsync(
        AgentTier tier,
        AgentTierOptions opts,
        AgentTier ceiling,
        CancellationToken cancellationToken)
    {
        if ((int)tier < (int)ceiling)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!opts.Tiers.TryGetValue(tier, out var cfg) || !cfg.Enabled
            || cfg.MaxConcurrent is not { } rawCap)
        {
            return null;
        }

        if (_strategyServices is not null
            && !_strategyServices.IsKeyedService(typeof(ILeafConversationStrategyFactory), tier)
            && !_strategyServices.IsKeyedService(typeof(IConversationStrategy), tier))
        {
            return null;
        }

        var cap = ApplyClusterShare(rawCap);
        if (cap <= 0)
        {
            return null;
        }

        var result = await _capacityTracker.TryAdmitAsync(tier, cap, cancellationToken).ConfigureAwait(false);
        if (!result.Admitted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogDebug(
                "Tier {Tier} refused admission at count {Count}/{Cap}; falling through.",
                tier,
                result.Count,
                cap);
            return null;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            await _capacityTracker.ReleaseAsync(tier, CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        _logger.LogDebug("Admitted to tier {Tier} at count {Count}/{Cap}.", tier, result.Count, cap);
        return tier;
    }

    /// <summary>
    /// Scales <paramref name="rawCap"/> by the configured
    /// <see cref="CapacityCoordinationOptions.ClusterShare"/> per ADR-0010.
    /// Floors fractional results so the cluster under-admits rather than
    /// over-admits at the boundary; clamps an out-of-range share to
    /// <c>(0, 1]</c> for the same reason. Pass-through when no
    /// <see cref="HyperscaleOptions"/> monitor is registered, when share is
    /// at the ceiling.
    /// </summary>
    private long ApplyClusterShare(long rawCap)
    {
        if (_hyperscaleOptions is null)
        {
            return rawCap;
        }

        var share = _hyperscaleOptions.CurrentValue.CapacityCoordination.ClusterShare;
        if (double.IsNaN(share) || share <= 0)
        {
            return 0;
        }
        if (share >= 1.0)
        {
            return rawCap;
        }

        return (long)Math.Floor(rawCap * share);
    }

    private static IEnumerable<AgentTier> BuildResolveOrder(IReadOnlyList<AgentTier> baseOrder, AgentTier? preferred)
    {
        if (preferred is not { } p || !baseOrder.Contains(p))
        {
            return baseOrder;
        }

        return BuildOrderWithPreferred(baseOrder, p);

        static IEnumerable<AgentTier> BuildOrderWithPreferred(IReadOnlyList<AgentTier> order, AgentTier preferred)
        {
            yield return preferred;
            foreach (var tier in order)
            {
                if (tier != preferred)
                {
                    yield return tier;
                }
            }
        }
    }
}

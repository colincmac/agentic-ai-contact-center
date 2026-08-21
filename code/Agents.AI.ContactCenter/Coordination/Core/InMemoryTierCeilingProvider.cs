using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.Coordination.Core;

/// <summary>
/// In-process <see cref="ITierCeilingProvider"/> for single-pod dev / Aspire
/// and for the per-pod fallback in ADR-0004's degraded-mode admission
/// contract. <see cref="SetAsync"/> updates the local field directly; there
/// is no Pub/Sub fan-out because there are no peers.
/// </summary>
public sealed class InMemoryTierCeilingProvider : ITierCeilingProvider
{
    private int _current;

    public InMemoryTierCeilingProvider(IOptions<HyperscaleOptions> options)
    {
        _current = (int)options.Value.TierCeiling.DefaultCeiling;
    }

    public AgentTier Current => (AgentTier)Volatile.Read(ref _current);

    public Task<AgentTier> RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Current);
    }

    public Task SetAsync(AgentTier ceiling, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _current, (int)ceiling);
        return Task.CompletedTask;
    }
}

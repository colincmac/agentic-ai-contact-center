using Agents.AI.ContactCenter.Configuration;

namespace Agents.AI.ContactCenter.Calling.Core;

/// <summary>Scoped, exactly-once ownership of the distributed capacity slot for a call.</summary>
public sealed class CallTierAdmission : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IAgentTierResolver? _resolver;
    private AgentTier? _tier;
    private int _released;

    public AgentTier? Tier => _tier;

    public bool UsesResolver => _resolver is not null;

    internal void Initialize(IAgentTierResolver? resolver, AgentTier tier)
    {
        if (_tier is not null)
        {
            throw new InvalidOperationException("The call tier admission has already been initialized.");
        }

        _resolver = resolver;
        _tier = tier;
    }

    public async ValueTask<AgentTier?> MoveToFallbackAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_resolver is null || _tier is not { } current || Volatile.Read(ref _released) != 0)
            {
                return null;
            }

            var next = await _resolver.ResolveFallbackAsync(current, cancellationToken).ConfigureAwait(false);
            if (next is null)
            {
                return null;
            }

            try
            {
                await _resolver.ReleaseAsync(current, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await _resolver.ReleaseAsync(next.Value, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            _tier = next;
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ReleaseAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        if (_resolver is not null && _tier is { } tier)
        {
            await _resolver.ReleaseAsync(tier, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
        _gate.Dispose();
    }
}

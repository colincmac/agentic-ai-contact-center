using Agents.AI.ContactCenter.Configuration;

namespace Agents.AI.ContactCenter.Calling.Core;

/// <summary>Scoped, exactly-once ownership of the distributed capacity slot for a call.</summary>
public sealed class CallTierAdmission : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IAgentTierResolver? _resolver;
    private AgentTier? _tier;
    private int _released;
    private bool _initialized;

    public AgentTier? Tier => _tier;

    public bool UsesResolver => _resolver is not null;

    internal void Initialize(IAgentTierResolver? resolver, AgentTier tier)
    {
        _gate.Wait();
        try
        {
            if (_initialized || _released != 0)
            {
                throw new InvalidOperationException("The call tier admission has already been initialized or released.");
            }

            _resolver = resolver;
            _tier = tier;
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
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

            if (cancellationToken.IsCancellationRequested)
            {
                await _resolver.ReleaseAsync(next.Value, CancellationToken.None).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            // Transfer local ownership before releasing. If the backend fails, cleanup
            // still owns the new slot and must not retry an ambiguous old-slot release.
            _tier = next;
            await _resolver.ReleaseAsync(current, CancellationToken.None).ConfigureAwait(false);
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ReleaseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_released != 0)
            {
                return;
            }

            _released = 1;
            if (_resolver is not null && _tier is { } tier)
            {
                await _resolver.ReleaseAsync(tier, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
    }
}

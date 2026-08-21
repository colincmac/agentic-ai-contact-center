namespace Agents.AI.ContactCenter.Coordination.Core;

/// <summary>
/// No-op <see cref="IWebhookForwarder"/> for dev / single-pod scenarios.
/// Returns <see cref="WebhookForwardOutcome.LocalOwner"/> when the owner
/// matches the local <see cref="IClusterIdentity"/> and
/// <see cref="WebhookForwardOutcome.OwnerUnreachable"/> otherwise so the
/// caller drops to the reaper / local-fallback path per ADR-0011.
/// </summary>
public sealed class NullWebhookForwarder : IWebhookForwarder
{
    private readonly IClusterIdentity _identity;

    public NullWebhookForwarder(IClusterIdentity identity)
    {
        _identity = identity;
    }

    public Task<WebhookForwardResult> TryForwardAsync(
        CallOwnership owner,
        string callbackPath,
        ReadOnlyMemory<byte> body,
        string contentType,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        cancellationToken.ThrowIfCancellationRequested();

        var outcome = string.Equals(owner.ClusterId, _identity.ClusterId, StringComparison.Ordinal)
            && string.Equals(owner.PodId, _identity.PodId, StringComparison.Ordinal)
                ? WebhookForwardOutcome.LocalOwner
                : WebhookForwardOutcome.OwnerUnreachable;

        return Task.FromResult(new WebhookForwardResult(outcome, StatusCode: null));
    }
}

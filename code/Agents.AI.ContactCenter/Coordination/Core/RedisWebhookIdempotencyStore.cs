using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Agents.AI.ContactCenter.Coordination.Core;

/// <summary>
/// Distributed <see cref="IWebhookIdempotencyStore"/> backed by Azure Managed
/// Redis Enterprise (or any Redis cluster) per ADR-0004. Uses
/// <c>SET dedup:{callConnectionId}:sequenceNumber 1 NX EX</c>; the boolean
/// result is the dedup verdict directly.
/// </summary>
/// <remarks>
/// Relies on an <see cref="IConnectionMultiplexer"/> already registered in DI
/// (typically by Aspire's <c>AddRedisClient</c> wiring).
/// </remarks>
public sealed class RedisWebhookIdempotencyStore : IWebhookIdempotencyStore
{
    private static readonly RedisValue Token = "1";

    private readonly IConnectionMultiplexer _connection;
    private readonly TimeSpan _ttl;

    public RedisWebhookIdempotencyStore(IConnectionMultiplexer connection, IOptions<HyperscaleOptions> options)
    {
        _connection = connection;
        _ttl = options.Value.WebhookIdempotency.TokenLifetime;
    }

    public async Task<bool> TryRegisterAsync(string callConnectionId, int sequenceNumber, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var db = _connection.GetDatabase();
        var key = CoordinationRedisKeys.Dedup(callConnectionId, sequenceNumber);
        return await db.StringSetAsync(key, Token, _ttl, when: When.NotExists).ConfigureAwait(false);
    }
}

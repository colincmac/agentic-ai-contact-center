using StackExchange.Redis;

namespace Agents.AI.ContactCenter.Coordination.Core;

/// <summary>
/// Distributed <see cref="IPodLeaseStore"/> backed by Azure Managed Redis
/// Enterprise (or any Redis cluster) per ADR-0011. Writes
/// <c>pod:lease:{clusterId}:{podId}</c> with TTL and a value carrying the
/// local <see cref="IClusterIdentity.InstanceId"/>; release is a compare-and-DEL
/// Lua script so a re-launched pod cannot accidentally delete the fresh
/// process's lease.
/// </summary>
public sealed class RedisPodLeaseStore : IPodLeaseStore
{
    private static readonly LuaScript ReleaseScript = LuaScript.Prepare(@"
local v = redis.call('GET', @key)
if not v then return 0 end
if v ~= @instanceId then return 0 end
redis.call('DEL', @key)
return 1
");

    private readonly IConnectionMultiplexer _connection;
    private readonly IClusterIdentity _identity;

    public RedisPodLeaseStore(IConnectionMultiplexer connection, IClusterIdentity identity)
    {
        _connection = connection;
        _identity = identity;
    }

    public async Task RenewAsync(TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var db = _connection.GetDatabase();
        var key = CoordinationRedisKeys.PodLease(_identity.ClusterId, _identity.PodId);
        await db.StringSetAsync(key, _identity.InstanceId, leaseDuration).ConfigureAwait(false);
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var db = _connection.GetDatabase();
        var key = (RedisKey)CoordinationRedisKeys.PodLease(_identity.ClusterId, _identity.PodId);

        await db.ScriptEvaluateAsync(ReleaseScript, new
        {
            key,
            instanceId = (RedisValue)_identity.InstanceId,
        }).ConfigureAwait(false);
    }

    public async Task<bool> IsAliveAsync(string clusterId, string podId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var db = _connection.GetDatabase();
        return await db.KeyExistsAsync(CoordinationRedisKeys.PodLease(clusterId, podId)).ConfigureAwait(false);
    }
}

using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Agents.AI.ContactCenter.Coordination.Core;

/// <summary>
/// Distributed <see cref="ICallOwnershipDirectory"/> backed by Azure Managed
/// Redis Enterprise (or any Redis cluster) per ADR-0011. Acquire is
/// <c>SET … NX PX</c>; renew and release are server-side compare-and-X Lua
/// scripts that match on the local pod's <see cref="IClusterIdentity.InstanceId"/>
/// so a reaped lease cannot be silently reclaimed by a stale process.
/// </summary>
/// <remarks>
/// Relies on an <see cref="IConnectionMultiplexer"/> already registered in DI
/// (typically by Aspire's <c>AddRedisClient</c> wiring).
/// </remarks>
public sealed class RedisCallOwnershipDirectory : ICallOwnershipDirectory
{
    // Compare-and-renew: extract the InstanceId prefix (everything before the
    // first '|') and proceed only when it matches ARGV[1].
    private static readonly LuaScript RenewScript = LuaScript.Prepare(@"
local v = redis.call('GET', @key)
if not v then return 0 end
local sep = string.find(v, '|', 1, true)
if not sep then return 0 end
if string.sub(v, 1, sep - 1) ~= @instanceId then return 0 end
redis.call('SET', @key, @value, 'PX', @ttlMs)
return 1
");

    private static readonly LuaScript ReleaseScript = LuaScript.Prepare(@"
local v = redis.call('GET', @key)
if not v then return 0 end
local sep = string.find(v, '|', 1, true)
if not sep then return 0 end
if string.sub(v, 1, sep - 1) ~= @instanceId then return 0 end
redis.call('DEL', @key)
return 1
");

    // Reap-by-value: only delete when the value still matches the snapshot we
    // decoded; two reapers racing the same orphan cannot both succeed.
    private static readonly LuaScript ReapScript = LuaScript.Prepare(@"
local v = redis.call('GET', @key)
if not v then return 0 end
if v ~= @expectedValue then return 0 end
redis.call('DEL', @key)
return 1
");

    private readonly IConnectionMultiplexer _connection;
    private readonly IClusterIdentity _identity;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _leaseDuration;

    public RedisCallOwnershipDirectory(
        IConnectionMultiplexer connection,
        IClusterIdentity identity,
        IOptions<HyperscaleOptions> options,
        TimeProvider timeProvider)
    {
        _connection = connection;
        _identity = identity;
        _timeProvider = timeProvider;
        _leaseDuration = options.Value.CallOwnership.LeaseDuration;
    }

    public async Task<CallOwnershipAcquireResult> TryAcquireAsync(string callConnectionId, CallOwnershipKind kind, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var db = _connection.GetDatabase();
        var key = CoordinationRedisKeys.Ownership(callConnectionId);
        var newOwner = BuildLocalOwner(kind, _timeProvider.GetUtcNow());
        var encoded = CallOwnershipCodec.Encode(newOwner);

        var acquired = await db.StringSetAsync(key, encoded, _leaseDuration, when: When.NotExists).ConfigureAwait(false);
        if (acquired)
        {
            return new CallOwnershipAcquireResult(true, newOwner);
        }

        var existingValue = await db.StringGetAsync(key).ConfigureAwait(false);
        if (existingValue.IsNullOrEmpty)
        {
            // Lease vanished between SET NX and GET (TTL hit). Retry once.
            return await TryAcquireAsync(callConnectionId, kind, cancellationToken).ConfigureAwait(false);
        }

        return new CallOwnershipAcquireResult(false, CallOwnershipCodec.Decode(existingValue!));
    }

    public async Task<CallOwnership?> GetOwnerAsync(string callConnectionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var db = _connection.GetDatabase();
        var value = await db.StringGetAsync(CoordinationRedisKeys.Ownership(callConnectionId)).ConfigureAwait(false);
        return value.IsNullOrEmpty ? null : CallOwnershipCodec.Decode(value!);
    }

    public async Task<bool> RenewAsync(string callConnectionId, CallOwnershipKind kind, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var db = _connection.GetDatabase();
        var key = (RedisKey)CoordinationRedisKeys.Ownership(callConnectionId);
        var renewed = BuildLocalOwner(kind, _timeProvider.GetUtcNow());
        var encoded = CallOwnershipCodec.Encode(renewed);

        var result = await db.ScriptEvaluateAsync(RenewScript, new
        {
            key,
            instanceId = (RedisValue)_identity.InstanceId,
            value = (RedisValue)encoded,
            ttlMs = (RedisValue)(long)_leaseDuration.TotalMilliseconds,
        }).ConfigureAwait(false);

        return (long)result == 1;
    }

    public async Task<bool> ReleaseAsync(string callConnectionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var db = _connection.GetDatabase();
        var key = (RedisKey)CoordinationRedisKeys.Ownership(callConnectionId);

        var result = await db.ScriptEvaluateAsync(ReleaseScript, new
        {
            key,
            instanceId = (RedisValue)_identity.InstanceId,
        }).ConfigureAwait(false);

        return (long)result == 1;
    }

    public async Task<int> ReapOrphansAsync(IPodLeaseStore podLeases, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(podLeases);
        cancellationToken.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow();
        var db = _connection.GetDatabase();
        var reaped = 0;

        foreach (var server in _connection.GetServers())
        {
            if (!server.IsConnected || server.IsReplica)
            {
                continue;
            }

            await foreach (var key in server.KeysAsync(pattern: "owner:*").WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var raw = await db.StringGetAsync(key).ConfigureAwait(false);
                if (raw.IsNullOrEmpty)
                {
                    continue;
                }

                CallOwnership owner;
                try
                {
                    owner = CallOwnershipCodec.Decode(raw!);
                }
                catch (FormatException)
                {
                    continue;
                }

                if (owner.LeaseUntil > now)
                {
                    continue;
                }

                var alive = await podLeases.IsAliveAsync(owner.ClusterId, owner.PodId, cancellationToken).ConfigureAwait(false);
                if (alive)
                {
                    continue;
                }

                var deleted = await db.ScriptEvaluateAsync(ReapScript, new
                {
                    key = (RedisKey)key,
                    expectedValue = raw,
                }).ConfigureAwait(false);

                if ((long)deleted == 1)
                {
                    reaped++;
                }
            }
        }

        return reaped;
    }

    private CallOwnership BuildLocalOwner(CallOwnershipKind kind, DateTimeOffset now) =>
        new(_identity.ClusterId, _identity.PodId, _identity.InstanceId, kind, now + _leaseDuration);
}

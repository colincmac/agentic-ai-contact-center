using Agents.AI.ContactCenter.Configuration;
using StackExchange.Redis;

namespace Agents.AI.ContactCenter.Coordination.Core;

/// <summary>
/// Distributed <see cref="IDistributedCapacityTracker"/> backed by Azure
/// Managed Redis Enterprise (or any Redis cluster) per ADR-0004. Admit is a
/// server-side compare-and-<c>INCR</c> Lua script so a race across pods
/// cannot over-admit; release is a clamped <c>DECR</c> so a double-release
/// (e.g., session end + reaper sweep) cannot drive the counter negative.
/// </summary>
/// <remarks>
/// Relies on an <see cref="IConnectionMultiplexer"/> already registered in DI
/// (typically by Aspire's <c>AddRedisClient</c> wiring). No TTL is set on the
/// counter key per ADR-0004 ("TTL'd by lease, not call") — the ADR-0011
/// reaper is responsible for releasing orphaned admits when their owning
/// pod's <c>pod:lease:*</c> expires.
/// </remarks>
public sealed class RedisDistributedCapacityTracker : IDistributedCapacityTracker
{
    // Atomic compare-and-INCR. Returns {admitted, count} where admitted is
    // 1 when the increment happened and 0 when refused; count is the value
    // after the operation (post-INCR on admit, unchanged on refuse).
    private static readonly LuaScript AdmitScript = LuaScript.Prepare(@"
local current = tonumber(redis.call('GET', @key) or '0')
local cap = tonumber(@cap)
if current >= cap then
    return {0, current}
end
local new_count = redis.call('INCR', @key)
return {1, new_count}
");

    // Clamped DECR. Returns the count after the operation; a DECR that
    // would drive the counter below zero is suppressed.
    private static readonly LuaScript ReleaseScript = LuaScript.Prepare(@"
local current = tonumber(redis.call('GET', @key) or '0')
if current <= 0 then
    return 0
end
return redis.call('DECR', @key)
");

    private readonly IConnectionMultiplexer _connection;

    public RedisDistributedCapacityTracker(IConnectionMultiplexer connection)
    {
        _connection = connection;
    }

    public async Task<CapacityAdmissionResult> TryAdmitAsync(AgentTier tier, long cap, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var db = _connection.GetDatabase();
        var key = (RedisKey)CoordinationRedisKeys.CapacityCounter(tier);

        var result = await db.ScriptEvaluateAsync(AdmitScript, new
        {
            key,
            cap = (RedisValue)cap,
        }).ConfigureAwait(false);

        var values = (RedisResult[])result!;
        return new CapacityAdmissionResult(Admitted: (long)values[0] == 1, Count: (long)values[1]);
    }

    public async Task ReleaseAsync(AgentTier tier, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var db = _connection.GetDatabase();
        var key = (RedisKey)CoordinationRedisKeys.CapacityCounter(tier);

        await db.ScriptEvaluateAsync(ReleaseScript, new { key }).ConfigureAwait(false);
    }

    public async Task<long> GetCountAsync(AgentTier tier, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var db = _connection.GetDatabase();
        var value = await db.StringGetAsync(CoordinationRedisKeys.CapacityCounter(tier)).ConfigureAwait(false);
        return value.IsNullOrEmpty ? 0L : (long)value;
    }
}

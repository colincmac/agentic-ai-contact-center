using System.Globalization;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Exceptions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Agents.AI.ContactCenter.State.Stores;

/// <summary>
/// Redis-backed <see cref="ICallStateStore"/>. The per-call snapshot is a Redis hash keyed by
/// <see cref="CallStateRedisKeys.Snapshot"/> — one field per slice plus version/timestamp metadata.
/// Saves use a compare-and-set Lua script on the version field so a stale writer is rejected with
/// <see cref="CallStateConcurrencyException"/>, mirroring the optimistic-concurrency contract of the
/// in-memory and Cosmos stores.
/// </summary>
/// <remarks>Relies on an <see cref="IConnectionMultiplexer"/> registered in DI (typically Aspire's <c>AddRedisClient</c>).</remarks>
public sealed class RedisCallStateStore : ICallStateStore
{
    // Returns {1, newVersion} on success, {0, currentVersion} on a version mismatch.
    private const string SaveScript = @"
local cur = tonumber(redis.call('HGET', KEYS[1], ARGV[1])) or 0
if cur ~= tonumber(ARGV[2]) then return {0, cur} end
local newv = cur + 1
redis.call('HSET', KEYS[1], ARGV[1], newv, ARGV[3], ARGV[4], ARGV[5], ARGV[6])
for i = 8, #ARGV, 2 do
  redis.call('HSET', KEYS[1], ARGV[i], ARGV[i+1])
end
local ttl = tonumber(ARGV[7])
if ttl > 0 then redis.call('PEXPIRE', KEYS[1], ttl) end
return {1, newv}
";

    private readonly IConnectionMultiplexer _connection;
    private readonly CallStateOptions _options;

    public RedisCallStateStore(IConnectionMultiplexer connection, IOptions<CallStateOptions> options)
    {
        _connection = connection;
        _options = options.Value;
    }

    public async ValueTask<CallStateSnapshot> SaveAsync(CallStateSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        var db = _connection.GetDatabase();
        var key = (RedisKey)CallStateRedisKeys.Snapshot(snapshot.CallId);
        var updatedAt = DateTimeOffset.UtcNow;
        var ttlMs = _options.Ttl is { } ttl ? (long)ttl.TotalMilliseconds : 0;

        var args = new List<RedisValue>
        {
            CallStateRedisKeys.VersionField,
            snapshot.Version,
            CallStateRedisKeys.UpdatedAtField,
            updatedAt.ToString("O", CultureInfo.InvariantCulture),
            CallStateRedisKeys.SequenceField,
            snapshot.EventSequence,
            ttlMs,
        };
        foreach (var (sliceId, json) in snapshot.Slices)
        {
            args.Add(sliceId);
            args.Add(json);
        }

        var result = (RedisResult[]?)await db.ScriptEvaluateAsync(SaveScript, [key], [.. args]).ConfigureAwait(false);
        if (result is null || result.Length < 2)
        {
            throw new InvalidOperationException($"Unexpected Redis response saving call state for '{snapshot.CallId}'.");
        }

        var status = (long)result[0];
        var version = (long)result[1];
        if (status == 0)
        {
            throw new CallStateConcurrencyException(snapshot.CallId, snapshot.Version, version);
        }

        return snapshot with { Version = version, UpdatedAt = updatedAt };
    }

    public async ValueTask<CallStateSnapshot?> LoadAsync(string callId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var db = _connection.GetDatabase();
        var entries = await db.HashGetAllAsync(CallStateRedisKeys.Snapshot(callId)).ConfigureAwait(false);
        if (entries.Length == 0)
        {
            return null;
        }

        long version = 0;
        long eventSequence = 0;
        var updatedAt = DateTimeOffset.UtcNow;
        var slices = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var field = entry.Name.ToString();
            if (field == CallStateRedisKeys.VersionField)
            {
                version = (long)entry.Value;
            }
            else if (field == CallStateRedisKeys.SequenceField)
            {
                eventSequence = (long)entry.Value;
            }
            else if (field == CallStateRedisKeys.UpdatedAtField)
            {
                _ = DateTimeOffset.TryParse(entry.Value.ToString(), CultureInfo.InvariantCulture, out updatedAt);
            }
            else
            {
                slices[field] = entry.Value.ToString();
            }
        }

        return new CallStateSnapshot
        {
            CallId = callId,
            Version = version,
            EventSequence = eventSequence,
            Slices = slices,
            UpdatedAt = updatedAt,
        };
    }

    public async ValueTask DeleteAsync(string callId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var db = _connection.GetDatabase();
        await db.KeyDeleteAsync(CallStateRedisKeys.Snapshot(callId)).ConfigureAwait(false);
    }
}

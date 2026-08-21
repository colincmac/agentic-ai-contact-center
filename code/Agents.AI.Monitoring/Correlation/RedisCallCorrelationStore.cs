using System.Text.Json;
using System.Text.Json.Serialization;
using StackExchange.Redis;

namespace Agents.AI.Monitoring.Correlation;

/// <summary>
/// Redis-backed <see cref="ICallCorrelationStore"/> for cross-pod, durable correlation.
/// Stores the context as JSON under <c>corr:e2e:{id}</c> and maintains pointer keys for the
/// context id and the ACS server/connection ids so any boundary identifier resolves the call.
/// Opt in with <c>AddRedisCallCorrelationStore()</c>; requires a registered
/// <see cref="IConnectionMultiplexer"/> (already present in the contact-center host).
/// </summary>
internal sealed class RedisCallCorrelationStore : ICallCorrelationStore
{
    private const string KeyPrefix = "corr:";
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(8);
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        private static readonly LuaScript MergeScript = LuaScript.Prepare(@"
local incoming = cjson.decode(@json)
local currentJson = redis.call('GET', @key)
if currentJson then
    local current = cjson.decode(currentJson)
    for field, value in pairs(incoming) do
        current[field] = value
    end
    incoming = current
end
local merged = cjson.encode(incoming)
redis.call('SET', @key, merged, 'PX', @ttlMs)
return merged
");

    private readonly IConnectionMultiplexer _redis;

    public RedisCallCorrelationStore(IConnectionMultiplexer redis) => _redis = redis;

    public async Task<CallCorrelationContext> GetOrCreateByServerCallIdAsync(
        CallCorrelationContext candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.AcsServerCallId);
        cancellationToken.ThrowIfCancellationRequested();

        var db = _redis.GetDatabase();
        var serverKey = ServerKey(candidate.AcsServerCallId);
        var candidateJson = JsonSerializer.Serialize(candidate, JsonOptions);
        var created = await db.StringSetAsync(serverKey, candidateJson, Ttl, when: When.NotExists).ConfigureAwait(false);
        var canonical = created
            ? candidate
            : JsonSerializer.Deserialize<CallCorrelationContext>((await db.StringGetAsync(serverKey).ConfigureAwait(false)).ToString(), JsonOptions)
                ?? throw new InvalidOperationException($"Correlation context for server call '{candidate.AcsServerCallId}' is invalid.");

        await UpsertAsync(canonical, cancellationToken).ConfigureAwait(false);
        return canonical;
    }

    public async Task UpsertAsync(CallCorrelationContext context, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        var json = JsonSerializer.Serialize(context, JsonOptions);
        var tasks = new List<Task>
        {
            MergeAsync(db, E2EKey(context.E2ECallId), json),
        };

        if (!string.IsNullOrEmpty(context.AcsServerCallId))
        {
            tasks.Add(MergeAsync(db, ServerKey(context.AcsServerCallId), json));
        }

        AddPointer(db, tasks, context.ContextId, context.E2ECallId);
        AddPointer(db, tasks, context.AcsServerCallId, context.E2ECallId);
        AddPointer(db, tasks, context.AcsCallConnectionId, context.E2ECallId);

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public Task<CallCorrelationContext?> GetByE2ECallIdAsync(string e2eCallId, CancellationToken cancellationToken = default)
        => LoadAsync(e2eCallId);

    public Task<CallCorrelationContext?> GetByContextIdAsync(string contextId, CancellationToken cancellationToken = default)
        => ResolveAsync(contextId);

    public Task<CallCorrelationContext?> GetByCallKeyAsync(string callKey, CancellationToken cancellationToken = default)
        => ResolveAsync(callKey);

    private async Task<CallCorrelationContext?> ResolveAsync(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        var byServerCall = await LoadServerAsync(key).ConfigureAwait(false);
        if (byServerCall is not null)
        {
            return byServerCall;
        }

        var direct = await LoadAsync(key).ConfigureAwait(false);
        if (direct is not null)
        {
            return direct;
        }

        var db = _redis.GetDatabase();
        var e2e = await db.StringGetAsync(PointerKey(key)).ConfigureAwait(false);
        return e2e.HasValue ? await LoadAsync(e2e.ToString()).ConfigureAwait(false) : null;
    }

    private async Task<CallCorrelationContext?> LoadAsync(string e2eCallId)
    {
        var db = _redis.GetDatabase();
        var json = await db.StringGetAsync(E2EKey(e2eCallId)).ConfigureAwait(false);
        return json.HasValue ? JsonSerializer.Deserialize<CallCorrelationContext>(json.ToString(), JsonOptions) : null;
    }

    private async Task<CallCorrelationContext?> LoadServerAsync(string serverCallId)
    {
        var json = await _redis.GetDatabase().StringGetAsync(ServerKey(serverCallId)).ConfigureAwait(false);
        return json.HasValue ? JsonSerializer.Deserialize<CallCorrelationContext>(json.ToString(), JsonOptions) : null;
    }

    private static void AddPointer(IDatabase db, List<Task> tasks, string? key, string e2eCallId)
    {
        if (!string.IsNullOrEmpty(key) && !string.Equals(key, e2eCallId, StringComparison.Ordinal))
        {
            tasks.Add(db.StringSetAsync(PointerKey(key), e2eCallId, Ttl));
        }
    }

    private static async Task MergeAsync(IDatabase db, RedisKey key, string json)
    {
        await db.ScriptEvaluateAsync(MergeScript, new
        {
            key,
            json = (RedisValue)json,
            ttlMs = (RedisValue)(long)Ttl.TotalMilliseconds
        }).ConfigureAwait(false);
    }

    private static RedisKey E2EKey(string e2eCallId) => $"{KeyPrefix}e2e:{e2eCallId}";

    private static RedisKey ServerKey(string serverCallId) => $"{KeyPrefix}server:{{{serverCallId}}}";

    private static RedisKey PointerKey(string key) => $"{KeyPrefix}ptr:{key}";
}

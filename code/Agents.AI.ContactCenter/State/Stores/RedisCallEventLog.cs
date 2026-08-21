using System.Globalization;
using System.Runtime.CompilerServices;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Agents.AI.ContactCenter.State.Stores;

/// <summary>
/// Redis Streams-backed <see cref="ICallEventLog"/>. Each call's events live in a stream keyed by
/// <see cref="CallStateRedisKeys.EventStream"/>; a companion counter provides the gap-free monotonic
/// sequence returned to callers and used to filter replays.
/// </summary>
/// <remarks>Relies on an <see cref="IConnectionMultiplexer"/> registered in DI (typically Aspire's <c>AddRedisClient</c>).</remarks>
public sealed class RedisCallEventLog : ICallEventLog
{
    private const string SequenceField = "seq";
    private const string TypeField = "type";
    private const string PayloadField = "json";

    private readonly IConnectionMultiplexer _connection;
    private readonly CallStateOptions _options;

    public RedisCallEventLog(IConnectionMultiplexer connection, IOptions<CallStateOptions> options)
    {
        _connection = connection;
        _options = options.Value;
    }

    public async ValueTask<long> AppendAsync(string callId, StrategyEvent strategyEvent, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        ArgumentNullException.ThrowIfNull(strategyEvent);
        cancellationToken.ThrowIfCancellationRequested();

        var db = _connection.GetDatabase();
        var sequence = await db.StringIncrementAsync(CallStateRedisKeys.EventSequence(callId)).ConfigureAwait(false);

        await db.StreamAddAsync(
            CallStateRedisKeys.EventStream(callId),
            [
                new NameValueEntry(SequenceField, sequence),
                new NameValueEntry(TypeField, StrategyEventSerializer.TypeName(strategyEvent)),
                new NameValueEntry(PayloadField, StrategyEventSerializer.Serialize(strategyEvent)),
            ]).ConfigureAwait(false);

        if (_options.Ttl is { } ttl)
        {
            await db.KeyExpireAsync(CallStateRedisKeys.EventStream(callId), ttl).ConfigureAwait(false);
            await db.KeyExpireAsync(CallStateRedisKeys.EventSequence(callId), ttl).ConfigureAwait(false);
        }

        return sequence;
    }

    public async IAsyncEnumerable<CallEventEnvelope> ReadAsync(
        string callId,
        long afterSequence = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var db = _connection.GetDatabase();
        var entries = await db.StreamRangeAsync(CallStateRedisKeys.EventStream(callId)).ConfigureAwait(false);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sequence = ReadLong(entry, SequenceField);
            if (sequence <= afterSequence)
            {
                continue;
            }

            var typeName = entry[TypeField].ToString();
            var json = entry[PayloadField].ToString();
            var strategyEvent = StrategyEventSerializer.Deserialize(typeName, json);
            if (strategyEvent is null)
            {
                continue;
            }

            yield return new CallEventEnvelope
            {
                CallId = callId,
                Sequence = sequence,
                Event = strategyEvent,
            };
        }
    }

    private static long ReadLong(StreamEntry entry, string field)
        => long.TryParse(entry[field].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
}

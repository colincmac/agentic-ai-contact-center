using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Configuration;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.State.Stores;

/// <summary>
/// Azure Cosmos DB-backed <see cref="ICallEventLog"/>. Events are documents in the event container,
/// partitioned by <c>/callId</c>, with <c>id = {callId}:{sequence}</c>. A per-call counter document
/// (patched with an atomic increment) supplies the gap-free monotonic sequence. All reads and writes
/// use the Cosmos stream APIs so the log is independent of the client's configured serializer.
/// </summary>
/// <remarks>Relies on a <see cref="CosmosClient"/> registered in DI (typically Aspire's <c>AddAzureCosmosClient</c>).</remarks>
public sealed class CosmosCallEventLog : ICallEventLog
{
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerOptions.Default)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Container _container;
    private readonly CallStateOptions _options;

    public CosmosCallEventLog(CosmosClient client, IOptions<CallStateOptions> options)
    {
        _options = options.Value;
        _container = client.GetContainer(_options.CosmosDatabaseName, _options.CosmosEventContainerName);
    }

    public async ValueTask<long> AppendAsync(string callId, StrategyEvent strategyEvent, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        ArgumentNullException.ThrowIfNull(strategyEvent);

        var partition = new PartitionKey(callId);
        var sequence = await NextSequenceAsync(callId, partition, cancellationToken).ConfigureAwait(false);

        var document = new EventDocument
        {
            Id = $"{callId}:{sequence:D12}",
            CallId = callId,
            Sequence = sequence,
            Type = StrategyEventSerializer.TypeName(strategyEvent),
            Payload = StrategyEventSerializer.Serialize(strategyEvent),
            RecordedAt = DateTimeOffset.UtcNow,
            Ttl = _options.Ttl is { } ttl ? (int)ttl.TotalSeconds : null,
        };

        using var stream = Serialize(document);
        using var response = await _container.CreateItemStreamAsync(stream, partition, cancellationToken: cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return sequence;
    }

    public async IAsyncEnumerable<CallEventEnvelope> ReadAsync(
        string callId,
        long afterSequence = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var query = new QueryDefinition(
                "SELECT c.callId, c.sequence, c.type, c.payload FROM c WHERE c.callId = @callId AND c.sequence > @after ORDER BY c.sequence")
            .WithParameter("@callId", callId)
            .WithParameter("@after", afterSequence);

        using var iterator = _container.GetItemQueryStreamIterator(query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(callId) });
        while (iterator.HasMoreResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var response = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var document = await JsonDocument.ParseAsync(response.Content, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("Documents", out var documents))
            {
                continue;
            }

            foreach (var item in documents.EnumerateArray())
            {
                var sequence = item.GetProperty("sequence").GetInt64();
                var type = item.GetProperty("type").GetString() ?? string.Empty;
                var payload = item.GetProperty("payload").GetString() ?? string.Empty;
                var strategyEvent = StrategyEventSerializer.Deserialize(type, payload);
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
    }

    private async Task<long> NextSequenceAsync(string callId, PartitionKey partition, CancellationToken cancellationToken)
    {
        var counterId = $"{callId}:seq";
        try
        {
            using var response = await _container.PatchItemStreamAsync(
                counterId,
                partition,
                [PatchOperation.Increment("/seq", 1)],
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return await CreateCounterAsync(callId, counterId, partition, cancellationToken).ConfigureAwait(false);
            }

            response.EnsureSuccessStatusCode();
            return ReadSeq(response.Content);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return await CreateCounterAsync(callId, counterId, partition, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<long> CreateCounterAsync(string callId, string counterId, PartitionKey partition, CancellationToken cancellationToken)
    {
        var counter = new CounterDocument
        {
            Id = counterId,
            CallId = callId,
            Seq = 1,
            Ttl = _options.Ttl is { } ttl ? (int)ttl.TotalSeconds : null,
        };

        try
        {
            using var stream = Serialize(counter);
            using var response = await _container.CreateItemStreamAsync(stream, partition, cancellationToken: cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return 1;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            // Lost the create race — another append created the counter; increment it.
            using var response = await _container.PatchItemStreamAsync(
                counterId,
                partition,
                [PatchOperation.Increment("/seq", 1)],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return ReadSeq(response.Content);
        }
    }

    private static long ReadSeq(Stream content)
    {
        using var document = JsonDocument.Parse(content);
        return document.RootElement.GetProperty("seq").GetInt64();
    }

    private static MemoryStream Serialize<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, jsonOptions);
        return new MemoryStream(bytes, writable: false);
    }

    private sealed record EventDocument
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("callId")]
        public required string CallId { get; init; }

        [JsonPropertyName("sequence")]
        public long Sequence { get; init; }

        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("payload")]
        public required string Payload { get; init; }

        [JsonPropertyName("recordedAt")]
        public DateTimeOffset RecordedAt { get; init; }

        [JsonPropertyName("ttl")]
        public int? Ttl { get; init; }
    }

    private sealed record CounterDocument
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("callId")]
        public required string CallId { get; init; }

        [JsonPropertyName("seq")]
        public long Seq { get; init; }

        [JsonPropertyName("ttl")]
        public int? Ttl { get; init; }
    }
}

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Exceptions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.State.Stores;

/// <summary>
/// Azure Cosmos DB-backed <see cref="ICallStateStore"/>. Each call's snapshot is a single document in
/// the snapshot container, partitioned by <c>/callId</c>, with the slices stored as a JSON map. Saves
/// are serializer-agnostic: the document is written via the Cosmos stream APIs with our own
/// <see cref="CallStateSnapshot.Version"/> as the concurrency token (paired with the document ETag), so
/// a stale writer is rejected with <see cref="CallStateConcurrencyException"/>.
/// </summary>
/// <remarks>
/// Relies on a <see cref="CosmosClient"/> registered in DI (typically Aspire's <c>AddAzureCosmosClient</c>).
/// The database and container are expected to exist (provisioned by infrastructure); the snapshot
/// container should have TTL enabled for <see cref="CallStateOptions.Ttl"/> to take effect.
/// </remarks>
public sealed class CosmosCallStateStore : ICallStateStore
{
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerOptions.Default)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Container _container;
    private readonly CallStateOptions _options;

    public CosmosCallStateStore(CosmosClient client, IOptions<CallStateOptions> options)
    {
        _options = options.Value;
        _container = client.GetContainer(_options.CosmosDatabaseName, _options.CosmosSnapshotContainerName);
    }

    public async ValueTask<CallStateSnapshot> SaveAsync(CallStateSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var partition = new PartitionKey(snapshot.CallId);
        var (existing, etag) = await ReadDocumentAsync(snapshot.CallId, partition, cancellationToken).ConfigureAwait(false);

        var currentVersion = existing?.Version ?? 0;
        if (snapshot.Version != currentVersion)
        {
            throw new CallStateConcurrencyException(snapshot.CallId, snapshot.Version, currentVersion);
        }

        var document = new CallStateDocument
        {
            Id = snapshot.CallId,
            CallId = snapshot.CallId,
            Version = currentVersion + 1,
            EventSequence = snapshot.EventSequence,
            UpdatedAt = DateTimeOffset.UtcNow,
            Slices = snapshot.Slices,
            Ttl = _options.Ttl is { } ttl ? (int)ttl.TotalSeconds : null,
        };

        using var stream = new MemoryStream();
        await JsonSerializer.SerializeAsync(stream, document, jsonOptions, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;

        try
        {
            ResponseMessage response;
            if (existing is null)
            {
                response = await _container.CreateItemStreamAsync(stream, partition, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var requestOptions = new ItemRequestOptions { IfMatchEtag = etag };
                response = await _container.ReplaceItemStreamAsync(stream, snapshot.CallId, partition, requestOptions, cancellationToken).ConfigureAwait(false);
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
                {
                    throw new CallStateConcurrencyException(snapshot.CallId, snapshot.Version, currentVersion);
                }

                response.EnsureSuccessStatusCode();
            }
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            throw new CallStateConcurrencyException(snapshot.CallId, snapshot.Version, currentVersion);
        }

        return snapshot with { Version = document.Version, UpdatedAt = document.UpdatedAt };
    }

    public async ValueTask<CallStateSnapshot?> LoadAsync(string callId, CancellationToken cancellationToken = default)
    {
        var (document, _) = await ReadDocumentAsync(callId, new PartitionKey(callId), cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        return new CallStateSnapshot
        {
            CallId = callId,
            Version = document.Version,
            EventSequence = document.EventSequence,
            Slices = document.Slices,
            UpdatedAt = document.UpdatedAt,
        };
    }

    public async ValueTask DeleteAsync(string callId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _container.DeleteItemStreamAsync(callId, new PartitionKey(callId), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone — idempotent delete.
        }
    }

    private async Task<(CallStateDocument? Document, string? ETag)> ReadDocumentAsync(string callId, PartitionKey partition, CancellationToken cancellationToken)
    {
        using var response = await _container.ReadItemStreamAsync(callId, partition, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound || response.Content is null)
        {
            return (null, null);
        }

        response.EnsureSuccessStatusCode();
        var document = await JsonSerializer.DeserializeAsync<CallStateDocument>(response.Content, jsonOptions, cancellationToken).ConfigureAwait(false);
        return (document, response.Headers.ETag);
    }

    private sealed record CallStateDocument
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("callId")]
        public required string CallId { get; init; }

        [JsonPropertyName("version")]
        public long Version { get; init; }

        [JsonPropertyName("eventSequence")]
        public long EventSequence { get; init; }

        [JsonPropertyName("updatedAt")]
        public DateTimeOffset UpdatedAt { get; init; }

        [JsonPropertyName("slices")]
        public required IReadOnlyDictionary<string, string> Slices { get; init; }

        [JsonPropertyName("ttl")]
        public int? Ttl { get; init; }
    }
}

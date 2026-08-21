using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agents.AI.Extensions.Helpers.Streaming;

public static class RawMediaStreamExtensions
{
    /// <summary>
    /// Wrap consumer chunks into DataContent objects for agent ingestion.
    /// </summary>
    public static async IAsyncEnumerable<DataContent> ToDataContentAsync(
        this RawMediaPipeSubscription consumer,
        string mediaType = "audio/pcm",
        int? maxChunkBytes = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in consumer.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (maxChunkBytes is not null && chunk.Length > maxChunkBytes.Value)
            {
                // Split large chunk
                var remaining = chunk;
                while (remaining.Length > 0)
                {
                    var sliceSize = Math.Min(maxChunkBytes.Value, remaining.Length);
                    var slice = remaining[..sliceSize];
                    remaining = remaining[sliceSize..];
                    yield return new DataContent(slice, mediaType);
                }
            }
            else
            {
                yield return new DataContent(chunk, mediaType);
            }
        }
    }

    public static ValueTask WriteAsync(this RawMediaStreamChannel distributor, ChatResponseUpdate update, CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(MessageUpdateExtensions.FromChatResponseUpdate(update), AgentsAIJsonUtilities.DefaultOptions);
        return distributor.WriteAsync(bytes, cancellationToken);
    }

    public static ValueTask WriteAsync(this RawMediaStreamChannel distributor, AgentResponseUpdate update, CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(MessageUpdateExtensions.FromAgentResponseUpdate(update), AgentsAIJsonUtilities.DefaultOptions);
        return distributor.WriteAsync(bytes, cancellationToken);
    }

    public static ValueTask WriteAsync(this RawMediaStreamChannel distributor, MessageUpdate message, CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, AgentsAIJsonUtilities.DefaultOptions);
        return distributor.WriteAsync(bytes, cancellationToken);
    }

    public static async ValueTask WriteAsync(this RawMediaStreamChannel distributor, IAsyncEnumerable<MessageUpdate> messageStream, CancellationToken cancellationToken = default)
    {
        await foreach (var message in messageStream.WithCancellation(cancellationToken))
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message, AgentsAIJsonUtilities.DefaultOptions);
            await distributor.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
    }

    public static async IAsyncEnumerable<MessageUpdate> ReadAsMessageUpdatesAsync(this RawMediaPipeSubscription consumer, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in consumer.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (chunk.Length == 0) continue;
            MessageUpdate? result = null;

            try
            {
                result = JsonSerializer.Deserialize<MessageUpdate>(chunk.Span, AgentsAIJsonUtilities.DefaultOptions);

            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"Failed to deserialize chunk to MessageUpdate: {ex}");
            }

            if (result is not null)
            {
                Debug.WriteLine(JsonSerializer.Serialize(result, AgentsAIJsonUtilities.DefaultOptions));
                yield return result;
            }
        }
    }
}

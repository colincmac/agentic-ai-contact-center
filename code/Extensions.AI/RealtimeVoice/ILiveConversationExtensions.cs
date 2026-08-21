using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenAI.Realtime;

namespace Extensions.AI.RealtimeVoice;

public static class ILiveConversationExtensions
{
    public static T? GetService<T>(this ILiveConversationClient client, object? serviceKey = null) where T : class
        => client.GetService(typeof(T), serviceKey) as T;

    public static T? GetService<T>(this ILiveConversationSession session, object? serviceKey = null) where T : class
    => session.GetService(typeof(T), serviceKey) as T;

    public static LiveConversationClientBuilder AsBuilder(this ILiveConversationClient innerClient)
        => new(innerClient);

    public static async IAsyncEnumerable<T> GetUpdatesAsync<T>(this ILiveConversationSession session, LiveConversationResponseOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    where T : RealtimeServerUpdate
    {
        await foreach (ChatResponseUpdate serverEvent in session.GetStreamingResponseAsync(options, cancellationToken).ConfigureAwait(false))
        {
            if (serverEvent.RawRepresentation is T typedEvent)
            {
                yield return typedEvent;
            }
        }
    }

    public static async Task<T> WaitForUpdateAsync<T>(this ILiveConversationSession session, LiveConversationResponseOptions? options = null, CancellationToken cancellationToken = default)
    where T : RealtimeServerUpdate
    {
        await foreach (T serverEvent in session.GetUpdatesAsync<T>(options, cancellationToken).ConfigureAwait(false))
        {
            return serverEvent;
        }

        throw new OperationCanceledException("No server event received before cancellation.", cancellationToken);
    }
}

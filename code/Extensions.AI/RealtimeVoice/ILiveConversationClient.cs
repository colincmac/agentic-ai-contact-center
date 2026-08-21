using Extensions.AI.RealtimeVoice.Configuration;
using Microsoft.Extensions.AI;

namespace Extensions.AI.RealtimeVoice;

/// <summary>Represents a client for real-time bidirectional audio communication.</summary>
public interface ILiveConversationClient
{
    /// <summary>Creates a new real-time conversation session.</summary>
    /// <param name="options">Configuration options for the session.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A real-time conversation session.</returns>
    Task<ILiveConversationSession> GetSessionAsync(
        LiveConversationSessionOptions? sessionOptions = null,
        CancellationToken cancellationToken = default);

    ILiveConversationSession GetSession(
    LiveConversationSessionOptions? sessionOptions = null);

    /// <summary>Gets metadata about the realtime conversation client.</summary>
    LiveConversationClientMetadata Metadata { get; }

    /// <summary>Asks the client for an object of the specified type.</summary>
    object? GetService(Type serviceType, object? serviceKey = null);
}

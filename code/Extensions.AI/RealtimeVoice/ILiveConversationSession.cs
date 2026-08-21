using Microsoft.Extensions.AI;

namespace Extensions.AI.RealtimeVoice;

/// <summary>Represents an active real-time conversation session.</summary>
public interface ILiveConversationSession : IDisposable
{
    /// <summary>Gets the unique identifier for this session.</summary>
    string? SessionId { get; }

    /// <summary>Gets the current state of the session.</summary>
    RealtimeSessionState State { get; }

    public IList<AITool> SessionTools { get; }

    public LiveConversationSessionOptions? CurrentSessionConfiguration { get; }

    /// <summary>Occurs when the session state changes.</summary>
    event EventHandler<RealtimeSessionStateChangedEventArgs>? StateChanged;

    /// <summary>Sends audio input to the conversation.</summary>
    /// <param name="audioData">The audio data to send.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task SendAudioAsync(
        ReadOnlyMemory<byte> audioData,
        CancellationToken cancellationToken = default);

    /// <summary>Sends audio input stream to the conversation.</summary>
    /// <param name="audioStream">The audio stream to send.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task SendAudioStreamAsync(
        Stream audioStream,
        CancellationToken cancellationToken = default);

    Task SendMessagesAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken = default);

    /// <summary>Receives updates from the conversation.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An async enumerable of conversation updates.</returns>
    IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        LiveConversationResponseOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Interrupts the current response generation.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task InterruptAsync(CancellationToken cancellationToken = default);

    /// <summary>Commits the current input buffer and triggers response generation.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task CommitPendingAudioAsync(CancellationToken cancellationToken = default);

    /// <summary>Clears the current input buffer.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task ClearInputAudioAsync(CancellationToken cancellationToken = default);

    Task StartResponseAsync(LiveConversationResponseOptions? responseOptions, CancellationToken cancellationToken = default);

    /// <summary>Updates the session configuration.</summary>
    /// <param name="options">The configuration options to update.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task ConfigureSessionAsync(
        LiveConversationSessionOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Gets service of the specified type from the session.</summary>
    object? GetService(Type serviceType, object? serviceKey = null);
}

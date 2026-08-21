using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.AI;
using Microsoft.Shared.Diagnostics;

namespace Extensions.AI.RealtimeVoice
{
    /// <summary>
    /// A delegating (decorator) implementation of <see cref="ILiveConversationSession"/> that forwards
    /// all operations to an inner <see cref="ILiveConversationSession"/>. Override individual virtual
    /// members to inject cross-cutting concerns (logging, telemetry, filters, etc.).
    /// </summary>
    public class DelegatingConversationSession : ILiveConversationSession
    {
        protected ILiveConversationSession InnerSession { get; }

        public DelegatingConversationSession(ILiveConversationSession innerSession)
        {
            InnerSession = Throw.IfNull(innerSession);
        }

        /// <inheritdoc />
        public virtual string? SessionId => InnerSession.SessionId;

        /// <inheritdoc />
        public virtual RealtimeSessionState State => InnerSession.State;

        public virtual IList<AITool> SessionTools => InnerSession.SessionTools;

        public LiveConversationSessionOptions? CurrentSessionConfiguration => InnerSession.CurrentSessionConfiguration;

        /// <inheritdoc />
        public virtual event EventHandler<RealtimeSessionStateChangedEventArgs>? StateChanged
        {
            add => InnerSession.StateChanged += value;
            remove => InnerSession.StateChanged -= value;
        }

        /// <inheritdoc />
        public virtual Task SendAudioAsync(
            ReadOnlyMemory<byte> audioData,
            CancellationToken cancellationToken = default)
            => InnerSession.SendAudioAsync(audioData, cancellationToken);

        /// <inheritdoc />
        public virtual Task SendAudioStreamAsync(
            Stream audioStream,
            CancellationToken cancellationToken = default)
            => InnerSession.SendAudioStreamAsync(audioStream, cancellationToken);

        /// <inheritdoc />
        public virtual Task SendMessagesAsync(
            IEnumerable<ChatMessage> messages,
            CancellationToken cancellationToken = default)
            => InnerSession.SendMessagesAsync(messages, cancellationToken);

        /// <inheritdoc />
        public virtual IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            LiveConversationResponseOptions? options = null,
            CancellationToken cancellationToken = default)
            => InnerSession.GetStreamingResponseAsync(options, cancellationToken);

        /// <inheritdoc />
        public virtual Task InterruptAsync(CancellationToken cancellationToken = default)
            => InnerSession.InterruptAsync(cancellationToken);

        /// <inheritdoc />
        public virtual Task CommitPendingAudioAsync(CancellationToken cancellationToken = default)
            => InnerSession.CommitPendingAudioAsync(cancellationToken);

        /// <inheritdoc />
        public virtual Task ClearInputAudioAsync(CancellationToken cancellationToken = default)
            => InnerSession.ClearInputAudioAsync(cancellationToken);

        /// <inheritdoc />
        public virtual Task StartResponseAsync(
            LiveConversationResponseOptions? responseOptions,
            CancellationToken cancellationToken = default)
            => InnerSession.StartResponseAsync(responseOptions, cancellationToken);

        /// <inheritdoc />
        public virtual Task ConfigureSessionAsync(
            LiveConversationSessionOptions options,
            CancellationToken cancellationToken = default)
            => InnerSession.ConfigureSessionAsync(options, cancellationToken);

        /// <inheritdoc />
        public virtual object? GetService(Type serviceType, object? serviceKey = null)
            => InnerSession.GetService(serviceType, serviceKey);

        /// <summary>
        /// Disposes the inner session. Override if you need to alter disposal behavior.
        /// </summary>
        public virtual void Dispose()
        {
            InnerSession.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}

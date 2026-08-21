using Extensions.AI.RealtimeVoice.Configuration;
using Microsoft.Shared.Diagnostics;

namespace Extensions.AI.RealtimeVoice;

public class DelegatingConversationClient : ILiveConversationClient
{
    protected DelegatingConversationClient(ILiveConversationClient innerClient)
    {
        InnerClient = Throw.IfNull(innerClient);
    }

    protected ILiveConversationClient InnerClient { get; }

    public LiveConversationClientMetadata Metadata => InnerClient.Metadata;

    public virtual object? GetService(Type serviceType, object? serviceKey = null)
    {
        _ = Throw.IfNull(serviceType);

        // If the key is non-null, we don't know what it means so pass through to the inner service.
        return
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this :
            InnerClient.GetService(serviceType, serviceKey);
    }

    public virtual Task<ILiveConversationSession> GetSessionAsync(LiveConversationSessionOptions? sessionOptions, CancellationToken cancellationToken = default) =>
        InnerClient.GetSessionAsync(sessionOptions, cancellationToken);

    public virtual ILiveConversationSession GetSession(LiveConversationSessionOptions? sessionOptions) =>
        InnerClient.GetSession(sessionOptions);
}

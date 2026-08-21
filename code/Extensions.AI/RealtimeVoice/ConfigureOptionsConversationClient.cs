using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Extensions.AI.RealtimeVoice;


public class ConfigurableSessionConversationClient: DelegatingConversationClient
{
    private readonly Func<ILiveConversationSession, ILiveConversationSession> _sessionBuilder;
    public ConfigurableSessionConversationClient(ILiveConversationClient innerClient, Func<ILiveConversationSession, ILiveConversationSession>? sessionBuilder = null) : base(innerClient)
    {
        _sessionBuilder = sessionBuilder ?? ((session) => session);
    }


    public override async Task<ILiveConversationSession> GetSessionAsync(LiveConversationSessionOptions? sessionOptions, CancellationToken cancellationToken = default)
    {
        var session = await InnerClient.GetSessionAsync(sessionOptions, cancellationToken);
        return _sessionBuilder(session);
    }

    public override ILiveConversationSession GetSession(LiveConversationSessionOptions? sessionOptions)
    {
        var session = InnerClient.GetSession(sessionOptions);
        return _sessionBuilder(session);
    }
}

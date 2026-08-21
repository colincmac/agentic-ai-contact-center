using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace Extensions.AI.RealtimeVoice;


public class ConfigureOptionsConversationSession : DelegatingConversationSession
{
    private readonly Action<LiveConversationSessionOptions> _configureSessionOptions;
    private readonly Action<LiveConversationResponseOptions?> _configureResponseOptions;

    public ConfigureOptionsConversationSession(ILiveConversationSession innerSession, Action<LiveConversationSessionOptions>? configureSessionOptions = null, Action<LiveConversationResponseOptions?>? configureResponseOptions = null) : base(innerSession)
    {
        _configureSessionOptions = configureSessionOptions ?? (_ => { });
        _configureResponseOptions = configureResponseOptions ?? (_ => { });
    }


    /// <inheritdoc />
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        LiveConversationResponseOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _configureResponseOptions(options);
        return InnerSession.GetStreamingResponseAsync(options, cancellationToken);
    }

    /// <inheritdoc />
    public override Task StartResponseAsync(
        LiveConversationResponseOptions? responseOptions,
        CancellationToken cancellationToken = default)
    {
        _configureResponseOptions(responseOptions);
        return InnerSession.StartResponseAsync(responseOptions, cancellationToken);
    }

    /// <inheritdoc />
    public override Task ConfigureSessionAsync(
        LiveConversationSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        _configureSessionOptions(options);
        return InnerSession.ConfigureSessionAsync(options, cancellationToken);
    }

}

using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.AI;

namespace Extensions.AI.Realtime;

public sealed class ConfigureOptionsRealtimeClientSession : IRealtimeClientSession
{
    private readonly IRealtimeClientSession _innerSession;
    private readonly Func<RealtimeSessionOptions, RealtimeSessionOptions> _sessionOptionsFactory;

    public ConfigureOptionsRealtimeClientSession(IRealtimeClientSession innerSession, Func<RealtimeSessionOptions, RealtimeSessionOptions> sessionOptionsFactory)
    {
        this._innerSession = innerSession;
        this._sessionOptionsFactory = sessionOptionsFactory;
    }

    public RealtimeSessionOptions? Options => _innerSession.Options;

    public IAsyncEnumerable<RealtimeServerMessage> GetStreamingResponseAsync(CancellationToken cancellationToken = default)
    {
        return _innerSession.GetStreamingResponseAsync(cancellationToken);
    }

    public Task SendAsync(RealtimeClientMessage message, CancellationToken cancellationToken = default)
    {
        if (message is SessionUpdateRealtimeClientMessage sessionUpdateRealtimeClientMessage)
        {
            sessionUpdateRealtimeClientMessage.Options = _sessionOptionsFactory(sessionUpdateRealtimeClientMessage.Options);
        }
        return _innerSession.SendAsync(message, cancellationToken);
    }
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return _innerSession.GetService(serviceType, serviceKey);
    }

    public ValueTask DisposeAsync()
    {
        return _innerSession.DisposeAsync();
    }



}

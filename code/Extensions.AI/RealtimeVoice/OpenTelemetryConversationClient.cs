using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Diagnostics.Metrics;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Showcase.AgentFramework.LiveVoice.Client;
using Extensions.AI.RealtimeVoice.Configuration;
using Extensions.AI.RealtimeVoice;

namespace Extensions.AI.RealtimeVoice;

public sealed class OpenTelemetryConversationClient : DelegatingConversationClient
{
    private readonly string? _activitySourceName;
    private readonly ILogger? _logger;

    public OpenTelemetryConversationClient(ILiveConversationClient innerClient, ILogger? logger = null, string? sourceName = null) : base(innerClient)
    {
        _activitySourceName = sourceName;
        _logger = logger ?? NullLogger.Instance;
    }

    public override async Task<ILiveConversationSession> GetSessionAsync(LiveConversationSessionOptions? sessionOptions, CancellationToken cancellationToken = default)
    {
        var innerSession = await base.GetSessionAsync(sessionOptions, cancellationToken);
        return new OpenTelemetryConversationSession(innerSession, _activitySourceName, _logger);
    }
    public override ILiveConversationSession GetSession(LiveConversationSessionOptions? sessionOptions)
    {
        var innerSession = base.GetSession(sessionOptions);
        return new OpenTelemetryConversationSession(innerSession, _activitySourceName, _logger);
    }
}

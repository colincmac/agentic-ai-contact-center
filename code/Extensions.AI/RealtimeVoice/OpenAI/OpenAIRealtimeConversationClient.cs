//using System.Reflection;
//using Extensions.AI.RealtimeVoice.Configuration;
//using Microsoft.Extensions.AI;
//using Microsoft.Extensions.Logging;
//using Microsoft.Extensions.Logging.Abstractions;
//using Microsoft.Shared.Diagnostics;
//using OpenAI;
//using OpenAI.Realtime;
//using Showcase.AgentFramework.LiveVoice.Client;

//namespace Extensions.AI.RealtimeVoice.OpenAI;

//public partial class OpenAIRealtimeConversationClient : ILiveConversationClient
//{
//    private readonly RealtimeClient _realtimeClient;
//    private readonly ILoggerFactory _loggerFactory;
//    private readonly ILogger _logger;
//    private readonly string _realtimeModelId;
//    /// <summary>Initializes a new instance of the <see cref="Microsoft.Extensions.AI.OpenAIRealtimeConversationClient"/> class.</summary>
//    /// <param name="client">The underlying OpenAI client.</param>
//    /// <param name="realtimeModelId">The model ID to use.</param>
//    /// <param name="loggerFactory">The logger instance.</param>
//    public OpenAIRealtimeConversationClient(
//        OpenAIClient client,
//        string realtimeModelId,
//        ILoggerFactory? loggerFactory = null) : this(client.GetRealtimeClient(), realtimeModelId, loggerFactory)
//    {
//    }

//    /// <summary>Initializes a new instance of the <see cref="Microsoft.Extensions.AI.OpenAIRealtimeConversationClient"/> class.</summary>
//    /// <param name="realtimeClient">The underlying OpenAI realtime client.</param>
//    /// <param name="realtimeModelId">The model ID to use.</param>
//    /// <param name="loggerFactory">The logger instance.</param>
//    public OpenAIRealtimeConversationClient(
//        RealtimeClient realtimeClient,
//        string realtimeModelId,
//        ILoggerFactory? loggerFactory = null): base() 
//    {
//        _realtimeClient = Throw.IfNull(realtimeClient);
//        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
//        _logger = _loggerFactory.CreateLogger<OpenAIRealtimeConversationClient>();
//        _realtimeModelId = realtimeModelId;
        
//        Metadata = new(_realtimeModelId, "openai", realtimeClient.Endpoint);
//    }

//    /// <inheritdoc/>
//    public LiveConversationClientMetadata Metadata { get; }

//    /// <inheritdoc/>
//    public async Task<ILiveConversationSession> GetSessionAsync(
//        LiveConversationSessionOptions? sessionOptions,
//        CancellationToken cancellationToken = default)
//    {
//        try
//        {
//            _logger.LogStartingRealtimeConversationSession(_realtimeModelId, Metadata.ProviderName);

//            var logger = _loggerFactory.CreateLogger<OpenAIRealtimeConversationSession>();

//            // Start the session
//            var session = await _realtimeClient.StartConversationSessionAsync(_realtimeModelId, cancellationToken: cancellationToken)
//                .ConfigureAwait(false);

//            var wrapper = new OpenAIRealtimeConversationSession(
//                this,
//                session,
//                _realtimeModelId,
//                sessionOptions,
//                logger);

//            if (sessionOptions is not null)
//            {
//                await wrapper.ConfigureSessionAsync(sessionOptions, cancellationToken).ConfigureAwait(false);
//            }
//            _logger.LogRealtimeConversationSessionStarted(_realtimeModelId, wrapper.SessionId, Metadata.ProviderName);

//            return wrapper;
//        }
//        catch (Exception ex)
//        {
//            _logger.LogRealtimeConversationSessionFailedToStart(_realtimeModelId, Metadata.ProviderName, ex);
//            throw;
//        }
//    }


//    public ILiveConversationSession GetSession(
//        LiveConversationSessionOptions? sessionOptions)
//    {
//        var logger = _loggerFactory.CreateLogger<OpenAIRealtimeConversationSession>();

//        return new OpenAIRealtimeConversationSession(this, _realtimeModelId, sessionOptions, logger);
//    }

//    /// <inheritdoc/>
//    public object? GetService(Type serviceType, object? serviceKey = null)
//    {
//        Throw.IfNull(serviceType);

//        return
//            serviceKey is not null ? null :
//            serviceType == typeof(ILiveConversationClient) ? this :
//            serviceType.IsInstanceOfType(this) ? this :
//            serviceType == typeof(RealtimeClient) ? _realtimeClient :
//            serviceType == typeof(LiveConversationClientMetadata) ? Metadata : null;
//    }
//}

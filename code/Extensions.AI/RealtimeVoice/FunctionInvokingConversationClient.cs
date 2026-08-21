//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Text;
//using System.Threading.Tasks;
//using Extensions.AI.RealtimeVoice;
//using Microsoft.Extensions.AI;
//using Microsoft.Extensions.Logging;
//using Microsoft.Extensions.Logging.Abstractions;
//using Showcase.AgentFramework.LiveVoice.Client;

//namespace Extensions.AI.RealtimeVoice;

//public class FunctionInvokingConversationClient : DelegatingConversationClient
//{

//    private readonly ILoggerFactory? _loggerFactory;
//    private readonly IServiceProvider? _functionInvocationServices;

//    public Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>? FunctionInvoker { get; set; }

//    public FunctionInvokingConversationClient(ILiveConversationClient innerClient, ILoggerFactory? loggerFactory = null, IServiceProvider? functionInvocationServices = null) : base(innerClient)
//    {
//        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
//        _functionInvocationServices = functionInvocationServices;
//    }

//    public override async Task<ILiveConversationSession> GetSessionAsync(LiveConversationSessionOptions? sessionOptions, CancellationToken cancellationToken = default)
//    {
//        var innerSession = await base.GetSessionAsync(sessionOptions, cancellationToken);
//        return new FunctionInvokingConversationSession(innerSession, _loggerFactory, _functionInvocationServices);
//    }

//    public override ILiveConversationSession GetSession(LiveConversationSessionOptions? sessionOptions)
//    {
//        var innerSession = base.GetSession(sessionOptions);
//        return new FunctionInvokingConversationSession(innerSession, _loggerFactory, _functionInvocationServices);
//    }
//}

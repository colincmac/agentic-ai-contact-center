//using System.Collections.Concurrent;
//using System.Diagnostics;
//using System.Runtime.CompilerServices;
//using System.Text.Json;
//using System.Threading.Channels;
//using Extensions.AI.OpenTelemetry.SemanticConventions;
//using Extensions.AI.RealtimeVoice;
//using Microsoft.Extensions.AI;
//using Microsoft.Extensions.Logging;
//using Microsoft.Extensions.Logging.Abstractions;
//using Microsoft.Shared.Diagnostics;
//using OpenTelemetryConstants = Extensions.AI.OpenTelemetry.SemanticConventions;

//namespace Extensions.AI.RealtimeVoice;


///// <summary>
///// A conversation session that can invoke functions requested by the underlying <see cref="ILiveConversationSession"/>.
///// Unlike <see cref="FunctionInvokingChatClient"/>, this class is designed to work in real-time. Function invocations, through FunctionCallContent,
///// are processed as soon as they are detected in the streaming response from the inner session. 
///// </summary>
//public sealed partial class FunctionInvokingConversationSession : DelegatingConversationSession, IAsyncDisposable
//{
//    private readonly Dictionary<string, AITool> _defaultToolMap = [];
//    private readonly ConcurrentDictionary<string, FunctionInvocation> _pendingFunctions = [];
//    private readonly Channel<FunctionInvocation> _functionInvocationChannel = Channel.CreateUnbounded<FunctionInvocation>(new UnboundedChannelOptions
//    {
//        SingleReader = true,
//        SingleWriter = false,
//        AllowSynchronousContinuations = true
//    });
//    private readonly ConcurrentBag<Task> _activeFunctionTasks = [];
//    private readonly CancellationTokenSource _shutdownCts = new();

//    private Task? _functionProcessorTask;
//    /// <summary>The logger to use for logging information about function invocation.</summary>
//    private readonly ILogger _logger;

//    /// <summary>The <see cref="ActivitySource"/> to use for telemetry.</summary>
//    /// <remarks>This component does not own the instance and should not dispose it.</remarks>
//    private readonly ActivitySource? _activitySource;

//    /// <summary>Gets the <see cref="IServiceProvider"/> specified when constructing the <see cref="FunctionInvokingChatClient"/>, if any.</summary>
//    private IServiceProvider? FunctionInvocationServices { get; }

//    public FunctionInvokingConversationSession(ILiveConversationSession innerSession, ILoggerFactory? loggerFactory = null, IServiceProvider? functionInvocationServices = null) : base(innerSession)
//    {
//        _logger = (ILogger?)loggerFactory?.CreateLogger<FunctionInvokingConversationSession>() ?? NullLogger.Instance;
//        _activitySource = innerSession.GetService<ActivitySource>();
//        FunctionInvocationServices = functionInvocationServices;
//        // Build tool map
//        _defaultToolMap = BuildToolMap([.. AdditionalTools ?? [], .. innerSession.SessionTools]);
//    }


//    private static Dictionary<string, AITool> BuildToolMap(IEnumerable<AITool> tools)
//    {
//        return tools.ToDictionary(t => t.Name);
//    }
//    private void UpdateTools(IEnumerable<AITool>? tools)
//    {
//        if (tools == null) return;

//        foreach (var tool in tools)
//        {
//            _defaultToolMap[tool.Name] = tool;
//        }
//    }


//    public bool IncludeDetailedErrors { get; set; }


//    public bool AllowConcurrentInvocation { get; set; }


//    public IList<AITool>? AdditionalTools { get; set; }

//    public override async Task StartResponseAsync(LiveConversationResponseOptions? responseOptions, CancellationToken cancellationToken = default)
//    {
//        UpdateTools(responseOptions?.Tools);
//        await base.StartResponseAsync(responseOptions, cancellationToken);
//    }

//    public override Task ConfigureSessionAsync(LiveConversationSessionOptions options, CancellationToken cancellationToken = default)
//    {
//        UpdateTools(options.Tools);
//        return base.ConfigureSessionAsync(options, cancellationToken);
//    }
//    public override async Task SendMessagesAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
//    {
//        var additionalMessages = new List<ChatMessage>();
//        foreach (var message in messages)
//        {
//            foreach (var content in message.Contents)
//            {

//                if (content is FunctionApprovalResponseContent approvalResponse)
//                {
//                    var pending = _pendingFunctions.AddOrUpdate(
//                        approvalResponse.FunctionCall.CallId,
//                        _ => new FunctionInvocation
//                        {
//                            CallContent = approvalResponse.FunctionCall,
//                            ApprovalResponse = approvalResponse,
//                            Tool = _defaultToolMap.TryGetValue(approvalResponse.FunctionCall.Name, out var tool) ? tool : null
//                        },
//                        (_, existing) =>
//                        {
//                            existing.ApprovalResponse = approvalResponse;
//                            return existing;
//                        });

//                    var result = await ProcessFunctionCallAsync(pending, true, cancellationToken);
//                    pending.ResultContent = CreateFunctionResultContent(result, IncludeDetailedErrors);
//                    additionalMessages.Add(new ChatMessage(
//                        ChatRole.Tool,
//                        [pending.ResultContent]));
//                }
//            }
//        }
//        await base.SendMessagesAsync([.. messages, .. additionalMessages], cancellationToken);
//    }

//    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
//        LiveConversationResponseOptions? options = null,
//        [EnumeratorCancellation] CancellationToken cancellationToken = default)
//    {
//        _functionProcessorTask ??= Task.Run(() => ProcessBackgroundFunctionInvocationsAsync(cancellationToken), cancellationToken);
//        await foreach (var update in base.GetStreamingResponseAsync(options, cancellationToken))
//        {
//            var results = new List<AIContent>();
//            var hasFunctionCallContent = false;
//            await foreach (var content in ProcessChatResponseUpdateContentsAsync(update.Contents, options, cancellationToken))
//            {
//                results.Add(content);
//                hasFunctionCallContent = content is FunctionCallContent || hasFunctionCallContent;

//                if (content is FunctionResultContent frc)
//                {
//                    await StartResponseAsync(null, cancellationToken);
//                }
//            }
//            yield return ReplaceUpdateContents(update, results);

//            if (hasFunctionCallContent)
//            {
//                await StartResponseAsync(options, cancellationToken);
//            }
//        }
//    }


//    private async Task ProcessBackgroundFunctionInvocationsAsync(CancellationToken cancellationToken)
//    {
//        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
//        var linkedToken = linkedCts.Token;
//        var reader = _functionInvocationChannel.Reader;

//        try
//        {
//            await foreach (var functionInvocation in reader.ReadAllAsync(linkedToken).ConfigureAwait(false))
//            {
//                var task = ProcessSingleFunctionAsync(functionInvocation, linkedToken);
//                _activeFunctionTasks.Add(task);
//            }
//        }
//        catch (OperationCanceledException)
//        {
//            // Expected when cancellation is requested
//        }
//        catch (Exception ex)
//        {
//            _logger.LogError(ex, "Function processing failed for session {SessionId}", InnerSession.SessionId);
//        }
//    }

//    private async Task ProcessSingleFunctionAsync(FunctionInvocation functionInvocation, CancellationToken cancellationToken)
//    {
//        try
//        {
//            var result = await ProcessFunctionCallAsync(functionInvocation, captureExceptions: true, cancellationToken);
//            functionInvocation.ResultContent = CreateFunctionResultContent(result, IncludeDetailedErrors);
//            await SendMessagesAsync([new(ChatRole.Tool, [functionInvocation.ResultContent])], cancellationToken);
//        }
//        catch (OperationCanceledException)
//        {
//            // Expected during shutdown
//        }
//        catch (Exception ex)
//        {
//            _logger.LogError(ex, "Function {FunctionName} failed for session {SessionId}",
//                functionInvocation.CallContent.Name, InnerSession.SessionId);
//        }
//    }
//    /// <summary>
//    /// 
//    /// </summary>
//    /// <param name="contents"></param>
//    /// <param name="options"></param>
//    /// <param name="cancellationToken"></param>
//    /// <returns></returns>
//    private async IAsyncEnumerable<AIContent> ProcessChatResponseUpdateContentsAsync(
//        IList<AIContent> contents,
//        LiveConversationResponseOptions? options,
//        [EnumeratorCancellation] CancellationToken cancellationToken)
//    {
//        foreach (var content in contents)
//        {
//            switch (content)
//            {
//                case FunctionCallContent fcc:
//                    {
//                        _defaultToolMap.TryGetValue(fcc.Name, out var tool);
//                        var pending = _pendingFunctions.GetOrAdd(fcc.CallId, _ => new FunctionInvocation
//                        {
//                            CallContent = fcc,
//                            Tool = tool
//                        });

//                        if (pending.ResultContent is not null)
//                        {
//                            // Already processed
//                            yield return pending.ResultContent;
//                            break;
//                        }

//                        if (pending.RequiresApproval)
//                        {
//                            var approvalRequest = new FunctionApprovalRequestContent(fcc.CallId, fcc)
//                            {
//                                AdditionalProperties = new AdditionalPropertiesDictionary([new ("requestedAt", DateTime.UtcNow)])
//                            };

//                            pending.ApprovalRequest = approvalRequest;
//                            yield return approvalRequest;
//                            break;
//                        }

//                        if(InnerSession.CurrentSessionConfiguration?.EnableAsyncToolCalls == true)
//                        {
//                            await _functionInvocationChannel.Writer.WriteAsync(pending, cancellationToken);
//                        }
//                        else
//                        {
//                            var result = await ProcessFunctionCallAsync(pending, true, cancellationToken);
//                            pending.ResultContent = CreateFunctionResultContent(result, IncludeDetailedErrors);
//                            await SendMessagesAsync([new(ChatRole.Tool, [pending.ResultContent])], cancellationToken);
//                            yield return pending.ResultContent;
//                        }
//                        break;
//                    }

//                case FunctionApprovalResponseContent farc:
//                    {

//                        _defaultToolMap.TryGetValue(farc.FunctionCall.Name, out var tool);
//                        farc.AdditionalProperties ??= [];
//                        farc.AdditionalProperties["respondedAt"] = DateTime.UtcNow;

//                        var pending = _pendingFunctions.AddOrUpdate(
//                            farc.FunctionCall.CallId,
//                            _ => new FunctionInvocation
//                            {
//                                CallContent = farc.FunctionCall,
//                                ApprovalResponse = farc,
//                                Tool = tool,
//                            }, (_, existing) =>
//                            {
//                                existing.ApprovalResponse = farc;
//                                return existing;
//                            });

//                        if(pending.ResultContent is not null)
//                        {
//                            // Already processed
//                            yield return pending.ResultContent;
//                            break;
//                        }

//                        if (InnerSession.CurrentSessionConfiguration?.EnableAsyncToolCalls == true)
//                        {
//                            await _functionInvocationChannel.Writer.WriteAsync(pending, cancellationToken);
//                        }
//                        else
//                        {
//                            var result = await ProcessFunctionCallAsync(pending, true, cancellationToken);
//                            pending.ResultContent = CreateFunctionResultContent(result, IncludeDetailedErrors);
//                            await SendMessagesAsync([new(ChatRole.Tool, [pending.ResultContent])], cancellationToken);
//                            yield return pending.ResultContent;
//                        }
//                        break;
//                    }

//                default:
//                    yield return content;
//                    break;
//            }
//        }
//    }

//    private static ChatResponseUpdate ReplaceUpdateContents(ChatResponseUpdate original, IList<AIContent> contents) =>
//        new()
//        {
//            Contents = contents,
//            ConversationId = original.ConversationId,
//            CreatedAt = original.CreatedAt,
//            MessageId = original.MessageId,
//            ResponseId = original.ResponseId,
//            Role = original.Role,
//            AuthorName = original.AuthorName,
//            AdditionalProperties = original.AdditionalProperties,
//            RawRepresentation = original.RawRepresentation,
//            FinishReason = original.FinishReason,
//            ModelId = original.ModelId
//        };

//    private async Task<FunctionInvocationResult> ProcessFunctionCallAsync(
//        FunctionInvocation pendingCall,
//        bool captureExceptions = true,
//        CancellationToken cancellationToken = default)
//    {
//        if (pendingCall.ApprovalResponse is FunctionApprovalResponseContent farc && !farc.Approved)
//        {
//            return new(terminate: false, FunctionInvocationStatus.Rejected, pendingCall.CallContent, result: null, exception: null);
//        }

//        if (pendingCall.Tool is not AIFunction aiFunction)
//        {
//            return new(terminate: false, FunctionInvocationStatus.NotFound, pendingCall.CallContent, result: null, exception: null);
//        }

//        object? result;
//        try
//        {
//            result = await InstrumentedInvokeFunctionAsync(pendingCall, cancellationToken);
//        }
//        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
//        {
//            if (!captureExceptions)
//            {
//                throw;
//            }

//            return new(
//                terminate: false,
//                FunctionInvocationStatus.Exception,
//                pendingCall.CallContent,
//                result: null,
//                exception: e);
//        }

//        return new(
//            terminate: false,
//            FunctionInvocationStatus.RanToCompletion,
//            pendingCall.CallContent,
//            result,
//            exception: null);
//    }

//    private static FunctionResultContent CreateFunctionResultContent(FunctionInvocationResult result, bool includeDetailedErrors)
//    {
//        _ = Throw.IfNull(result);

//        object? functionResult;
//        if (result.Status == FunctionInvocationStatus.RanToCompletion)
//        {
//            functionResult = result.Result ?? "Success: Function completed.";
//        }
//        else
//        {
//            string message = result.Status switch
//            {
//                FunctionInvocationStatus.NotFound => $"Error: Requested function \"{result.CallContent.Name}\" not found.",
//                FunctionInvocationStatus.Exception => "Error: Function failed.",
//                FunctionInvocationStatus.Rejected => "Error: Tool call invocation was rejected by user.",
//                _ => "Error: Unknown error.",
//            };

//            if (includeDetailedErrors && result.Exception is not null)
//            {
//                message = $"{message} Exception: {result.Exception.Message}";
//            }

//            functionResult = message;
//        }

//        return new FunctionResultContent(result.CallContent.CallId, functionResult) { Exception = result.Exception };
//    }


//    /// <summary>Invokes the function asynchronously.</summary>
//    /// <param name = "context" >
//    /// The function invocation context detailing the function to be invoked and its arguments along with additional request information.
//    /// </param>
//    /// <param name = "cancellationToken" > The < see cref= "CancellationToken" /> to monitor for cancellation requests. The default is <see cref = "CancellationToken.None" />.</ param >
//    /// < returns > The result of the function invocation, or<see langword = "null" /> if the function invocation returned <see langword = "null" />.</ returns >
//    /// < exception cref= "ArgumentNullException" >< paramref name= "context" /> is < see langword= "null" />.</ exception >
//    private async Task<object?> InstrumentedInvokeFunctionAsync(FunctionInvocation context, CancellationToken cancellationToken = default)
//    {
//        _ = Throw.IfNull(context);

//        Debug.Assert(!context.RequiresApproval, "An unapproved Function should not be able to reach this.");

//        if (context.Tool is not AIFunction function)
//        {
//            throw new InvalidOperationException("The specified tool is not a function.");
//        }

//        using Activity? activity = _activitySource?.StartActivity(
//            $"{GenAI.OperationNameValues.ExecuteTool} {context.Tool?.Name}",
//            ActivityKind.Internal,
//            default(ActivityContext),
//            [
//                new(GenAI.AttributeGenAiOperationName, GenAI.OperationNameValues.ExecuteTool),
//                new(GenAI.AttributeGenAiToolType, GenAI.ToolTypeFunction),
//                new(GenAI.AttributeGenAiToolCallId, context.CallContent.CallId),
//                new(GenAI.AttributeGenAiToolName, function.Name),
//                new(GenAI.AttributeGenAiToolDescription, function.Description),
//            ]);

//        var startingTimestamp = Stopwatch.GetTimestamp();

//        var enableSensitiveData = activity is { IsAllDataRequested: true } && InnerSession.GetService<OpenTelemetryConversationSession>()?.EnableSensitiveData is true;
//        var traceLoggingEnabled = _logger.IsEnabled(LogLevel.Trace);
//        var loggedInvoke = false;
//        if (enableSensitiveData || traceLoggingEnabled)
//        {
//            string functionArguments = TelemetryHelpers.AsJson(context.CallContent.Arguments, function.JsonSerializerOptions);

//            if (enableSensitiveData)
//            {
//                _ = activity?.SetTag(GenAI.Tool.Definitions, functionArguments);
//            }

//            if (traceLoggingEnabled)
//            {
//                LogInvokingSensitive(function.Name, functionArguments);
//                loggedInvoke = true;
//            }
//        }

//        if (!loggedInvoke && _logger.IsEnabled(LogLevel.Debug))
//        {
//            LogInvoking(function.Name);
//        }
//        var arguments = context.GetArguments(FunctionInvocationServices);

//        object? result = null;
//        try
//        {
//            result = await function.InvokeAsync(arguments, cancellationToken);
//        }
//        catch (Exception e)
//        {
//            if (activity is not null)
//            {
//                _ = activity.SetTag(Error.AttributeErrorType, e.GetType().FullName)
//                            .SetStatus(ActivityStatusCode.Error, e.Message);
//            }

//            if (e is OperationCanceledException)
//            {
//                LogInvocationCanceled(function.Name);
//            }
//            else
//            {
//                LogInvocationFailed(function.Name, e);
//            }

//            throw;
//        }
//        finally
//        {
//            bool loggedResult = false;
//            if (enableSensitiveData || traceLoggingEnabled)
//            {
//                string functionResult = TelemetryHelpers.AsJson(result, function.JsonSerializerOptions);

//                if (enableSensitiveData)
//                {
//                    _ = activity?.SetTag(GenAI.Tool.Call.Result, functionResult);
//                }

//                if (traceLoggingEnabled)
//                {
//                    LogInvocationCompletedSensitive(function.Name, GetElapsedTime(startingTimestamp), functionResult);
//                    loggedResult = true;
//                }
//            }

//            if (!loggedResult && _logger.IsEnabled(LogLevel.Debug))
//            {
//                LogInvocationCompleted(function.Name, GetElapsedTime(startingTimestamp));
//            }
//        }

//        return result;
//    }
//    private static TimeSpan GetElapsedTime(long startingTimestamp) =>
//#if NET
//    Stopwatch.GetElapsedTime(startingTimestamp);
//#else
//        new((long)((Stopwatch.GetTimestamp() - startingTimestamp) * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency)));
//#endif
//    public override void Dispose()
//    {
//        // Signal shutdown
//        if(!_shutdownCts.IsCancellationRequested)
//        {
//            _shutdownCts.Cancel();
//        }

//        // Complete the channel to stop accepting new work
//        _functionInvocationChannel.Writer.TryComplete();

//        // Wait for active tasks with a timeout
//        try
//        {
//            var allTasks = _activeFunctionTasks.ToArray();
//            if (allTasks.Length > 0)
//            {
//                Task.WhenAll(allTasks).Wait(TimeSpan.FromSeconds(5));
//            }

//            _functionProcessorTask?.Wait(TimeSpan.FromSeconds(1));
//        }
//        catch (AggregateException)
//        {
//            // Tasks were cancelled or failed during shutdown
//        }

//        _shutdownCts.Dispose();
//        base.Dispose();
//    }

//    public async ValueTask DisposeAsync()
//    {
//        await _shutdownCts.CancelAsync();
//        try
//        {
//            var allTasks = _activeFunctionTasks.ToArray();
//            if (allTasks.Length > 0)
//            {
//                await Task.WhenAll(allTasks).WaitAsync(TimeSpan.FromSeconds(5));
//            }

//            if(_functionProcessorTask is not null)
//            {
//                await _functionProcessorTask.WaitAsync(TimeSpan.FromSeconds(1));
//            }
//        }
//        catch (AggregateException)
//        {
//            // Tasks were cancelled or failed during shutdown
//        }
//        Dispose();
//    }


//    [LoggerMessage(LogLevel.Debug, "Invoking {MethodName}.", SkipEnabledCheck = true)]
//    private partial void LogInvoking(string methodName);

//    [LoggerMessage(LogLevel.Trace, "Invoking {MethodName}({Arguments}).", SkipEnabledCheck = true)]
//    private partial void LogInvokingSensitive(string methodName, string arguments);

//    [LoggerMessage(LogLevel.Debug, "{MethodName} invocation completed. Duration: {Duration}", SkipEnabledCheck = true)]
//    private partial void LogInvocationCompleted(string methodName, TimeSpan duration);

//    [LoggerMessage(LogLevel.Trace, "{MethodName} invocation completed. Duration: {Duration}. Result: {Result}", SkipEnabledCheck = true)]
//    private partial void LogInvocationCompletedSensitive(string methodName, TimeSpan duration, string result);

//    [LoggerMessage(LogLevel.Debug, "{MethodName} invocation canceled.")]
//    private partial void LogInvocationCanceled(string methodName);

//    [LoggerMessage(LogLevel.Error, "{MethodName} invocation failed.")]
//    private partial void LogInvocationFailed(string methodName, Exception error);


//    public sealed class FunctionInvocation
//    {
//        public required FunctionCallContent CallContent { get; set; }
//        public FunctionResultContent? ResultContent { get; set; }
//        public FunctionApprovalRequestContent? ApprovalRequest { get; set; }
//        public FunctionApprovalResponseContent? ApprovalResponse { get; set; }

//        public AITool? Tool { get; set; }
//        public AIFunctionArguments GetArguments(IServiceProvider? sp)
//        {

//            var args = new AIFunctionArguments(CallContent.Arguments)
//            {
//                Services = sp,
//            };
//            args.Context ??= new Dictionary<object, object?>();
//            args.Context.TryAdd("CallId", CallContent.CallId);
//            if(ApprovalRequest?.Id != null)
//            {
//                args.Context.TryAdd("ApprovalId", ApprovalRequest.Id);
//            }
//            return args;
//        }

//        public bool RequiresApproval => Tool?.GetService<ApprovalRequiredAIFunction>() != null
//            && ApprovalResponse is null;
//    }

//    /// <summary>Provides information about the invocation of a function call.</summary>
//    public sealed class FunctionInvocationResult
//    {
//        /// <summary>
//        /// Initializes a new instance of the <see cref="FunctionInvocationResult"/> class.
//        /// </summary>
//        /// <param name="terminate">Indicates whether the caller should terminate the processing loop.</param>
//        /// <param name="status">Indicates the status of the function invocation.</param>
//        /// <param name="callContent">Contains information about the function call.</param>
//        /// <param name="result">The result of the function call.</param>
//        /// <param name="exception">The exception thrown by the function call, if any.</param>
//        internal FunctionInvocationResult(bool terminate, FunctionInvocationStatus status, FunctionCallContent callContent, object? result, Exception? exception)
//        {
//            Terminate = terminate;
//            Status = status;
//            CallContent = callContent;
//            Result = result;
//            Exception = exception;
//        }

//        /// <summary>Gets status about how the function invocation completed.</summary>
//        public FunctionInvocationStatus Status { get; }

//        /// <summary>Gets the function call content information associated with this invocation.</summary>
//        public FunctionCallContent CallContent { get; }

//        /// <summary>Gets the result of the function call.</summary>
//        public object? Result { get; }

//        /// <summary>Gets any exception the function call threw.</summary>
//        public Exception? Exception { get; }

//        /// <summary>Gets a value indicating whether the caller should terminate the processing loop.</summary>
//        public bool Terminate { get; }
//    }

//    /// <summary>Provides error codes for when errors occur as part of the function calling loop.</summary>
//    public enum FunctionInvocationStatus
//    {
//        Rejected,
//        /// <summary>The operation completed successfully.</summary>
//        RanToCompletion,

//        /// <summary>The requested function could not be found.</summary>
//        NotFound,

//        /// <summary>The function call failed with an exception.</summary>
//        Exception,
//    }
//    private struct ApprovalResultWithRequestMessage
//    {
//        public FunctionApprovalResponseContent Response { get; set; }
//        public ChatMessage? RequestMessage { get; set; }
//    }
//}

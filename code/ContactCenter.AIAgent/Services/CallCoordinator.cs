using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Calling.Core;
using Agents.AI.ContactCenter.Coordination;
using Agents.AI.Monitoring.Correlation;
using Azure.Communication.CallAutomation;
using Azure.Messaging;
using ContactCenter.AIAgent.Configuration;
using Microsoft.Extensions.Options;

namespace ContactCenter.AIAgent.Services;

/// <summary>Validated incoming-call facts passed from the authenticated Event Grid endpoint.</summary>
public sealed record IncomingDemoCall(string EventId, string IncomingContext, IncomingCallContext Caller);

public interface ICallCoordinator
{
    Task ReceiveAsync(IncomingDemoCall incoming, CancellationToken ct);
    Task CallbackAsync(string routeId, IReadOnlyList<CloudEvent> events, CancellationToken ct);
    Task RunMediaAsync(string routeId, WebSocket socket, CancellationToken ct);
    bool HasRoute(string routeId);
    Task TransferAsync(string callId, CancellationToken ct);
    Task FinishAsync(string callId, string prompt, CancellationToken ct);
}

/// <summary>Single-instance demo orchestration. No cross-replica routing or durable bank is claimed.</summary>
public sealed class CallCoordinator(
    ICallSessionFactory sessions,
    IIncomingCallAdmissionController admission,
    IAcsCallGateway gateway,
    IOptions<BankingDemoOptions> options,
    ICallCorrelationAccessor correlation,
    ILogger<CallCoordinator> logger) : BackgroundService, ICallCoordinator
{
    public const string WorkflowId = "demo-bank@1";
    private readonly ConcurrentDictionary<string, Entry> _routes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Entry> _calls = new(StringComparer.Ordinal);

    public bool HasRoute(string routeId) => _routes.TryGetValue(routeId, out var entry)
        && Volatile.Read(ref entry.MediaAttached) == 0 && !entry.RemoteEnded && !entry.AnswerFailed;

    public async Task ReceiveAsync(IncomingDemoCall incoming, CancellationToken ct)
    {
        var decision = await admission.TryAcquireAsync(incoming.Caller.CallId, incoming.EventId, ct).ConfigureAwait(false);
        if (!decision.Acquired)
        {
            logger.LogInformation("Incoming delivery is already owned or requires recovery; outcome={Outcome}.", decision.Outcome);
            return;
        }

        Entry? entry = null;
        var answering = false;
        var previousContext = correlation.Current;
        var callContext = CallCorrelationContext.New(incoming.Caller.CallId, incoming.Caller.CorrelationId);
        try
        {
            correlation.Current = callContext;
            CallSessionAcquisition acquired;
            try
            {
                acquired = await sessions.CreateAsync(new() { CallContext = incoming.Caller, WorkflowId = WorkflowId }, ct).ConfigureAwait(false);
            }
            catch (Agents.AI.ContactCenter.Exceptions.CapacityExhaustedException)
            {
                if (!await admission.MarkAnsweringAsync(incoming.Caller.CallId, ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("Overflow redirect admission was lost.");
                }
                answering = true;
                await gateway.RedirectAsync(incoming.IncomingContext, options.Value.OperatorNumber, ct).ConfigureAwait(false);
                if (!await admission.MarkAnsweredAsync(incoming.Caller.CallId, "redirected-to-operator", CancellationToken.None).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("The redirect outcome could not be recorded.");
                }
                logger.LogInformation("Call capacity exhausted; redirected the incoming call to the configured operator.");
                return;
            }
            entry = new Entry(acquired.Session) { Context = callContext };
            if (!_calls.TryAdd(incoming.Caller.CallId, entry) || !_routes.TryAdd(entry.RouteId, entry))
            {
                throw new InvalidOperationException("A demo call is already registered.");
            }
            acquired.Session.StateChanged += state =>
            {
                if (state == CallSessionState.Ended) { entry.Ended.TrySetResult(); }
                return ValueTask.CompletedTask;
            };
            if (!await admission.MarkAnsweringAsync(incoming.Caller.CallId, ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Answer admission was lost.");
            }
            answering = true;
            var baseUri = options.Value.PublicBaseUri!;
            var callback = new Uri(baseUri, $"automation/callbacks/{entry.RouteId}");
            var media = new UriBuilder(new Uri(baseUri, $"automation/media/{entry.RouteId}")) { Scheme = "wss", Port = baseUri.IsDefaultPort ? -1 : baseUri.Port }.Uri;
            var connection = await gateway.AnswerAsync(incoming.IncomingContext, callback, media, ct).ConfigureAwait(false);
            entry.Connection.TrySetResult(connection);
            entry.Context = entry.Context with { AcsCallConnectionId = connection.CallConnectionId };
            if (!await admission.MarkAnsweredAsync(incoming.Caller.CallId, connection.CallConnectionId, CancellationToken.None).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The answer result could not be recorded.");
            }
        }
        catch
        {
            await admission.MarkAnswerFailedAsync(incoming.Caller.CallId, retryable: !answering, CancellationToken.None).ConfigureAwait(false);
            if (entry is not null)
            {
                entry.AnswerFailed = true;
                entry.Connection.TrySetException(new InvalidOperationException("The answer outcome is unavailable."));
                await entry.Session.EndAsync("answer_failed", CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
        finally { correlation.Current = previousContext; }
    }

    public async Task RunMediaAsync(string routeId, WebSocket socket, CancellationToken ct)
    {
        if (!_routes.TryGetValue(routeId, out var entry)) { throw new KeyNotFoundException("Unknown media route."); }
        if (Interlocked.CompareExchange(ref entry.MediaAttached, 1, 0) != 0)
        {
            throw new InvalidOperationException("This call already has a media connection.");
        }
        var prior = correlation.Current;
        try
        {
            var connection = await entry.Connection.Task.WaitAsync(TimeSpan.FromSeconds(options.Value.MediaConnectTimeoutSeconds), ct).ConfigureAwait(false);
            correlation.Current = entry.Context;
            var edge = gateway.CreateEdge(socket, connection, entry.Session.CallInformation, ct);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startup.CancelAfter(TimeSpan.FromSeconds(options.Value.MediaConnectTimeoutSeconds));
            if (!await entry.Session.AttachCallerEdgeAsync(edge, startup.Token).ConfigureAwait(false))
            {
                await edge.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException("The media edge could not be attached.");
            }
            startup.CancelAfter(Timeout.InfiniteTimeSpan);
            await entry.Ended.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            correlation.Current = prior;
            await entry.Session.EndAsync("media_closed", CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task CallbackAsync(string routeId, IReadOnlyList<CloudEvent> events, CancellationToken ct)
    {
        if (!_routes.TryGetValue(routeId, out var entry))
        {
            logger.LogDebug("Acknowledging a late callback for a retired route.");
            return;
        }
        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        var prior = correlation.Current;
        try
        {
            correlation.Current = entry.Context;
            foreach (var envelope in events)
            {
                if (entry.SeenEvents.Contains(envelope.Id)) { continue; }
                var item = CallAutomationEventParser.Parse(envelope);
                if (!string.IsNullOrEmpty(item.ServerCallId) && item.ServerCallId != entry.Session.CallId)
                {
                    throw new UnauthorizedAccessException("Callback does not belong to the routed server call.");
                }
                if (entry.AnswerFailed)
                {
                    if (item is CallConnected) { await gateway.HangUpAsync(item.CallConnectionId, ct).ConfigureAwait(false); }
                    entry.SeenEvents.Add(envelope.Id);
                    continue;
                }
                var connection = await entry.Connection.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                if (item.CallConnectionId != connection.CallConnectionId) { throw new UnauthorizedAccessException("Callback does not belong to the routed call."); }
                switch (item)
                {
                    case CallDisconnected:
                        entry.RemoteEnded = true;
                        await entry.Session.EndAsync("caller_disconnected", ct).ConfigureAwait(false);
                        break;
                    case CallTransferAccepted when entry.TransferRequested:
                        entry.RemoteEnded = true;
                        await entry.Session.EndAsync("operator_transfer_accepted", ct).ConfigureAwait(false);
                        break;
                    case CallTransferFailed when entry.TransferRequested:
                        await FinishCoreAsync(entry, "We could not connect you to an operator. Please call again later. Goodbye.", ct).ConfigureAwait(false);
                        break;
                    case PlayCompleted completed when entry.FinishOperation is not null && completed.OperationContext == entry.FinishOperation:
                    case PlayFailed failed when entry.FinishOperation is not null && failed.OperationContext == entry.FinishOperation:
                        await HangUpCoreAsync(entry, ct).ConfigureAwait(false);
                        break;
                }
                entry.SeenEvents.Add(envelope.Id);
                if (entry.SeenEvents.Count > 2048) { throw new InvalidOperationException("Demo callback event budget exceeded."); }
            }
        }
        finally { correlation.Current = prior; entry.Gate.Release(); }
    }

    public async Task TransferAsync(string callId, CancellationToken ct)
    {
        var entry = Get(callId);
        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (entry.TransferRequested || entry.RemoteEnded) { return; }
            var connection = await entry.Connection.Task.WaitAsync(ct).ConfigureAwait(false);
            entry.TransferRequested = true;
            entry.ControlDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
            await entry.Session.Strategy.SuspendAsync(ct).ConfigureAwait(false);
            if (entry.Session.CallerEdge is { } edge) { await edge.DispatchAsync(new OutboundDirective.StopPlayback(DateTimeOffset.UtcNow), ct).ConfigureAwait(false); }
            try { await gateway.TransferAsync(connection.CallConnectionId, options.Value.OperatorNumber, entry.Context.ContextId, ct).ConfigureAwait(false); }
            catch (Azure.RequestFailedException)
            {
                await FinishCoreAsync(entry, "The operator is unavailable. Please call again later. Goodbye.", ct).ConfigureAwait(false);
            }
        }
        finally { entry.Gate.Release(); }
    }

    public async Task FinishAsync(string callId, string prompt, CancellationToken ct)
    {
        var entry = Get(callId);
        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try { await FinishCoreAsync(entry, prompt, ct).ConfigureAwait(false); }
        finally { entry.Gate.Release(); }
    }

    private async Task FinishCoreAsync(Entry entry, string prompt, CancellationToken ct)
    {
        if (entry.RemoteEnded || entry.FinishOperation is not null) { return; }
        var connection = await entry.Connection.Task.WaitAsync(ct).ConfigureAwait(false);
        entry.FinishOperation = $"goodbye-{Guid.NewGuid():N}";
        entry.ControlDeadline = DateTimeOffset.UtcNow.AddSeconds(20);
        await entry.Session.Strategy.SuspendAsync(ct).ConfigureAwait(false);
        if (entry.Session.CallerEdge is { } edge) { await edge.DispatchAsync(new OutboundDirective.StopPlayback(DateTimeOffset.UtcNow), ct).ConfigureAwait(false); }
        await gateway.PlayAsync(connection.CallConnectionId, prompt, entry.FinishOperation, ct).ConfigureAwait(false);
    }

    private async Task HangUpCoreAsync(Entry entry, CancellationToken ct)
    {
        if (entry.RemoteEnded) { return; }
        var connection = await entry.Connection.Task.WaitAsync(ct).ConfigureAwait(false);
        await gateway.HangUpAsync(connection.CallConnectionId, ct).ConfigureAwait(false);
        entry.RemoteEnded = true;
        await entry.Session.EndAsync("demo_finished", ct).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            foreach (var entry in _routes.Values)
            {
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    if (entry.RemoteEnded || (entry.AnswerFailed && now - entry.CreatedAt > TimeSpan.FromMinutes(2)))
                    {
                        _routes.TryRemove(entry.RouteId, out _);
                        _calls.TryRemove(entry.Session.CallId, out _);
                        await entry.Session.DisposeAsync().ConfigureAwait(false);
                    }
                    else if (entry.Connection.Task.IsCompletedSuccessfully)
                    {
                        if (entry.ControlDeadline is { } deadline && now >= deadline)
                        {
                            await entry.Gate.WaitAsync(stoppingToken).ConfigureAwait(false);
                            try { await HangUpCoreAsync(entry, stoppingToken).ConfigureAwait(false); }
                            finally { entry.Gate.Release(); }
                        }
                        else if (!entry.TransferRequested && entry.FinishOperation is null
                            && (entry.Ended.Task.IsCompleted
                                || now - entry.CreatedAt > TimeSpan.FromSeconds(options.Value.MaximumCallSeconds)
                                || (entry.MediaAttached == 0 && now - entry.CreatedAt > TimeSpan.FromSeconds(options.Value.MediaConnectTimeoutSeconds))))
                        {
                            await TransferAsync(entry.Session.CallId, stoppingToken).ConfigureAwait(false);
                        }
                    }
                }
                catch (Azure.RequestFailedException ex) when (ex.Status is 404 or 410)
                {
                    logger.LogInformation("ACS call is no longer available during cleanup.");
                    entry.RemoteEnded = true;
                    await entry.Session.EndAsync("acs_call_gone", stoppingToken).ConfigureAwait(false);
                }
                catch (Azure.RequestFailedException ex) { logger.LogWarning(ex, "ACS demo cleanup operation failed."); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        foreach (var entry in _routes.Values)
        {
            if (entry.Connection.Task.IsCompletedSuccessfully && !entry.RemoteEnded)
            {
                try { await gateway.HangUpAsync(entry.Connection.Task.Result.CallConnectionId, cancellationToken).ConfigureAwait(false); }
                catch (Azure.RequestFailedException ex) { logger.LogWarning(ex, "ACS hangup failed during demo shutdown."); }
            }
            await entry.Session.EndAsync("host_stopping", cancellationToken).ConfigureAwait(false);
            await entry.Session.DisposeAsync().ConfigureAwait(false);
        }
        _routes.Clear();
        _calls.Clear();
    }

    private Entry Get(string callId) => _calls.TryGetValue(callId, out var entry) ? entry : throw new KeyNotFoundException("Demo call not found.");

    private sealed class Entry(ICallSession session)
    {
        public string RouteId { get; } = Guid.NewGuid().ToString("N");
        public ICallSession Session { get; } = session;
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        public CallCorrelationContext Context { get; set; } = CallCorrelationContext.New(session.CallId, session.CallInformation.CorrelationId);
        public TaskCompletionSource<CallConnectionProperties> Connection { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public HashSet<string> SeenEvents { get; } = new(StringComparer.Ordinal);
        public int MediaAttached;
        public bool AnswerFailed;
        public bool RemoteEnded;
        public bool TransferRequested;
        public string? FinishOperation;
        public DateTimeOffset? ControlDeadline;
    }
}

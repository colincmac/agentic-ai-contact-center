using Agents.AI.ContactCenter.Telemetry;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.IvrWorkflow.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent;

namespace Agents.AI.ContactCenter.Calling.Core;

/// <summary>
/// Default <see cref="ICallSessionFactory"/>. Creates the per-call DI scope, binds the
/// chosen workflow, resolves the top-tier <see cref="IConversationStrategy"/> from that
/// scope via keyed DI, then constructs the session.
/// </summary>
public sealed class CallSessionFactory : ICallSessionFactory
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICallSessionRegistry _registry;
    private readonly CallingTelemetry _telemetry;
    private readonly CancellationToken _applicationStopping;
    private readonly ConcurrentDictionary<string, Creation> _creations = new();

    public CallSessionFactory(
        IServiceScopeFactory scopeFactory,
        ICallSessionRegistry registry,
        CallingTelemetry telemetry,
        IHostApplicationLifetime? applicationLifetime = null)
    {
        ArgumentNullException.ThrowIfNull(telemetry);

        _scopeFactory = scopeFactory;
        _registry = registry;
        _telemetry = telemetry;
        _applicationStopping = applicationLifetime?.ApplicationStopping ?? CancellationToken.None;
    }

    public async Task<CallSessionAcquisition> CreateAsync(
        CallSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var callId = request.CallContext.CallId;
        while (true)
        {
            if (_creations.TryGetValue(callId, out var currentCreation))
            {
                ValidateCompatibleRequest(currentCreation.Request, request);
                return await AwaitCreationAsync(callId, currentCreation, cancellationToken).ConfigureAwait(false);
            }

            if (_registry.TryGet(callId) is { } existing)
            {
                ValidateCompatibleSession(existing, request);
                return new CallSessionAcquisition(existing, Created: false);
            }

            var candidate = new Creation(request);
            if (!_creations.TryAdd(callId, candidate))
            {
                continue;
            }

            candidate.Runner = RunCreationAsync(callId, candidate);
            return await AwaitCreationAsync(callId, candidate, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<CallSessionAcquisition> AwaitCreationAsync(
        string callId,
        Creation creation,
        CancellationToken cancellationToken)
    {
        var session = await creation.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        var created = Interlocked.CompareExchange(ref creation.Claimed, 1, 0) == 0;
        if (created)
        {
            ((ICollection<KeyValuePair<string, Creation>>)_creations)
                .Remove(new KeyValuePair<string, Creation>(callId, creation));
        }
        return new CallSessionAcquisition(session, created);
    }

    private async Task RunCreationAsync(string callId, Creation creation)
    {
        try
        {
            var session = await CreateAndPublishAsync(creation.Request).ConfigureAwait(false);
            creation.Completion.TrySetResult(session);
        }
        catch (Exception ex)
        {
            ((ICollection<KeyValuePair<string, Creation>>)_creations)
                .Remove(new KeyValuePair<string, Creation>(callId, creation));
            creation.Completion.TrySetException(ex);
        }
    }

    private async Task<ICallSession> CreateAndPublishAsync(CallSessionRequest request)
    {
        var callId = request.CallContext.CallId;

        var tier = request.PreferredTier ?? AgentTier.DtmfOnly;

        using var createSpan = _telemetry.StartChildActivity(CallingActivitySource.CreateSessionActivityName, callId);
        createSpan?.SetTag(CallingActivitySource.CallTierTag, tier.ToString());

        var scope = _scopeFactory.CreateAsyncScope();
        CallSession? session = null;
        try
        {
            scope.ServiceProvider.GetRequiredService<ICallContextAccessor>().Set(request.CallContext);
            scope.ServiceProvider.GetRequiredService<CallWorkflowSelection>().Set(request.WorkflowId);

            var strategy = scope.ServiceProvider.GetRequiredKeyedService<IConversationStrategy>(tier);
            _telemetry.CallCreated(request.CallContext.CallId, tier, strategy.Kind);
            createSpan?.SetTag(CallingActivitySource.CallStrategyKindTag, strategy.Kind.ToString());

            session = ActivatorUtilities.CreateInstance<CallSession>(
                scope.ServiceProvider,
                request.CallContext,
                strategy,
                scope);

            scope.ServiceProvider.GetRequiredService<ICallSessionAccessor>().Set(session);
            await session.StartAsync(_applicationStopping).ConfigureAwait(false);

            if (_registry.TryAdd(session))
            {
                return session;
            }

            var winner = _registry.TryGet(callId)
                ?? throw new InvalidOperationException(
                    $"Call session '{callId}' lost publication but no registry winner exists.");
            ValidateCompatibleSession(winner, request);
            await session.DisposeAsync().ConfigureAwait(false);
            return winner;
        }
        catch (Exception ex)
        {
            CallingActivitySource.SetError(createSpan, ex);

            if (session is not null)
            {
                _registry.TryRemove(callId, session);
                await session.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await scope.DisposeAsync().ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                $"Failed to create call session '{callId}' for tier {tier}. Ensure the " +
                "workflow and IConversationStrategy tier are registered.",
                ex);
        }
    }

    private static void ValidateCompatibleRequest(CallSessionRequest existing, CallSessionRequest requested)
    {
        if (existing.PreferredTier != requested.PreferredTier
            || !string.Equals(existing.WorkflowId, requested.WorkflowId, StringComparison.Ordinal)
            || !Equals(existing.CallContext, requested.CallContext))
        {
            throw new InvalidOperationException(
                $"A conflicting session creation is already in progress for call '{requested.CallContext.CallId}'.");
        }
    }

    private static void ValidateCompatibleSession(ICallSession existing, CallSessionRequest requested)
    {
        if (!Equals(existing.CallInformation, requested.CallContext)
            || requested.PreferredTier is { } requestedTier && existing.Strategy.Tier != requestedTier)
        {
            throw new InvalidOperationException(
                $"Call '{requested.CallContext.CallId}' already has a session with conflicting routing inputs.");
        }
    }

    private sealed class Creation(CallSessionRequest request)
    {
        public CallSessionRequest Request { get; } = request;

        public TaskCompletionSource<ICallSession> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task? Runner { get; set; }

        public int Claimed;
    }
}

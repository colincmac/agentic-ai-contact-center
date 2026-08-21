using System.Threading.Channels;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Calling.Core;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.State.Projections;

namespace Agents.AI.ContactCenter.Tests.Calling;

public sealed class CallSessionRegistryTests
{
    [Fact]
    public async Task Concurrent_TryAdd_has_exactly_one_winner()
    {
        var registry = new CallSessionRegistry();
        await using var first = new StubSession(CallId);
        await using var second = new StubSession(CallId);

        var results = await Task.WhenAll(
            Task.Run(() => registry.TryAdd(first)),
            Task.Run(() => registry.TryAdd(second)));

        Assert.Single(results, static result => result);
        Assert.Contains(registry.TryGet(CallId), new ICallSession[] { first, second });
        Assert.Single(registry.ActiveSessions);
    }

    [Fact]
    public async Task TryRemove_requires_the_current_session_instance()
    {
        var registry = new CallSessionRegistry();
        await using var current = new StubSession(CallId);
        await using var stale = new StubSession(CallId);
        Assert.True(registry.TryAdd(current));

        Assert.False(registry.TryRemove(CallId, stale));
        Assert.Same(current, registry.TryGet(CallId));

        Assert.True(registry.TryRemove(CallId, current));
        Assert.Null(registry.TryGet(CallId));
    }

    private const string CallId = "registry-call";

    private sealed class StubSession(string callId) : ICallSession
    {
        public IncomingCallContext CallInformation { get; } = new()
        {
            CallId = callId,
            CallerIdentifier = "caller",
            CallTargetIdentifier = "target"
        };

        public string CallId { get; } = callId;
        public CallSessionState State => CallSessionState.Created;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public ICallEdge? CallerEdge => null;
        public IConversationStrategy Strategy { get; } = new StubStrategy();
        public ICallEdge? SupervisorEdge => null;
        public SupervisorMode? SupervisorMode => null;
        public IReadOnlyList<ICallObserver> Observers => [];
        public event Func<CallSessionState, ValueTask>? StateChanged
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> AttachCallerEdgeAsync(ICallEdge callerEdge, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task DetachCallerEdgeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> AttachSupervisorAsync(ICallEdge supervisorEdge, SupervisorMode mode, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> ChangeSupervisorModeAsync(SupervisorMode mode, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task DetachSupervisorAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ReplaceStrategyAsync(IConversationStrategy newStrategy, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task TransferAsync(TransferRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task HangUpAsync(bool hangUpForEveryone = true, string? reason = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EndAsync(string? reason = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => Strategy.DisposeAsync();
    }

    private sealed class StubStrategy : IConversationStrategy
    {
        private readonly Channel<OutboundDirective> _outbound = Channel.CreateUnbounded<OutboundDirective>();
        private readonly Channel<StrategyEvent> _events = Channel.CreateUnbounded<StrategyEvent>();

        public StrategyKind Kind => StrategyKind.Dtmf;
        public AgentTier Tier => AgentTier.DtmfOnly;
        public IvrSnapshot WorkflowState => IvrSnapshot.Empty;
        public EdgeCapabilities EmittedDirectives => EdgeCapabilities.None;
        public ChannelReader<OutboundDirective> Outbound => _outbound.Reader;
        public ChannelReader<StrategyEvent> Events => _events.Reader;
        public Task StartAsync(StrategyStartContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask SuspendAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ResumeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

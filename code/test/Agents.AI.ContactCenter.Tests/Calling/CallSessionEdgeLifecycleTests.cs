using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Calling.Core;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.State;
using Agents.AI.ContactCenter.State.Projections;
using Agents.AI.ContactCenter.State.Stores;
using Agents.AI.ContactCenter.Telemetry;
using Agents.AI.ContactCenter.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Tests.Calling;

public sealed class CallSessionEdgeLifecycleTests
{
    [Fact]
    public async Task Attach_DrainsStartupOutputBeforeAwaitingStrategy()
    {
        await using var fixture = await CallSessionFixture.CreateAsync();
        fixture.Strategy.OnStart = async ct =>
        {
            for (var i = 0; i < 10; i++)
            {
                await fixture.Strategy.EmitAsync(new OutboundDirective.Audio(new AudioFrame(new byte[] { 0, 0 }, DateTimeOffset.UtcNow)));
            }
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.True(await fixture.Session.AttachCallerEdgeAsync(new FakeCallerEdge("startup"), timeout.Token));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObserverOverflow_IsBounded_AndOnlyRequiredObserversEndCall(bool lossless)
    {
        var observer = new NonConsumingObserver(lossless);
        await using var fixture = await CallSessionFixture.CreateAsync(observers: [observer]);
        Assert.True(await fixture.Session.AttachCallerEdgeAsync(new FakeCallerEdge("caller")));
        for (var i = 0; i < 257; i++)
        {
            await fixture.Strategy.EmitEventAsync(new StrategyEvent.AgentUtterance("test", "event", DateTimeOffset.UtcNow));
        }
        await WaitUntilAsync(() => observer.Events?.Count == 256);
        if (lossless)
        {
            await WaitUntilAsync(() => fixture.Session.State == CallSessionState.Ended);
        }
        else
        {
            Assert.Equal(CallSessionState.Active, fixture.Session.State);
        }
    }

    private sealed class NonConsumingObserver(bool lossless) : ICallObserver
    {
        public string ObserverId => "bounded-test";
        public bool RequiresLosslessDelivery => lossless;
        public ChannelReader<StrategyEvent>? Events { get; private set; }
        public Task StartAsync(CallObservation observation, CancellationToken cancellationToken = default)
        {
            Events = observation.Events;
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Concurrent_StartAsync_callers_await_the_same_initialization()
    {
        var observer = new BlockingObserver();
        await using var fixture = await CallSessionFixture.CreateAsync(start: false, observers: [observer]);

        var firstStart = fixture.Session.StartAsync();
        await observer.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondStart = fixture.Session.StartAsync();

        Assert.False(secondStart.IsCompleted);
        observer.ReleaseStart.TrySetResult();
        await Task.WhenAll(firstStart, secondStart);

        Assert.Equal(1, observer.StartCount);
    }

    [Fact]
    public async Task Initialization_failure_is_shared_by_concurrent_and_future_callers()
    {
        var expected = new InvalidOperationException("observer start failed");
        var observer = new BlockingObserver { StartException = expected };
        await using var fixture = await CallSessionFixture.CreateAsync(start: false, observers: [observer]);

        var firstStart = fixture.Session.StartAsync();
        await observer.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondStart = fixture.Session.StartAsync();
        observer.ReleaseStart.TrySetResult();

        var firstException = await Assert.ThrowsAsync<InvalidOperationException>(() => firstStart);
        var secondException = await Assert.ThrowsAsync<InvalidOperationException>(() => secondStart);
        var futureException = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.StartAsync());

        Assert.Same(expected, firstException);
        Assert.Same(expected, secondException);
        Assert.Same(expected, futureException);
        Assert.Equal(1, observer.StartCount);
        Assert.Equal(CallSessionState.Faulted, fixture.Session.State);
    }

    [Fact]
    public async Task StartAsync_initializes_without_starting_strategy_or_requiring_edge()
    {
        await using var fixture = await CallSessionFixture.CreateAsync();

        Assert.Null(fixture.Session.CallerEdge);
        Assert.Equal(CallSessionState.Created, fixture.Session.State);
        Assert.Equal(0, fixture.Strategy.StartCount);
    }

    [Fact]
    public async Task AttachCallerEdgeAsync_starts_strategy_and_rejects_second_active_edge()
    {
        await using var fixture = await CallSessionFixture.CreateAsync();
        var first = new FakeCallerEdge("edge-1");
        var second = new FakeCallerEdge("edge-2");

        Assert.True(await fixture.Session.AttachCallerEdgeAsync(first));
        Assert.False(await fixture.Session.AttachCallerEdgeAsync(second));

        Assert.Same(first, fixture.Session.CallerEdge);
        Assert.Equal(CallSessionState.Active, fixture.Session.State);
        Assert.Equal(1, fixture.Strategy.StartCount);
        Assert.Same(first.Metadata, fixture.Strategy.StartContext!.CallerMetadata);

        await second.DisposeAsync();
    }

    [Fact]
    public async Task Detach_then_attach_replacement_preserves_strategy_and_workflow_state()
    {
        await using var fixture = await CallSessionFixture.CreateAsync();
        var workflowState = fixture.Strategy.WorkflowState;
        var first = new FakeCallerEdge("edge-1");
        var replacement = new FakeCallerEdge("edge-2");

        Assert.True(await fixture.Session.AttachCallerEdgeAsync(first));
        await fixture.Session.DetachCallerEdgeAsync();

        Assert.Null(fixture.Session.CallerEdge);
        Assert.Equal(CallSessionState.Suspended, fixture.Session.State);
        Assert.Equal(1, fixture.Strategy.SuspendCount);

        Assert.True(await fixture.Session.AttachCallerEdgeAsync(replacement));
        Assert.Same(replacement, fixture.Session.CallerEdge);
        Assert.Same(workflowState, fixture.Strategy.WorkflowState);
        Assert.Equal(1, fixture.Strategy.StartCount);
        Assert.Equal(1, fixture.Strategy.ResumeCount);
        Assert.Equal(CallSessionState.Active, fixture.Session.State);
    }

    [Fact]
    public async Task Caller_edge_disconnect_ends_session()
    {
        await using var fixture = await CallSessionFixture.CreateAsync();
        var edge = new FakeCallerEdge("edge-1");
        Assert.True(await fixture.Session.AttachCallerEdgeAsync(edge));

        await edge.HangupAsync();
        await WaitUntilAsync(() => fixture.Session.State == CallSessionState.Ended);

        Assert.Null(fixture.Registry.TryGet(fixture.Session.CallId));
    }

    [Fact]
    public async Task Concurrent_EndAsync_callers_await_the_same_teardown()
    {
        await using var fixture = await CallSessionFixture.CreateAsync();
        Assert.True(await fixture.Session.AttachCallerEdgeAsync(new FakeCallerEdge("edge-1")));
        fixture.Strategy.BlockStop = true;

        var firstEnd = fixture.Session.EndAsync("first");
        await fixture.Strategy.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondEnd = fixture.Session.EndAsync("second");

        Assert.False(secondEnd.IsCompleted);
        fixture.Strategy.ReleaseStop.TrySetResult();
        await Task.WhenAll(firstEnd, secondEnd);

        Assert.Equal(1, fixture.Strategy.StopCount);
        Assert.Equal(CallSessionState.Ended, fixture.Session.State);
    }

    [Fact]
    public async Task EndAsync_cancels_and_joins_in_progress_initialization()
    {
        var observer = new BlockingObserver();
        await using var fixture = await CallSessionFixture.CreateAsync(start: false, observers: [observer]);
        var initialization = fixture.Session.StartAsync();
        await observer.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await fixture.Session.EndAsync("shutdown").WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);
        Assert.Equal(CallSessionState.Ended, fixture.Session.State);
        Assert.Null(fixture.Registry.TryGet(fixture.Session.CallId));
    }

    [Fact]
    public async Task Transfer_without_edge_reports_clear_error_and_hangup_ends_locally()
    {
        await using var fixture = await CallSessionFixture.CreateAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Session.TransferAsync(new TransferRequest(
                "+15555550100",
                TransferKind.BlindToPhoneNumber,
                CustomContext: null)));

        Assert.Contains("no caller edge is attached", exception.Message, StringComparison.Ordinal);

        await fixture.Session.HangUpAsync();
        Assert.Equal(CallSessionState.Ended, fixture.Session.State);
    }

    [Fact]
    public async Task First_state_transition_latency_starts_at_session_creation()
    {
        using var measurements = new MetricCollector("contact_center.call.state_transition.latency");
        await using var fixture = await CallSessionFixture.CreateAsync();

        Assert.True(await fixture.Session.AttachCallerEdgeAsync(new FakeCallerEdge("edge-1")));

        Assert.NotEmpty(measurements.Values);
        Assert.All(measurements.Values, value => Assert.InRange(value, 0, TimeSpan.FromMinutes(1).TotalMilliseconds));
    }

    [Fact]
    public async Task First_audio_metric_ignores_non_audio_directives()
    {
        using var measurements = new MetricCollector("contact_center.call.time_to_first_audio");
        await using var fixture = await CallSessionFixture.CreateAsync();
        Assert.True(await fixture.Session.AttachCallerEdgeAsync(new FakeCallerEdge("edge-1")));

        await fixture.Strategy.EmitAsync(new OutboundDirective.StopPlayback(DateTimeOffset.UtcNow));
        await Task.Delay(50);
        Assert.Empty(measurements.Values);

        await fixture.Strategy.EmitAsync(new OutboundDirective.Audio(
            new AudioFrame(new byte[] { 1, 2 }, DateTimeOffset.UtcNow)));
        await WaitUntilAsync(() => measurements.Values.Count == 1);
    }

    [Fact]
    public async Task Caller_dispatch_failure_faults_and_ends_the_session()
    {
        await using var fixture = await CallSessionFixture.CreateAsync();
        var states = new List<CallSessionState>();
        fixture.Session.StateChanged += state =>
        {
            states.Add(state);
            return ValueTask.CompletedTask;
        };
        var edge = new FakeCallerEdge(
            "edge-1",
            dispatchException: new InvalidOperationException("dispatch failed"));
        Assert.True(await fixture.Session.AttachCallerEdgeAsync(edge));

        await fixture.Strategy.EmitAsync(new OutboundDirective.Audio(
            new AudioFrame(new byte[] { 1, 2 }, DateTimeOffset.UtcNow)));
        await WaitUntilAsync(() => fixture.Session.State == CallSessionState.Ended);

        Assert.Contains(CallSessionState.Faulted, states);
        Assert.Null(fixture.Registry.TryGet(fixture.Session.CallId));
    }

    [Fact]
    public async Task Top_level_strategy_fault_faults_and_ends_the_session()
    {
        await using var fixture = await CallSessionFixture.CreateAsync();
        var states = new List<CallSessionState>();
        fixture.Session.StateChanged += state =>
        {
            states.Add(state);
            return ValueTask.CompletedTask;
        };
        Assert.True(await fixture.Session.AttachCallerEdgeAsync(new FakeCallerEdge("edge-1")));

        await fixture.Strategy.EmitEventAsync(new StrategyEvent.Faulted(
            "strategy failed",
            new InvalidOperationException("strategy failed"),
            DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => fixture.Session.State == CallSessionState.Ended);

        Assert.Contains(CallSessionState.Faulted, states);
        Assert.Null(fixture.Registry.TryGet(fixture.Session.CallId));
    }

    [Fact]
    public async Task Transfer_failure_resumes_strategy_and_restores_active_state()
    {
        await using var fixture = await CallSessionFixture.CreateAsync();
        var edge = new FakeCallerEdge(
            "edge-1",
            capabilities: EdgeCapabilities.Streaming | EdgeCapabilities.TransferCall,
            canControl: true,
            transferException: new InvalidOperationException("transfer failed"));
        var states = new List<CallSessionState>();
        fixture.Session.StateChanged += state =>
        {
            states.Add(state);
            return ValueTask.CompletedTask;
        };
        Assert.True(await fixture.Session.AttachCallerEdgeAsync(edge));

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.TransferAsync(
            new TransferRequest("+15555550100", TransferKind.BlindToPhoneNumber, null)));

        Assert.Contains(CallSessionState.Transferring, states);
        Assert.Equal(CallSessionState.Active, fixture.Session.State);
        Assert.Equal(1, fixture.Strategy.ResumeCount);
    }

    [Fact]
    public async Task Concurrent_supervisor_attach_allows_exactly_one_edge()
    {
        await using var fixture = await CallSessionFixture.CreateAsync();
        Assert.True(await fixture.Session.AttachCallerEdgeAsync(new FakeCallerEdge("caller")));

        var firstConnecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task BlockFirstConnectAsync(CancellationToken cancellationToken)
        {
            firstConnecting.TrySetResult();
            await releaseFirst.Task.WaitAsync(cancellationToken);
        }

        var secondConnectCount = 0;
        Task TrackSecondConnectAsync(CancellationToken _)
        {
            Interlocked.Increment(ref secondConnectCount);
            return Task.CompletedTask;
        }

        var first = new FakeCallerEdge("supervisor-1", CallEdgeKind.Supervisor, connectAsync: BlockFirstConnectAsync);
        var second = new FakeCallerEdge("supervisor-2", CallEdgeKind.Supervisor, connectAsync: TrackSecondConnectAsync);
        var firstAttach = fixture.Session.AttachSupervisorAsync(first, SupervisorMode.Monitor);
        await firstConnecting.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondAttach = fixture.Session.AttachSupervisorAsync(second, SupervisorMode.Monitor);
        Assert.Equal(0, Volatile.Read(ref secondConnectCount));

        releaseFirst.TrySetResult();
        var results = await Task.WhenAll(firstAttach, secondAttach);

        Assert.Equal([true, false], results);
        Assert.Same(first, fixture.Session.SupervisorEdge);
        Assert.Equal(0, second.DisposeCount);

        await second.DisposeAsync();
    }

    [Fact]
    public async Task Supervisor_attach_rejects_non_supervisor_edge_before_connect()
    {
        await using var fixture = await CallSessionFixture.CreateAsync();
        var connectCount = 0;
        var edge = new FakeCallerEdge(
            "not-a-supervisor",
            connectAsync: _ =>
            {
                Interlocked.Increment(ref connectCount);
                return Task.CompletedTask;
            });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Session.AttachSupervisorAsync(edge, SupervisorMode.Monitor));

        Assert.Equal(0, connectCount);
        Assert.Equal(0, edge.DisposeCount);
        await edge.DisposeAsync();
    }

    [Fact]
    public async Task Supervisor_attach_failure_rolls_back_and_disposes_connected_edge()
    {
        await using var fixture = await CallSessionFixture.CreateAsync();
        Assert.True(await fixture.Session.AttachCallerEdgeAsync(new FakeCallerEdge("caller")));
        fixture.Strategy.SuspendException = new InvalidOperationException("suspend failed");
        var supervisor = new FakeCallerEdge("supervisor", CallEdgeKind.Supervisor);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Session.AttachSupervisorAsync(supervisor, SupervisorMode.BargeIn));

        Assert.Null(fixture.Session.SupervisorEdge);
        Assert.Null(fixture.Session.SupervisorMode);
        Assert.Equal(CallSessionState.Active, fixture.Session.State);
        Assert.Equal(1, supervisor.DisposeCount);
        Assert.Equal(0, fixture.Strategy.ResumeCount);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, cts.Token);
        }
    }

    private sealed class CallSessionFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly CallStateProjector _projector;

        private CallSessionFixture(
            ServiceProvider services,
            CallStateProjector projector,
            CallSession session,
            TestStrategy strategy,
            CallSessionRegistry registry)
        {
            _services = services;
            _projector = projector;
            Session = session;
            Strategy = strategy;
            Registry = registry;
        }

        public CallSession Session { get; }

        public TestStrategy Strategy { get; }

        public CallSessionRegistry Registry { get; }

        public static async Task<CallSessionFixture> CreateAsync(
            bool start = true,
            IEnumerable<ICallObserver>? observers = null)
        {
            const string callId = "call-edge-lifecycle";
            var services = new ServiceCollection().BuildServiceProvider();
            var scope = services.CreateScope();
            var registry = new CallSessionRegistry();
            var projector = new CallStateProjector(
                callId,
                [new IvrStateProjection()],
                new InMemoryCallStateStore(),
                new CallStateOptions());
            var strategy = new TestStrategy();
            var session = new CallSession(
                new IncomingCallContext
                {
                    CallId = callId,
                    CallerIdentifier = "+15555550001",
                    CallTargetIdentifier = "+15555550002",
                },
                new CallSessionRouting(null, AgentTier.DtmfOnly, AgentTier.DtmfOnly),
                strategy,
                scope,
                new InMemoryCallQualityReporter(TestTelemetry.LoggerFactory, TestTelemetry.Calling),
                registry,
                projector,
                TestTelemetry.Calling,
                new CallTierAdmission(),
                observers);

            Assert.True(registry.TryAdd(session));
            if (start)
            {
                await session.StartAsync();
            }
            return new CallSessionFixture(services, projector, session, strategy, registry);
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            await _projector.DisposeAsync();
            await _services.DisposeAsync();
        }
    }

    private sealed class TestStrategy : IConversationStrategy
    {
        private readonly Channel<OutboundDirective> _outbound = Channel.CreateBounded<OutboundDirective>(8);
        private readonly Channel<StrategyEvent> _events = Channel.CreateUnbounded<StrategyEvent>();

        public StrategyKind Kind => StrategyKind.RealtimeVoice;

        public AgentTier Tier => AgentTier.RealtimeVoice;

        public IvrSnapshot WorkflowState { get; } = new() { Status = IvrWorkflowStatus.Running };

        public EdgeCapabilities EmittedDirectives => EdgeCapabilities.Streaming;

        public ChannelReader<OutboundDirective> Outbound => _outbound.Reader;

        public ChannelReader<StrategyEvent> Events => _events.Reader;

        public int StartCount { get; private set; }

        public int SuspendCount { get; private set; }

        public int ResumeCount { get; private set; }

        public int StopCount { get; private set; }

        public Exception? SuspendException { get; set; }

        public bool BlockStop { get; set; }

        public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseStop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StrategyStartContext? StartContext { get; private set; }
        public Func<CancellationToken, Task>? OnStart { get; set; }

        public ValueTask EmitAsync(OutboundDirective directive)
            => _outbound.Writer.WriteAsync(directive);

        public ValueTask EmitEventAsync(StrategyEvent strategyEvent)
            => _events.Writer.WriteAsync(strategyEvent);

        public async Task StartAsync(StrategyStartContext context, CancellationToken cancellationToken = default)
        {
            StartContext = context;
            StartCount++;
            if (OnStart is not null) { await OnStart(cancellationToken); }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            if (BlockStop)
            {
                StopEntered.TrySetResult();
                await ReleaseStop.Task.WaitAsync(cancellationToken);
            }
            _outbound.Writer.TryComplete();
            _events.Writer.TryComplete();
        }

        public ValueTask SuspendAsync(CancellationToken cancellationToken = default)
        {
            SuspendCount++;
            return SuspendException is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(SuspendException);
        }

        public ValueTask ResumeAsync(CancellationToken cancellationToken = default)
        {
            ResumeCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _outbound.Writer.TryComplete();
            _events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingObserver : ICallObserver
    {
        public string ObserverId => "blocking";

        public int StartCount { get; private set; }

        public Exception? StartException { get; init; }

        public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseStart { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task StartAsync(CallObservation observation, CancellationToken cancellationToken = default)
        {
            StartCount++;
            StartEntered.TrySetResult();
            await ReleaseStart.Task.WaitAsync(cancellationToken);
            if (StartException is not null)
            {
                throw StartException;
            }
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MetricCollector : IDisposable
    {
        private readonly MeterListener _listener = new();

        public MetricCollector(string instrumentName)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CallingActivitySource.MeterName
                    && instrument.Name == instrumentName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<double>((_, measurement, _, _) => Values.Add(measurement));
            _listener.Start();
        }

        public List<double> Values { get; } = [];

        public void Dispose() => _listener.Dispose();
    }
}

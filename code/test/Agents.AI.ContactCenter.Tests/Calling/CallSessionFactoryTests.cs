using System.Threading.Channels;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.DependencyInjection;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.State.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agents.AI.ContactCenter.Tests.Calling;

public sealed class CallSessionFactoryTests
{
    [Fact]
    public async Task Concurrent_creation_publishes_one_initialized_session_with_one_owner()
    {
        await using var rig = FactoryRig.Create();
        var first = rig.Factory.CreateAsync(Request);
        await rig.Observer.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = rig.Factory.CreateAsync(Request);
        Assert.Null(rig.Registry.TryGet(CallId));
        Assert.False(second.IsCompleted);

        rig.Observer.ReleaseStart.TrySetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Same(results[0].Session, results[1].Session);
        Assert.Single(results, static result => result.Created);
        Assert.Same(results[0].Session, rig.Registry.TryGet(CallId));
        Assert.Equal(1, rig.StrategyCreateCount);
        Assert.Equal(1, rig.Observer.StartCount);
    }

    [Fact]
    public async Task Canceled_waiter_does_not_cancel_creation_or_claim_ownership()
    {
        await using var rig = FactoryRig.Create();
        using var cts = new CancellationTokenSource();
        var canceledWaiter = rig.Factory.CreateAsync(Request, cts.Token);
        await rig.Observer.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWaiter);

        var retry = rig.Factory.CreateAsync(Request);
        rig.Observer.ReleaseStart.TrySetResult();
        var acquisition = await retry;

        Assert.True(acquisition.Created);
        Assert.Same(acquisition.Session, rig.Registry.TryGet(CallId));
        Assert.Equal(1, rig.StrategyCreateCount);
    }

    [Fact]
    public async Task Conflicting_duplicate_request_is_rejected()
    {
        await using var rig = FactoryRig.Create();
        var first = rig.Factory.CreateAsync(Request);
        await rig.Observer.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var conflicting = Request with { WorkflowId = "different-workflow" };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rig.Factory.CreateAsync(conflicting));

        Assert.Contains("conflicting session creation", exception.Message, StringComparison.Ordinal);
        rig.Observer.ReleaseStart.TrySetResult();
        await first;
    }

    [Fact]
    public async Task Initialization_failure_is_removed_and_a_later_delivery_can_retry()
    {
        await using var rig = FactoryRig.Create();
        rig.Observer.StartException = new InvalidOperationException("initialization failed");
        var failed = rig.Factory.CreateAsync(Request);
        await rig.Observer.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        rig.Observer.ReleaseStart.TrySetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => failed);
        Assert.Null(rig.Registry.TryGet(CallId));

        rig.Observer.StartException = null;
        var retry = await rig.Factory.CreateAsync(Request);

        Assert.True(retry.Created);
        Assert.Same(retry.Session, rig.Registry.TryGet(CallId));
        Assert.Equal(2, rig.StrategyCreateCount);
        Assert.Equal(2, rig.Observer.StartCount);
    }

    private const string CallId = "factory-call";

    private static CallSessionRequest Request => new()
    {
        CallContext = new IncomingCallContext
        {
            CallId = CallId,
            CallerIdentifier = "+15555550001",
            CallTargetIdentifier = "+15555550002"
        },
        PreferredTier = AgentTier.DtmfOnly
    };

    private sealed class FactoryRig : IAsyncDisposable
    {
        private readonly IHost _host;
        private int _strategyCreateCount;

        private FactoryRig(IHost host, BlockingObserver observer)
        {
            _host = host;
            Observer = observer;
        }

        public ICallSessionFactory Factory => _host.Services.GetRequiredService<ICallSessionFactory>();

        public ICallSessionRegistry Registry => _host.Services.GetRequiredService<ICallSessionRegistry>();

        public BlockingObserver Observer { get; }

        public int StrategyCreateCount => Volatile.Read(ref _strategyCreateCount);

        public static FactoryRig Create()
        {
            FactoryRig? rig = null;
            var observer = new BlockingObserver();
            var builder = Host.CreateEmptyApplicationBuilder(null);
            builder.AddContactCenter(new CommunicationOptions
            {
                Acs = new AcsOptions
                {
                    ConnectionString = "endpoint=https://example.invalid/;accesskey=test",
                    CallBackUri = new Uri("https://example.invalid/callback"),
                    MediaStreamingUri = new Uri("wss://example.invalid/media")
                },
                Teams = new TeamsOptions
                {
                    ResourceTenantId = "tenant",
                    ResourceObjectId = "resource",
                    PhoneNumber = "+15555550000"
                }
            });
            builder.Services.AddSingleton<ICallObserver>(observer);
            builder.Services.AddKeyedTransient<IConversationStrategy>(
                AgentTier.DtmfOnly,
                (_, _) =>
                {
                    Interlocked.Increment(ref rig!._strategyCreateCount);
                    return new TestStrategy();
                });

            var host = builder.Build();
            rig = new FactoryRig(host, observer);
            return rig;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var session in Registry.ActiveSessions)
            {
                await session.DisposeAsync();
            }
            _host.Dispose();
        }
    }

    private sealed class BlockingObserver : ICallObserver
    {
        public string ObserverId => "factory-blocking";

        public int StartCount { get; private set; }

        public Exception? StartException { get; set; }

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

    private sealed class TestStrategy : IConversationStrategy
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
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _outbound.Writer.TryComplete();
            _events.Writer.TryComplete();
            return Task.CompletedTask;
        }
        public ValueTask SuspendAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ResumeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

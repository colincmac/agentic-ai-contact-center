//using System.Collections.Concurrent;
//using System.Threading.Channels;
//using Agents.AI.ContactCenter.Calling;
//using Agents.AI.ContactCenter.Calling.Core;
//using Agents.AI.ContactCenter.Configuration;
//using Agents.AI.ContactCenter.Coordination;
//using Agents.AI.ContactCenter.Coordination.Core;
//using Agents.AI.ContactCenter.Exceptions;
//using Agents.AI.ContactCenter.IvrWorkflow;
//using Agents.AI.ContactCenter.Media.Audio;
//using Agents.AI.ContactCenter.Media.Signaling;
//using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
//using Microsoft.Extensions.DependencyInjection;
//using Microsoft.Extensions.Options;

//namespace Agents.AI.ContactCenter.Tests.Calling;

///// <summary>
///// Item 9 — Wire <see cref="CallSessionFactory"/> + <see cref="CallSession"/>
///// to <see cref="ICallOwnershipDirectory"/> + <see cref="IPodHeartbeat"/>.
///// </summary>
//public class CallSessionOwnershipTests
//{

//    private static IncomingCallContext CreateContext(string callId, string callerIdentifier = "+15555555", string callTargetIdentifier = "+15555555")
//    {
//        var context = new IncomingCallContext
//        {
//            CallId = callId,
//            CallerIdentifier = callerIdentifier,
//            CallTargetIdentifier = callTargetIdentifier,
//        };
//        return context;
//    }
//    [Fact]
//    public async Task CreateAsync_acquires_ownership_after_registry_add_and_tracks_in_heartbeat()
//    {
//        var (factory, registry, ownership, heartbeat, _, _) = CreateRig();

//        var session = await factory.CreateAsync(new CallSessionRequest
//        {
//            Call = CreateContext("call-own-1"),
//            CallerEdge = new FakeOwnershipEdge("call-own-1"),
//            PreferredTier = AgentTier.RealtimeVoice
//        });

//        Assert.NotNull(registry.TryGet("call-own-1"));
//        var owner = await ownership.GetOwnerAsync("call-own-1");
//        Assert.NotNull(owner);
//        Assert.Equal(CallOwnershipKind.Streaming, owner!.Kind);
//        Assert.True(heartbeat.TrackedCalls.ContainsKey("call-own-1"));
//        Assert.Equal(CallOwnershipKind.Streaming, heartbeat.TrackedCalls["call-own-1"]);

//        await session.DisposeAsync();
//    }

//    [Fact]
//    public async Task CreateAsync_unwinds_registry_and_throws_when_ownership_already_held_by_another_pod()
//    {
//        var (factory, registry, ownership, heartbeat, identity, _) = CreateRig();

//        // Pre-seed the ownership directory from a *different* pod so our local
//        // TryAcquire returns Acquired=false.
//        var foreignIdentity = new MutableClusterIdentity
//        {
//            ClusterId = identity.ClusterId,
//            PodId = "pod-foreign",
//            InstanceId = Guid.NewGuid().ToString("N"),
//        };
//        var foreignDirectory = new InMemoryCallOwnershipDirectory(
//            foreignIdentity,
//            Options.Create(new HyperscaleOptions()),
//            TimeProvider.System);
//        // Both directories share state? No — each InMemoryCallOwnershipDirectory has
//        // its own dict. Use a shared one instead by seeding through the real directory
//        // but temporarily swapping identity:
//        identity.PodId = "pod-foreign";
//        identity.InstanceId = Guid.NewGuid().ToString("N");
//        var preSeed = await ownership.TryAcquireAsync("call-conflict", CallOwnershipKind.Streaming);
//        Assert.True(preSeed.Acquired);
//        identity.PodId = "pod-local";
//        identity.InstanceId = Guid.NewGuid().ToString("N");

//        var edge = new FakeOwnershipEdge("call-conflict");
//        var ex = await Assert.ThrowsAsync<CallOwnershipConflictException>(() => factory.CreateAsync(new CallSessionRequest
//        {
//            Call = CreateContext("call-conflict"),
//            CallerEdge = edge,
//            PreferredTier = AgentTier.RealtimeVoice
//        }));

//        Assert.Equal("call-conflict", ex.CallId);
//        Assert.Equal("pod-foreign", ex.ExistingOwner.PodId);
//        Assert.Null(registry.TryGet("call-conflict"));
//        Assert.False(heartbeat.TrackedCalls.ContainsKey("call-conflict"));
//        Assert.True(edge.Disposed, "Failed acquire must dispose the session (which disposes the edge via the strategy).");
//        _ = foreignDirectory; // suppress unused warning
//    }

//    [Fact]
//    public async Task CreateAsync_without_ownership_directory_works_unchanged()
//    {
//        // No ownership / no heartbeat — back-compat path.
//        var services = BuildServicesWithFakeStrategy();
//        var registry = new CallSessionRegistry();
//        var factory = new CallSessionFactory(
//            services.GetRequiredService<IServiceScopeFactory>(),
//            registry,
//            new InMemoryCallQualityReporter(TestTelemetry.LoggerFactory, TestTelemetry.Calling),
//            TestTelemetry.LoggerFactory,
//            TestTelemetry.Calling);

//        var session = await factory.CreateAsync(new CallSessionRequest
//        {
//            Call = CreateContext("call-no-own"),
//            CallerEdge = new FakeOwnershipEdge("call-no-own"),
//            PreferredTier = AgentTier.RealtimeVoice
//        });

//        Assert.NotNull(registry.TryGet("call-no-own"));
//        await session.DisposeAsync();
//    }

//    [Fact]
//    public async Task EndAsync_releases_ownership_and_untracks_in_heartbeat()
//    {
//        var (factory, _, ownership, heartbeat, _, _) = CreateRig();

//        var session = await factory.CreateAsync(new CallSessionRequest
//        {
//            Call = CreateContext("call-release"),
//            CallerEdge = new FakeOwnershipEdge("call-release"),
//            PreferredTier = AgentTier.RealtimeVoice
//        });

//        // Sanity: ownership and heartbeat were populated by the factory.
//        Assert.NotNull(await ownership.GetOwnerAsync("call-release"));
//        Assert.True(heartbeat.TrackedCalls.ContainsKey("call-release"));

//        await session.EndAsync();

//        Assert.Null(await ownership.GetOwnerAsync("call-release"));
//        Assert.False(heartbeat.TrackedCalls.ContainsKey("call-release"));
//    }

//    [Fact]
//    public async Task EndAsync_swallows_ownership_release_failures()
//    {
//        var throwingOwnership = new ThrowingReleaseOwnershipDirectory();
//        var heartbeat = new RecordingPodHeartbeat();
//        var services = BuildServicesWithFakeStrategy();
//        var registry = new CallSessionRegistry();
//        var factory = new CallSessionFactory(
//            services.GetRequiredService<IServiceScopeFactory>(),
//            registry,
//            new InMemoryCallQualityReporter(TestTelemetry.LoggerFactory, TestTelemetry.Calling),
//            TestTelemetry.LoggerFactory,
//            TestTelemetry.Calling,
//            ownership: throwingOwnership,
//            heartbeat: heartbeat);

//        var session = await factory.CreateAsync(new CallSessionRequest
//        {
//            Call = CreateContext("call-throw"),
//            CallerEdge = new FakeOwnershipEdge("call-throw"),
//            PreferredTier = AgentTier.RealtimeVoice
//        });

//        // EndAsync must not bubble the release exception.
//        await session.EndAsync();

//        Assert.True(throwingOwnership.ReleaseAttempted);
//        Assert.False(heartbeat.TrackedCalls.ContainsKey("call-throw"),
//            "Heartbeat untrack must still run when ownership release throws.");
//    }

//    [Fact]
//    public async Task EndAsync_without_ownership_directory_completes_cleanly()
//    {
//        var services = BuildServicesWithFakeStrategy();
//        var registry = new CallSessionRegistry();
//        var factory = new CallSessionFactory(
//            services.GetRequiredService<IServiceScopeFactory>(),
//            registry,
//            new InMemoryCallQualityReporter(TestTelemetry.LoggerFactory, TestTelemetry.Calling),
//            TestTelemetry.LoggerFactory,
//            TestTelemetry.Calling);

//        var session = await factory.CreateAsync(new CallSessionRequest
//        {
//            Call = CreateContext("call-no-own-end"),
//            CallerEdge = new FakeOwnershipEdge("call-no-own-end"),
//            PreferredTier = AgentTier.RealtimeVoice
//        });

//        await session.EndAsync();
//        Assert.Equal(CallSessionState.Ended, session.State);
//    }

//    private static (CallSessionFactory Factory, CallSessionRegistry Registry, InMemoryCallOwnershipDirectory Ownership, RecordingPodHeartbeat Heartbeat, MutableClusterIdentity Identity, ServiceProvider Services)
//        CreateRig()
//    {
//        var services = BuildServicesWithFakeStrategy();
//        var identity = new MutableClusterIdentity
//        {
//            ClusterId = "cluster-1",
//            PodId = "pod-local",
//            InstanceId = Guid.NewGuid().ToString("N"),
//        };
//        var ownership = new InMemoryCallOwnershipDirectory(
//            identity,
//            Options.Create(new HyperscaleOptions()),
//            TimeProvider.System);
//        var heartbeat = new RecordingPodHeartbeat();
//        var registry = new CallSessionRegistry();

//        var factory = new CallSessionFactory(
//            services.GetRequiredService<IServiceScopeFactory>(),
//            registry,
//            new InMemoryCallQualityReporter(TestTelemetry.LoggerFactory, TestTelemetry.Calling),
//            TestTelemetry.LoggerFactory,
//            TestTelemetry.Calling,
//            ownership: ownership,
//            heartbeat: heartbeat);

//        return (factory, registry, ownership, heartbeat, identity, services);
//    }

//    /// <summary>
//    /// Builds a service provider with <see cref="FakeOwnershipStrategy"/> registered as a
//    /// keyed transient <see cref="IConversationStrategy"/> at <see cref="AgentTier.RealtimeVoice"/>,
//    /// which is what every test in this file resolves via <c>PreferredTier</c>. Each call to
//    /// <c>CallSessionFactory.CreateAsync</c> resolves a fresh instance so dispose at session end
//    /// is one-to-one.
//    /// </summary>
//    private static ServiceProvider BuildServicesWithFakeStrategy()
//    {
//        var collection = new ServiceCollection();
//        collection.AddKeyedTransient<IConversationStrategy>(
//            AgentTier.RealtimeVoice,
//            (_, _) => new FakeOwnershipStrategy());
//        return collection.BuildServiceProvider();
//    }

//    private sealed class MutableClusterIdentity : IClusterIdentity
//    {
//        public string ClusterId { get; set; } = "cluster-1";
//        public string PodId { get; set; } = "pod-local";
//        public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");
//    }

//    private sealed class RecordingPodHeartbeat : IPodHeartbeat
//    {
//        private readonly ConcurrentDictionary<string, CallOwnershipKind> _tracked = new();

//        public IReadOnlyDictionary<string, CallOwnershipKind> TrackedCalls => _tracked;

//        public void TrackOwnedCall(string callConnectionId, CallOwnershipKind kind)
//            => _tracked[callConnectionId] = kind;

//        public void UntrackOwnedCall(string callConnectionId)
//            => _tracked.TryRemove(callConnectionId, out _);
//    }

//    private sealed class ThrowingReleaseOwnershipDirectory : ICallOwnershipDirectory
//    {
//        public bool ReleaseAttempted { get; private set; }

//        public Task<CallOwnershipAcquireResult> TryAcquireAsync(string callConnectionId, CallOwnershipKind kind, CancellationToken cancellationToken = default)
//            => Task.FromResult(new CallOwnershipAcquireResult(true, new CallOwnership(
//                ClusterId: "cluster-1",
//                PodId: "pod-local",
//                InstanceId: "i-1",
//                Kind: kind,
//                LeaseUntil: DateTimeOffset.UtcNow.AddMinutes(1))));

//        public Task<CallOwnership?> GetOwnerAsync(string callConnectionId, CancellationToken cancellationToken = default)
//            => Task.FromResult<CallOwnership?>(null);

//        public Task<bool> RenewAsync(string callConnectionId, CallOwnershipKind kind, CancellationToken cancellationToken = default)
//            => Task.FromResult(true);

//        public Task<bool> ReleaseAsync(string callConnectionId, CancellationToken cancellationToken = default)
//        {
//            ReleaseAttempted = true;
//            throw new InvalidOperationException("simulated release failure");
//        }

//        public Task<int> ReapOrphansAsync(IPodLeaseStore podLeases, CancellationToken cancellationToken = default)
//            => Task.FromResult(0);
//    }

//    private sealed class FakeOwnershipStrategy : IConversationStrategy
//    {
//        private readonly Channel<OutboundDirective> _outbound = Channel.CreateUnbounded<OutboundDirective>();
//        private readonly Channel<StrategyEvent> _events = Channel.CreateUnbounded<StrategyEvent>();

//        public StrategyKind Kind => StrategyKind.RealtimeVoice;

//        public AgentTier Tier => AgentTier.RealtimeVoice;

//        public IvrWorkflowState WorkflowState { get; } = new() { Status = IvrWorkflowStatus.Running };

//        public EdgeCapabilities EmittedDirectives => EdgeCapabilities.Audio;

//        public ChannelReader<OutboundDirective> Outbound => _outbound.Reader;

//        public ChannelReader<StrategyEvent> Events => _events.Reader;

//        public Task StartAsync(StrategyStartContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

//        public Task StopAsync(CancellationToken cancellationToken = default)
//        {
//            _outbound.Writer.TryComplete();
//            _events.Writer.TryComplete();
//            return Task.CompletedTask;
//        }

//        public ValueTask SuspendAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

//        public ValueTask ResumeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

//        public ValueTask DisposeAsync()
//        {
//            _outbound.Writer.TryComplete();
//            _events.Writer.TryComplete();
//            return ValueTask.CompletedTask;
//        }
//    }

//    private sealed class FakeOwnershipEdge : ICallEdge
//    {
//        private readonly Channel<AudioFrame> _inboundAudio = Channel.CreateUnbounded<AudioFrame>();
//        private readonly Channel<DtmfTone> _inboundDtmf = Channel.CreateUnbounded<DtmfTone>();
//        private readonly Channel<SessionSignal> _inboundSignals = Channel.CreateUnbounded<SessionSignal>();

//        public FakeOwnershipEdge(string edgeId)
//        {
//            EdgeId = edgeId;
//        }

//        public string EdgeId { get; }

//        public CallEdgeKind Kind => CallEdgeKind.Caller;

//        public EdgeCapabilities Capabilities => EdgeCapabilities.Audio | EdgeCapabilities.CollectDtmf;

//        public CallEdgeMetadata Metadata { get; } = new() { DisplayName = "caller", RawIdentifier = "+15555550000" };

//        public bool IsConnected { get; private set; } = true;

//        public ChannelReader<AudioFrame> InboundAudio => _inboundAudio.Reader;

//        public ChannelReader<DtmfTone> InboundDtmf => _inboundDtmf.Reader;

//        public ChannelReader<SessionSignal> InboundSignals => _inboundSignals.Reader;

//        public bool Disposed { get; private set; }

//        public event Func<EdgeDisconnectedReason, ValueTask>? Disconnected;

//        public ValueTask DispatchAsync(OutboundDirective directive, CancellationToken cancellationToken = default)
//            => ValueTask.CompletedTask;

//        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

//        public ValueTask DisposeAsync()
//        {
//            Disposed = true;
//            IsConnected = false;
//            _inboundAudio.Writer.TryComplete();
//            _inboundDtmf.Writer.TryComplete();
//            _inboundSignals.Writer.TryComplete();
//            _ = Disconnected; // suppress unused-event warning
//            return ValueTask.CompletedTask;
//        }
//    }
//}

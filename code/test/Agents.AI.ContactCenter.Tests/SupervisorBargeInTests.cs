//using System.Threading.Channels;
//using Agents.AI.ContactCenter.Calling;
//using Agents.AI.ContactCenter.Calling.Core;
//using Agents.AI.ContactCenter.Configuration;
//using Agents.AI.ContactCenter.DependencyInjection;
//using Agents.AI.ContactCenter.IvrWorkflow;
//using Agents.AI.ContactCenter.IvrWorkflow.Catalog;
//using Agents.AI.ContactCenter.State.Projections;
//using Agents.AI.ContactCenter.Telemetry;
//using Agents.AI.ContactCenter.Tests.Helpers;
//using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
//using Microsoft.Extensions.DependencyInjection;

//namespace Agents.AI.RealtimeVoice.Azure.Tests.Proposed;

///// <summary>
///// Proves the new supervisor barge-in surface on <see cref="ICallSession"/>:
/////   * Monitor — supervisor receives both caller audio and agent audio.
/////   * BargeIn — strategy is suspended, supervisor audio bridges to the caller,
/////     no agent audio reaches the caller while suspended.
/////   * Mode change BargeIn → Monitor — strategy resumes, audio routing flips back.
/////   * Detach — supervisor pump stops, snapshot clears, alert is resolved.
///// </summary>
//public class SupervisorBargeInTests
//{


//    [Fact]
//    public async Task Monitor_taps_caller_and_agent_audio_to_supervisor()
//    {
//        await using var fixture = await CallFixture.StartAsync(SupervisorMode.Monitor);

//        Assert.Equal(SupervisorMode.Monitor, fixture.Session.SupervisorMode);
//        Assert.Equal(CallSessionState.Active, fixture.Session.State);

//        // Caller speaks → strategy hears it AND supervisor hears it.
//        var callerPcm = new byte[] { 0xAA, 0xBB };
//        await fixture.Caller.PushAudioAsync(callerPcm);

//        var strategyHeard = await ReadOneAsync(fixture.Strategy.Inbound.Reader, TimeSpan.FromSeconds(2));
//        Assert.Equal(callerPcm.Length, strategyHeard.Pcm.Length);

//        var supervisorHeardCaller = await ReadOneAsync(fixture.Supervisor.Sent.Reader, TimeSpan.FromSeconds(2));
//        Assert.Equal(callerPcm.Length, supervisorHeardCaller.Pcm.Length);

//        // Strategy speaks → caller hears it AND supervisor hears it.
//        var agentPcm = new byte[] { 0x11, 0x22, 0x33 };
//        await fixture.Strategy.EmitOutboundAsync(agentPcm);

//        var callerHeard = await ReadOneAsync(fixture.Caller.Sent.Reader, TimeSpan.FromSeconds(2));
//        Assert.Equal(agentPcm.Length, callerHeard.Pcm.Length);

//        var supervisorHeardAgent = await ReadOneAsync(fixture.Supervisor.Sent.Reader, TimeSpan.FromSeconds(2));
//        Assert.Equal(agentPcm.Length, supervisorHeardAgent.Pcm.Length);

//        // Quality snapshot reflects supervisor presence. The presence and the alert
//        // are published in two separate broadcasts; wait for the snapshot that has both.
//        var snap = await fixture.WaitForSnapshotAsync(s =>
//            s.Supervisor is not null
//            && s.Alerts.Any(a => a.Kind == QualityAlertKind.SupervisorWhisper));
//        Assert.Equal(SupervisorMode.Monitor, snap.Supervisor!.Mode);
//        Assert.Contains(snap.Alerts, a => a.Kind == QualityAlertKind.SupervisorWhisper);
//    }

//    [Fact]
//    public async Task BargeIn_suspends_strategy_and_bridges_supervisor_to_caller()
//    {
//        await using var fixture = await CallFixture.StartAsync(SupervisorMode.Monitor);

//        // Flip to BargeIn.
//        Assert.True(await fixture.Session.ChangeSupervisorModeAsync(SupervisorMode.BargeIn));

//        await fixture.WaitUntilAsync(() => fixture.Session.State == CallSessionState.Suspended);
//        Assert.Equal(SupervisorMode.BargeIn, fixture.Session.SupervisorMode);
//        Assert.True(fixture.Strategy.SuspendCount >= 1);

//        // Supervisor audio reaches the caller.
//        var supervisorPcm = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
//        await fixture.Supervisor.PushAudioAsync(supervisorPcm);

//        var callerHeardSupervisor = await ReadOneAsync(fixture.Caller.Sent.Reader, TimeSpan.FromSeconds(2));
//        Assert.Equal(supervisorPcm.Length, callerHeardSupervisor.Pcm.Length);

//        // Strategy audio that escapes during suspend does NOT reach the caller.
//        var strayAgentPcm = new byte[] { 0x99 };
//        await fixture.Strategy.EmitOutboundAsync(strayAgentPcm);

//        // Give the outbound pump a moment to run; assert nothing landed.
//        await Task.Delay(150);
//        Assert.False(fixture.Caller.Sent.Reader.TryRead(out _),
//            "Strategy audio must not reach the caller during BargeIn");

//        // Caller audio must NOT reach the strategy during BargeIn.
//        await fixture.Caller.PushAudioAsync(new byte[] { 0x77 });
//        await Task.Delay(150);
//        Assert.False(fixture.Strategy.Inbound.Reader.TryRead(out _),
//            "Caller audio must not reach the strategy during BargeIn");

//        // Snapshot reflects the new mode AND the suspended state. Wait for the
//        // snapshot that has both — they're published in two separate broadcasts.
//        var snap = await fixture.WaitForSnapshotAsync(s =>
//            s.Supervisor?.Mode == SupervisorMode.BargeIn
//            && s.State == CallSessionState.Suspended);
//        Assert.Equal(SupervisorMode.BargeIn, snap.Supervisor!.Mode);
//        Assert.Equal(CallSessionState.Suspended, snap.State);
//    }

//    [Fact]
//    public async Task BargeIn_to_Monitor_resumes_strategy_and_restores_routing()
//    {
//        await using var fixture = await CallFixture.StartAsync(SupervisorMode.BargeIn);

//        // Confirm we started suspended.
//        await fixture.WaitUntilAsync(() => fixture.Session.State == CallSessionState.Suspended);

//        // Flip back to Monitor.
//        Assert.True(await fixture.Session.ChangeSupervisorModeAsync(SupervisorMode.Monitor));

//        await fixture.WaitUntilAsync(() => fixture.Session.State == CallSessionState.Active);
//        Assert.Equal(SupervisorMode.Monitor, fixture.Session.SupervisorMode);
//        Assert.True(fixture.Strategy.ResumeCount >= 1);

//        // Strategy outbound reaches the caller again.
//        var resumedAgentPcm = new byte[] { 0x42 };
//        await fixture.Strategy.EmitOutboundAsync(resumedAgentPcm);

//        var callerHeardResumed = await ReadOneAsync(fixture.Caller.Sent.Reader, TimeSpan.FromSeconds(2));
//        Assert.Equal(1, callerHeardResumed.Pcm.Length);
//    }

//    //[Fact]
//    //public async Task Detach_clears_supervisor_state_and_resolves_alert()
//    //{
//    //    await using var fixture = await CallFixture.StartAsync(SupervisorMode.BargeIn);
//    //    await fixture.WaitUntilAsync(() => fixture.Session.State == CallSessionState.Suspended);

//    //    await fixture.Session.DetachSupervisorAsync();

//    //    await fixture.WaitUntilAsync(() => fixture.Session.SupervisorEdge is null);
//    //    Assert.Null(fixture.Session.SupervisorMode);

//    //    // Detaching from BargeIn lifts the suspend.
//    //    await fixture.WaitUntilAsync(() => fixture.Session.State == CallSessionState.Active);

//    //    var snap = await fixture.WaitForSnapshotAsync(s => s.Supervisor is null);
//    //    Assert.Null(snap.Supervisor);
//    //    Assert.DoesNotContain(snap.Alerts, a => a.Kind == QualityAlertKind.SupervisorWhisper);
//    //}

//    [Fact]
//    public async Task Whisper_forwards_supervisor_audio_to_whisperable_strategy()
//    {
//        await using var fixture = await CallFixture.StartAsync(SupervisorMode.Whisper);

//        // Whisper does not suspend the strategy.
//        Assert.Equal(CallSessionState.Active, fixture.Session.State);

//        var supervisorPcm = new byte[] { 0xCA, 0xFE };
//        await fixture.Supervisor.PushAudioAsync(supervisorPcm);

//        await fixture.WaitUntilAsync(() => fixture.Strategy.Whispers.Count > 0);
//        var whisper = fixture.Strategy.Whispers[0];
//        Assert.Equal(supervisorPcm.Length, whisper.Audio!.Value.Length);
//        Assert.Equal(fixture.Supervisor.EdgeId, whisper.SupervisorId);

//        // Strategy audio still reaches the caller (Whisper is non-disruptive).
//        var agentPcm = new byte[] { 0x10 };
//        await fixture.Strategy.EmitOutboundAsync(agentPcm);
//        var callerHeard = await ReadOneAsync(fixture.Caller.Sent.Reader, TimeSpan.FromSeconds(2));
//        Assert.Equal(agentPcm.Length, callerHeard.Pcm.Length);

//        // Caller audio reaches strategy AND supervisor (so supervisor stays in context).
//        var callerPcm = new byte[] { 0x20 };
//        await fixture.Caller.PushAudioAsync(callerPcm);
//        var strategyHeardCaller = await ReadOneAsync(fixture.Strategy.Inbound.Reader, TimeSpan.FromSeconds(2));
//        Assert.Equal(callerPcm.Length, strategyHeardCaller.Pcm.Length);
//        var supervisorHeardCaller = await ReadOneAsync(fixture.Supervisor.Sent.Reader, TimeSpan.FromSeconds(2));
//        Assert.Equal(callerPcm.Length, supervisorHeardCaller.Pcm.Length);
//    }

//    private static async Task<AudioFrame> ReadOneAsync(ChannelReader<AudioFrame> reader, TimeSpan timeout)
//    {
//        using var cts = new CancellationTokenSource(timeout);
//        return await reader.ReadAsync(cts.Token);
//    }
//}

///// <summary>
///// Test scaffolding: spins up a CallSession backed by a controllable strategy and
///// fake caller + supervisor edges so each test reads as a script of caller / agent /
///// supervisor speech.
///// </summary>
//internal sealed class CallFixture : IAsyncDisposable
//{
//    public required FakeCallerEdge Caller { get; init; }
//    public required FakeCallerEdge Supervisor { get; init; }
//    public required ICallSession Session { get; init; }
//    public required ControllableStrategy Strategy { get; init; }
//    public required InMemoryCallQualityReporter Quality { get; init; }
//    public required ChannelReader<CallQualitySnapshot> Snapshots { get; init; }
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
//    public static async Task<CallFixture> StartAsync(SupervisorMode supervisorMode)
//    {
//        var strategy = new ControllableStrategy();

//        // Register the test-double strategy as the keyed IConversationStrategy that the
//        // CallSessionFactory will resolve for AgentTier.RealtimeVoice.
//        var serviceCollection = new ServiceCollection();
//        serviceCollection.AddLogging();
//        serviceCollection.AddSingleton<CallingTelemetry>();
//        serviceCollection.AddScoped<CallWorkflowSelection>();

//        serviceCollection.AddScoped<ICallSessionRegistry, CallSessionRegistry>();
//        serviceCollection.AddScoped<ICallContextAccessor, CallContextAccessor>();
//        serviceCollection.AddScoped<ICallSessionAccessor, CallSessionAccessor>();
//        serviceCollection.AddScoped<ICallQualityReporter, InMemoryCallQualityReporter>();
//        serviceCollection.AddKeyedSingleton<IConversationStrategy>(AgentTier.RealtimeVoice, strategy);
//        serviceCollection.AddDefaultCallStatePlane();

//        var services = serviceCollection.BuildServiceProvider();

//        var quality = new InMemoryCallQualityReporter(TestTelemetry.LoggerFactory, TestTelemetry.Calling);
//        var registry = new CallSessionRegistry();

//        var caller = new FakeCallerEdge("caller-1");
//        var supervisor = new FakeCallerEdge("supervisor-1", CallEdgeKind.Supervisor, "Floor Lead");
//        var factory = new CallSessionFactory(
//            services.GetRequiredService<IServiceScopeFactory>(),
//            registry,
//            quality,
//            TestTelemetry.LoggerFactory,
//            TestTelemetry.Calling,
//            defaultObservers: []);

//        var session = await factory.CreateAsync(new CallSessionRequest
//        {
//            Call = CreateContext("call-supervisor"),
//            CallerEdge = caller,
//            PreferredTier = AgentTier.RealtimeVoice,
//        });
//        services.GetRequiredService<ICallSessionAccessor>().Set(session);
//        var snapshots = quality.Subscribe("call-supervisor");
//        await session.StartAsync();

//        Assert.True(await session.AttachSupervisorAsync(supervisor, supervisorMode));

//        return new CallFixture
//        {
//            Caller = caller,
//            Supervisor = supervisor,
//            Session = session,
//            Strategy = strategy,
//            Quality = quality,
//            Snapshots = snapshots
//        };
//    }

//    public async Task<CallQualitySnapshot> WaitForSnapshotAsync(Func<CallQualitySnapshot, bool> predicate)
//    {
//        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
//        await foreach (var snap in Snapshots.ReadAllAsync(cts.Token))
//        {
//            if (predicate(snap))
//            {
//                return snap;
//            }
//        }
//        throw new TimeoutException("Snapshot predicate never matched");
//    }

//    public async Task WaitUntilAsync(Func<bool> predicate)
//    {
//        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
//        while (DateTimeOffset.UtcNow < deadline)
//        {
//            if (predicate())
//            {
//                return;
//            }
//            await Task.Delay(20);
//        }
//        throw new TimeoutException("Predicate never satisfied");
//    }

//    public async ValueTask DisposeAsync()
//    {
//        await Session.DisposeAsync();
//    }
//}

///// <summary>
///// Strategy double that exposes its inbound channel + lets the test push outbound
///// audio on demand. Implements <see cref="IWhisperableStrategy"/> so the
///// Whisper-mode test has something to assert against.
///// </summary>
//internal sealed class ControllableStrategy : IConversationStrategy, IWhisperableStrategy
//{
//    public Channel<AudioFrame> Inbound { get; } = Channel.CreateUnbounded<AudioFrame>();
//    private readonly Channel<OutboundDirective> _outbound = Channel.CreateUnbounded<OutboundDirective>();
//    private readonly Channel<StrategyEvent> _events = Channel.CreateUnbounded<StrategyEvent>();

//    public List<SupervisorWhisper> Whispers { get; } = [];
//    public int SuspendCount { get; private set; }
//    public int ResumeCount { get; private set; }

//    private Task? _inboundCopyLoop;
//    private CancellationTokenSource? _cts;

//    public StrategyKind Kind => StrategyKind.RealtimeVoice;

//    public AgentTier Tier => AgentTier.RealtimeVoice;

//    public IvrSnapshot WorkflowState { get; } = IvrSnapshot.Empty with { Status = IvrWorkflowStatus.Running };

//    public EdgeCapabilities EmittedDirectives => EdgeCapabilities.Audio | EdgeCapabilities.StopPlayback;

//    public ChannelReader<OutboundDirective> Outbound => _outbound.Reader;

//    public ChannelReader<StrategyEvent> Events => _events.Reader;

//    public Task StartAsync(StrategyStartContext context, CancellationToken cancellationToken = default)
//    {
//        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
//        // Copy the session-provided inbound channel into our test-visible Inbound
//        // channel so tests can read what the session forwarded.
//        _inboundCopyLoop = Task.Run(async () =>
//        {
//            try
//            {
//                await foreach (var frame in context.InboundAudio.ReadAllAsync(_cts.Token).ConfigureAwait(false))
//                {
//                    await Inbound.Writer.WriteAsync(frame, _cts.Token).ConfigureAwait(false);
//                }
//            }
//            catch (OperationCanceledException) { /* shutdown */ }
//            finally
//            {
//                Inbound.Writer.TryComplete();
//            }
//        }, CancellationToken.None);
//        return Task.CompletedTask;
//    }

//    public async Task StopAsync(CancellationToken cancellationToken = default)
//    {
//        if (_cts is not null) { try { await _cts.CancelAsync().ConfigureAwait(false); } catch { } }
//        if (_inboundCopyLoop is not null)
//        {
//            try { await _inboundCopyLoop.ConfigureAwait(false); } catch { /* shutdown */ }
//        }
//        _outbound.Writer.TryComplete();
//        _events.Writer.TryComplete();
//    }

//    public ValueTask SuspendAsync(CancellationToken cancellationToken = default)
//    {
//        SuspendCount++;
//        return ValueTask.CompletedTask;
//    }

//    public ValueTask ResumeAsync(CancellationToken cancellationToken = default)
//    {
//        ResumeCount++;
//        return ValueTask.CompletedTask;
//    }

//    public ValueTask InjectWhisperAsync(SupervisorWhisper whisper, CancellationToken cancellationToken = default)
//    {
//        Whispers.Add(whisper);
//        return ValueTask.CompletedTask;
//    }

//    public async ValueTask DisposeAsync()
//    {
//        await StopAsync().ConfigureAwait(false);
//        _cts?.Dispose();
//    }

//    public ValueTask EmitOutboundAsync(ReadOnlyMemory<byte> pcm)
//        => _outbound.Writer.WriteAsync(new OutboundDirective.Audio(
//            new AudioFrame(pcm, DateTimeOffset.UtcNow, "controllable")));
//}

using System.Threading.Channels;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Calling.Core;
using Agents.AI.ContactCenter.Calling.Strategies;
using Agents.AI.ContactCenter.Calling.Strategies.Composite;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Agents.AI.ContactCenter.State.Projections;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Tests.Calling;

public sealed class CompositeFallbackStrategyTests
{
    [Fact]
    public async Task Inner_fault_degrades_once_and_exhaustion_emits_terminal_fault()
    {
        var first = new TestStrategy(AgentTier.RealtimeVoice);
        var second = new TestStrategy(AgentTier.IntentNlu);
        await using var fixture = CreateComposite(
            [AgentTier.RealtimeVoice, AgentTier.IntentNlu],
            (AgentTier.RealtimeVoice, first),
            (AgentTier.IntentNlu, second));

        await fixture.Composite.StartAsync(CreateStartContext());
        await first.EmitFaultAsync("first failed");

        var degraded = Assert.IsType<StrategyEvent.TierDegraded>(
            await ReadUntilAsync(fixture.Composite.Events, static value => value is StrategyEvent.TierDegraded));
        Assert.Equal(AgentTier.RealtimeVoice, degraded.From);
        Assert.Equal(AgentTier.IntentNlu, degraded.To);
        Assert.Equal(1, second.StartCount);
        Assert.Equal(1, first.DisposeCount);

        await second.EmitFaultAsync("second failed");
        var terminal = Assert.IsType<StrategyEvent.Faulted>(
            await ReadUntilAsync(fixture.Composite.Events, static value => value is StrategyEvent.Faulted));
        Assert.Equal("No fallback available", terminal.Message);
    }

    [Fact]
    public async Task Fallback_start_failure_advances_to_next_tier()
    {
        var first = new TestStrategy(AgentTier.RealtimeVoice);
        var failing = new TestStrategy(AgentTier.IntentNlu)
        {
            StartException = new InvalidOperationException("start failed")
        };
        var final = new TestStrategy(AgentTier.DtmfOnly);
        await using var fixture = CreateComposite(
            [AgentTier.RealtimeVoice, AgentTier.IntentNlu, AgentTier.DtmfOnly],
            (AgentTier.RealtimeVoice, first),
            (AgentTier.IntentNlu, failing),
            (AgentTier.DtmfOnly, final));

        await fixture.Composite.StartAsync(CreateStartContext());
        await first.EmitFaultAsync("degrade");

        var degraded = Assert.IsType<StrategyEvent.TierDegraded>(
            await ReadUntilAsync(fixture.Composite.Events, static value => value is StrategyEvent.TierDegraded));
        Assert.Equal(AgentTier.RealtimeVoice, degraded.From);
        Assert.Equal(AgentTier.DtmfOnly, degraded.To);
        Assert.Equal(1, failing.StartCount);
        Assert.Equal(1, failing.DisposeCount);
        Assert.Equal(1, final.StartCount);
    }

    [Fact]
    public async Task Unexpected_inner_event_completion_triggers_degradation()
    {
        var first = new TestStrategy(AgentTier.RealtimeVoice);
        var second = new TestStrategy(AgentTier.IntentNlu);
        await using var fixture = CreateComposite(
            [AgentTier.RealtimeVoice, AgentTier.IntentNlu],
            (AgentTier.RealtimeVoice, first),
            (AgentTier.IntentNlu, second));
        await fixture.Composite.StartAsync(CreateStartContext());

        first.CompleteEvents();

        var degraded = Assert.IsType<StrategyEvent.TierDegraded>(
            await ReadUntilAsync(fixture.Composite.Events, static value => value is StrategyEvent.TierDegraded));
        Assert.Equal(AgentTier.IntentNlu, degraded.To);
        Assert.Equal(1, second.StartCount);
    }

    private static CompositeFixture CreateComposite(
        AgentTier[] tiers,
        params (AgentTier Tier, TestStrategy Strategy)[] strategies)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ICallerElevationDispatcher, CallerElevationDispatcher>();
        foreach (var (tier, strategy) in strategies)
        {
            services.AddKeyedSingleton<ILeafConversationStrategyFactory>(
                tier,
                new LeafConversationStrategyFactory(() => strategy));
        }

        var provider = services.BuildServiceProvider();
        var workflow = new WorkflowGraphCompiler().Compile(new WorkflowBlueprint
        {
            Id = "composite-test",
            InitialStageId = "start",
            Stages = [new StageBlueprint { Id = "start" }]
        });
        var session = new CallWorkflowSession(
            workflow,
            provider,
            provider.GetRequiredService<ICallerElevationDispatcher>());
        return new CompositeFixture(
            new CompositeFallbackStrategy(
                tiers,
                session,
                new CallTierAdmission(),
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()),
            provider);
    }

    private static StrategyStartContext CreateStartContext() => new()
    {
        CallId = "composite-call",
        InboundAudio = Channel.CreateUnbounded<AudioFrame>().Reader,
        InboundDtmf = Channel.CreateUnbounded<DtmfTone>().Reader,
        StateProjector = null
    };

    private static async Task<StrategyEvent> ReadUntilAsync(
        ChannelReader<StrategyEvent> reader,
        Func<StrategyEvent, bool> predicate)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var strategyEvent in reader.ReadAllAsync(cts.Token))
        {
            if (predicate(strategyEvent))
            {
                return strategyEvent;
            }
        }
        throw new InvalidOperationException("The composite event stream completed before the expected event.");
    }

    private sealed class CompositeFixture(
        CompositeFallbackStrategy composite,
        ServiceProvider services) : IAsyncDisposable
    {
        public CompositeFallbackStrategy Composite { get; } = composite;

        public async ValueTask DisposeAsync()
        {
            await Composite.DisposeAsync();
            await services.DisposeAsync();
        }
    }

    private sealed class TestStrategy(AgentTier tier) : IConversationStrategy
    {
        private readonly Channel<OutboundDirective> _outbound = Channel.CreateUnbounded<OutboundDirective>();
        private readonly Channel<StrategyEvent> _events = Channel.CreateUnbounded<StrategyEvent>();

        public StrategyKind Kind => StrategyKind.Dtmf;
        public AgentTier Tier { get; } = tier;
        public IvrSnapshot WorkflowState => IvrSnapshot.Empty;
        public EdgeCapabilities EmittedDirectives => EdgeCapabilities.None;
        public ChannelReader<OutboundDirective> Outbound => _outbound.Reader;
        public ChannelReader<StrategyEvent> Events => _events.Reader;
        public int StartCount { get; private set; }
        public int DisposeCount { get; private set; }
        public Exception? StartException { get; init; }

        public Task StartAsync(StrategyStartContext context, CancellationToken cancellationToken = default)
        {
            StartCount++;
            return StartException is null ? Task.CompletedTask : Task.FromException(StartException);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _outbound.Writer.TryComplete();
            _events.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public ValueTask SuspendAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ResumeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask EmitFaultAsync(string message)
            => _events.Writer.WriteAsync(new StrategyEvent.Faulted(message, null, DateTimeOffset.UtcNow));

        public void CompleteEvents() => _events.Writer.TryComplete();
    }
}

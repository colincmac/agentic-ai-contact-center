using System.Threading.Channels;
using global::Agents.AI.ContactCenter.Authentication;
using global::Agents.AI.ContactCenter.Calling;
using global::Agents.AI.ContactCenter.Configuration;
using global::Agents.AI.ContactCenter.State;
using global::Agents.AI.ContactCenter.State.Projections;
using global::Agents.AI.ContactCenter.State.Stores;

namespace Agents.AI.ContactCenter.Tests.Authentication;

public sealed class CallerAuthenticationRunnerTests
{
    [Fact]
    public async Task NoOrchestratorRegistered_ReturnsAnonymousAndEmitsNothing()
    {
        var services = new ServiceCollection()
            .AddSingleton(NewProjector())
            .BuildServiceProvider();
        var events = Channel.CreateUnbounded<StrategyEvent>();

        var result = await CallerAuthenticationRunner.RunAsync(
            BuildContext(services),
            services,
            events.Writer);

        Assert.Empty(result.Steps);
        Assert.Same(CallerIdentity.Anonymous, result.Identity);
        Assert.False(events.Reader.TryRead(out _));
    }

    [Fact]
    public async Task EmptyOrchestrator_ReturnsAnonymousAndEmitsNothing()
    {
        var (services, _) = BuildServices();
        var events = Channel.CreateUnbounded<StrategyEvent>();

        var result = await CallerAuthenticationRunner.RunAsync(
            BuildContext(services),
            services,
            events.Writer);

        Assert.Empty(result.Steps);
        Assert.Same(CallerIdentity.Anonymous, result.Identity);
        Assert.False(events.Reader.TryRead(out _));
    }

    [Fact]
    public async Task AniMatch_FoldsIdentityAndEmitsCallerIdentifiedThenLevelChanged()
    {
        var identity = AuthenticationOrchestratorTests.MakeIdentity("cust-1", CallerVerificationLevel.AniMatch);
        var (services, projector) = BuildServices(
            new AuthenticationOrchestratorTests.StubAuthenticator(
                "AniLookup", new AuthenticationOutcome.Authenticated(identity)));
        var (emit, sink) = NewEmit(projector);

        var result = await CallerAuthenticationRunner.RunAsync(
            BuildContext(services),
            services,
            emit);

        Assert.Equal(CallerVerificationLevel.AniMatch, projector.Get<AuthSnapshot>().Level);
        Assert.Equal("cust-1", projector.Get<AuthSnapshot>().Identity.UserId);
        Assert.Equal(CallerVerificationLevel.AniMatch, result.Identity.VerificationLevel);

        sink.Writer.Complete();
        var emitted = await DrainAsync(sink.Reader);
        Assert.Collection(emitted,
            e =>
            {
                var identified = Assert.IsType<StrategyEvent.CallerIdentified>(e);
                Assert.Equal("cust-1", identified.Identity.UserId);
                Assert.Equal("AniLookup", identified.AuthenticatorName);
            },
            e =>
            {
                var changed = Assert.IsType<StrategyEvent.CallerVerificationLevelChanged>(e);
                Assert.Equal(CallerVerificationLevel.None, changed.From);
                Assert.Equal(CallerVerificationLevel.AniMatch, changed.To);
            });
    }

    [Fact]
    public async Task Failed_EmitsCallerAuthenticationFailedAndNoLevelChanged()
    {
        var (services, projector) = BuildServices(
            new AuthenticationOrchestratorTests.StubAuthenticator(
                "AniLookup", new AuthenticationOutcome.Failed("no record")));
        var (emit, sink) = NewEmit(projector);

        await CallerAuthenticationRunner.RunAsync(
            BuildContext(services), services, emit);

        sink.Writer.Complete();
        var emitted = await DrainAsync(sink.Reader);
        var failed = Assert.Single(emitted);
        var failedEvent = Assert.IsType<StrategyEvent.CallerAuthenticationFailed>(failed);
        Assert.Equal("AniLookup", failedEvent.AuthenticatorName);
        Assert.Equal("no record", failedEvent.Reason);
        Assert.Equal(CallerVerificationLevel.None, projector.Get<AuthSnapshot>().Level);
    }

    [Fact]
    public async Task NeedsChallenge_EmitsCallerAuthenticationChallengeAndNoLevelChanged()
    {
        var challenge = new AuthenticationChallenge(
            AuthenticationMethod.SmsOtp,
            "Enter the 6-digit code",
            "ch-1",
            DateTimeOffset.UtcNow.AddMinutes(5));
        var (services, projector) = BuildServices(
            new AuthenticationOrchestratorTests.StubAuthenticator(
                "SmsOtp", new AuthenticationOutcome.NeedsChallenge(challenge)));
        var (emit, sink) = NewEmit(projector);

        await CallerAuthenticationRunner.RunAsync(
            BuildContext(services), services, emit);

        sink.Writer.Complete();
        var emitted = await DrainAsync(sink.Reader);
        var single = Assert.Single(emitted);
        var challengeEvent = Assert.IsType<StrategyEvent.CallerAuthenticationChallenge>(single);
        Assert.Same(challenge, challengeEvent.Challenge);
        var pending = projector.Get<AuthSnapshot>().PendingChallenge;
        Assert.NotNull(pending);
        Assert.Equal(challenge.ChallengeId, pending!.ChallengeId);
    }

    [Fact]
    public async Task NullEventsChannel_StillRunsOrchestrator()
    {
        var identity = AuthenticationOrchestratorTests.MakeIdentity("cust-1", CallerVerificationLevel.AniMatch);
        var (services, _) = BuildServices(
            new AuthenticationOrchestratorTests.StubAuthenticator(
                "AniLookup", new AuthenticationOutcome.Authenticated(identity)));

        var result = await CallerAuthenticationRunner.RunAsync(
            BuildContext(services), services,
            events: null);

        Assert.Equal(CallerVerificationLevel.AniMatch, result.Identity.VerificationLevel);
        Assert.Equal("cust-1", result.Identity.UserId);
    }

    [Fact]
    public async Task OrchestratorThrows_SwallowsAndReturnsCurrentIdentity()
    {
        var projector = NewProjector();
        var services = new ServiceCollection()
            .AddSingleton(projector)
            .AddSingleton<IAuthenticationOrchestrator>(_ => new ThrowingOrchestrator())
            .BuildServiceProvider();
        var events = Channel.CreateUnbounded<StrategyEvent>();

        var result = await CallerAuthenticationRunner.RunAsync(
            BuildContext(services), services,
            events.Writer);

        Assert.Empty(result.Steps);
        Assert.Same(CallerIdentity.Anonymous, result.Identity);
        Assert.Equal(CallerVerificationLevel.None, projector.Get<AuthSnapshot>().Level);
        Assert.False(events.Reader.TryRead(out _));
    }

    [Fact]
    public async Task CallerMetadata_IsForwardedIntoAuthenticationContext()
    {
        var capturing = new CapturingAuthenticator();
        var (services, _) = BuildServices(capturing);

        var metadata = new CallEdgeMetadata
        {
            DisplayName = "Jordan Reyes",
            RawIdentifier = "4:+14123236796",
        };

        await CallerAuthenticationRunner.RunAsync(
            BuildContext(services, metadata), services);

        Assert.NotNull(capturing.SeenContext);
        Assert.Same(metadata, capturing.SeenContext!.CallerMetadata);
        Assert.Equal("call-test", capturing.SeenContext.CallId);
    }

    [Fact]
    public async Task NullContext_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => CallerAuthenticationRunner.RunAsync(context: null!, services: null!));
    }

    private static CallStateProjector NewProjector() => new(
        "call-test",
        [new AuthStateProjection(), new IvrStateProjection()],
        new InMemoryCallStateStore(),
        new CallStateOptions());

    private static (IServiceProvider Services, CallStateProjector Projector) BuildServices(
        params ICallerAuthenticator[] authenticators)
    {
        var projector = NewProjector();
        var services = new ServiceCollection()
            .AddSingleton(projector)
            .AddSingleton<IAuthenticationOrchestrator>(_ => new AuthenticationOrchestrator(authenticators))
            .BuildServiceProvider();
        return (services, projector);
    }

    private static (ChannelWriter<StrategyEvent> Emit, Channel<StrategyEvent> Sink) NewEmit(CallStateProjector projector)
    {
        var sink = Channel.CreateUnbounded<StrategyEvent>();
        return (new StateFoldingChannelWriter(sink.Writer, () => projector), sink);
    }

    private static StrategyStartContext BuildContext(
        IServiceProvider services,
        CallEdgeMetadata? metadata = null) => new()
        {
            CallId = "call-test",
            InboundAudio = Channel.CreateUnbounded<AudioFrame>().Reader,
            InboundDtmf = Channel.CreateUnbounded<DtmfTone>().Reader,
            CallerMetadata = metadata,
        };

    private static async Task<List<StrategyEvent>> DrainAsync(ChannelReader<StrategyEvent> reader)
    {
        var events = new List<StrategyEvent>();
        await foreach (var evt in reader.ReadAllAsync())
        {
            events.Add(evt);
        }
        return events;
    }

    private sealed class ThrowingOrchestrator : IAuthenticationOrchestrator
    {
        public Task<AuthenticationRunResult> AuthenticateAsync(
            AuthenticationContext context,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");
    }

    private sealed class CapturingAuthenticator : ICallerAuthenticator
    {
        public AuthenticationContext? SeenContext { get; private set; }
        public string Name => "Capture";
        public Task<AuthenticationOutcome> AuthenticateAsync(AuthenticationContext context, CancellationToken cancellationToken = default)
        {
            SeenContext = context;
            return Task.FromResult<AuthenticationOutcome>(new AuthenticationOutcome.NotApplicable("captured"));
        }
    }
}

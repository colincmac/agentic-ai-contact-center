using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Exceptions;
using Agents.AI.ContactCenter.State;
using Agents.AI.ContactCenter.State.Projections;
using Agents.AI.ContactCenter.State.Stores;

namespace Agents.AI.ContactCenter.Tests.State;

public class CallStateTests
{
    private const string Call = "call-state-1";

    private static CallerIdentity Identity(
        string userId,
        CallerVerificationLevel level,
        string name = "Jane Doe",
        string? phone = "+15551234567")
        => new(
            UserId: userId,
            DisplayName: name,
            PhoneNumber: phone,
            Email: null,
            ObjectId: null,
            VerificationLevel: level,
            AuthenticatedAt: DateTimeOffset.UtcNow,
            AuthenticatedBy: "ani",
            Claims: new Dictionary<string, object?>());

    private static CallStateProjector NewProjector(ICallStateStore? store = null, params ICallStateProjection[] extra)
    {
        ICallStateProjection[] providers = [new AuthStateProjection(), new IvrStateProjection(), .. extra];
        return new CallStateProjector(Call, providers, store ?? new InMemoryCallStateStore(), new CallStateOptions());
    }

    private static CallStateProjector NewProjectorWithLog(
        InMemoryCallStateStore store,
        InMemoryCallEventLog log,
        params ICallStateProjection[] extra)
    {
        ICallStateProjection[] providers = [new AuthStateProjection(), new IvrStateProjection(), .. extra];
        return new CallStateProjector(Call, providers, store, new CallStateOptions { EnableEventLog = true }, log);
    }

    [Fact]
    public void AuthProvider_Folds_CallerIdentified_Into_Snapshot()
    {
        var provider = new AuthStateProjection();
        var bag = new CallStateBag();
        var now = DateTimeOffset.UtcNow;

        ((ICallStateProjection)provider).Fold(bag, new StrategyEvent.CallerIdentified(Identity("u-1", CallerVerificationLevel.AniMatch), "ani", now));

        var snapshot = provider.Read(bag);
        Assert.True(snapshot.IsAuthenticated);
        Assert.Equal("u-1", snapshot.UserId);
        Assert.Equal(CallerVerificationLevel.AniMatch, snapshot.Level);
        Assert.Equal("Jane Doe", snapshot.DisplayName);
        Assert.Single(snapshot.Steps);
        Assert.Equal(AuthStepOutcome.Authenticated, snapshot.Steps[0].Outcome);
    }

    [Fact]
    public void AuthProvider_StrongerIdentity_Promotes_WeakerDoesNot_Downgrade()
    {
        var provider = (ICallStateProjection)new AuthStateProjection();
        var typed = (AuthStateProjection)provider;
        var bag = new CallStateBag();
        var now = DateTimeOffset.UtcNow;

        provider.Fold(bag, new StrategyEvent.CallerIdentified(Identity("u-1", CallerVerificationLevel.MultiFactor), "otp", now));
        provider.Fold(bag, new StrategyEvent.CallerIdentified(Identity("u-weak", CallerVerificationLevel.AniMatch, name: "Weaker"), "ani", now));

        var snapshot = typed.Read(bag);
        Assert.Equal(CallerVerificationLevel.MultiFactor, snapshot.Level);
        Assert.Equal("u-1", snapshot.UserId);
        Assert.Equal("Jane Doe", snapshot.DisplayName);
        Assert.Equal(2, snapshot.Steps.Length);
    }

    [Fact]
    public void AuthProvider_NoOp_Event_Returns_Same_Reference()
    {
        var provider = (ICallStateProjection)new AuthStateProjection();
        var bag = new CallStateBag();

        provider.Fold(bag, new StrategyEvent.Transcript("caller", "hello", true, DateTimeOffset.UtcNow));

        var snapshot = ((AuthStateProjection)provider).Read(bag);
        Assert.False(snapshot.IsAuthenticated);
        Assert.Empty(snapshot.Steps);
    }

    [Fact]
    public void IvrProvider_Tracks_Steps_And_Captures_Validated_Workflow_Data()
    {
        var provider = (ICallStateProjection)new IvrStateProjection();
        var typed = (IvrStateProjection)provider;
        var bag = new CallStateBag();
        var now = DateTimeOffset.UtcNow;

        provider.Fold(bag, new StrategyEvent.WorkflowStepEntered("greeting", now));
        provider.Fold(bag, new StrategyEvent.WorkflowDataRecorded(
            new Dictionary<string, string?> { ["CallerFirstName"] = "Jane", ["CallerLastName"] = "Doe" },
            now));
        provider.Fold(bag, new StrategyEvent.WorkflowStepEntered("verify", now));

        var snapshot = typed.Read(bag);
        Assert.Equal("verify", snapshot.CurrentStepId);
        Assert.Contains("greeting", snapshot.CompletedSteps);
        Assert.Equal("Jane", snapshot.Slots["CallerFirstName"]);
        Assert.Equal("Doe", snapshot.Slots["CallerLastName"]);
        Assert.Equal(global::Agents.AI.ContactCenter.IvrWorkflow.IvrWorkflowStatus.Running, snapshot.Status);
    }

    [Fact]
    public void IvrProvider_Does_Not_Capture_Arbitrary_Function_Arguments()
    {
        var provider = (ICallStateProjection)new IvrStateProjection();
        var typed = (IvrStateProjection)provider;
        var bag = new CallStateBag();
        var now = DateTimeOffset.UtcNow;
        provider.Fold(bag, new StrategyEvent.WorkflowDataRecorded(
            new Dictionary<string, string?> { ["CallerFirstName"] = "Validated" }, now));

        provider.Fold(bag, new StrategyEvent.FunctionCalled(
            "RecordCallerName",
            new Dictionary<string, object?> { ["CallerFirstName"] = "Unvalidated", ["Pin"] = "sensitive-value" },
            "fn-1", now));

        var snapshot = typed.Read(bag);
        Assert.Single(snapshot.Slots);
        Assert.Equal("Validated", snapshot.Slots["CallerFirstName"]);
        Assert.False(snapshot.Slots.ContainsKey("Pin"));
    }

    [Fact]
    public async Task Projector_Fold_Then_Get_Returns_Typed_Slice()
    {
        var projector = NewProjector();
        await projector.HydrateAsync();

        projector.Fold(new StrategyEvent.CallerIdentified(Identity("u-9", CallerVerificationLevel.VoiceBiometric), "voice", DateTimeOffset.UtcNow));

        Assert.Equal(CallerVerificationLevel.VoiceBiometric, projector.Get<AuthSnapshot>().Level);
        Assert.Equal(global::Agents.AI.ContactCenter.IvrWorkflow.IvrWorkflowStatus.NotStarted, projector.Get<IvrSnapshot>().Status);

        await projector.DisposeAsync();
    }

    [Fact]
    public async Task Projector_Get_For_Unregistered_Slice_Throws()
    {
        var projector = NewProjector();
        await projector.HydrateAsync();

        Assert.Throws<InvalidOperationException>(() => projector.Get<UnregisteredSnapshot>());

        await projector.DisposeAsync();
    }

    [Fact]
    public async Task Projector_Persists_And_Hydrates_Across_Instances()
    {
        var store = new InMemoryCallStateStore();

        var first = NewProjector(store);
        await first.HydrateAsync();
        first.Fold(new StrategyEvent.CallerIdentified(Identity("u-restore", CallerVerificationLevel.MultiFactor), "otp", DateTimeOffset.UtcNow));
        first.Fold(new StrategyEvent.WorkflowStepEntered("greeting", DateTimeOffset.UtcNow));
        await first.FlushAsync();
        await first.DisposeAsync();

        var second = NewProjector(store);
        await second.HydrateAsync();

        Assert.Equal(CallerVerificationLevel.MultiFactor, second.Get<AuthSnapshot>().Level);
        Assert.Equal("u-restore", second.Get<AuthSnapshot>().UserId);
        Assert.Equal("greeting", second.Get<IvrSnapshot>().CurrentStepId);

        await second.DisposeAsync();
    }

    [Fact]
    public async Task Projector_Supports_Custom_Provider_Additively()
    {
        var projector = NewProjector(store: null, new TranscriptCounterProjection());
        await projector.HydrateAsync();

        projector.Fold(new StrategyEvent.Transcript("caller", "one", true, DateTimeOffset.UtcNow));
        projector.Fold(new StrategyEvent.Transcript("caller", "two", true, DateTimeOffset.UtcNow));

        Assert.Equal(2, projector.Get<CounterSnapshot>().Transcripts);

        await projector.DisposeAsync();
    }

    [Fact]
    public async Task InMemoryStore_Enforces_Optimistic_Concurrency()
    {
        var store = new InMemoryCallStateStore();
        var slices = new Dictionary<string, string> { ["auth"] = "{}" };

        var saved = await store.SaveAsync(new CallStateSnapshot { CallId = Call, Version = 0, Slices = slices });
        Assert.Equal(1, saved.Version);

        await Assert.ThrowsAsync<CallStateConcurrencyException>(async () =>
            await store.SaveAsync(new CallStateSnapshot { CallId = Call, Version = 0, Slices = slices }));

        var next = await store.SaveAsync(saved with { Slices = slices });
        Assert.Equal(2, next.Version);
    }

    [Fact]
    public async Task InMemoryStore_Load_Returns_Null_When_Absent_And_Delete_Is_Idempotent()
    {
        var store = new InMemoryCallStateStore();

        Assert.Null(await store.LoadAsync("missing"));
        await store.DeleteAsync("missing");
    }

    [Fact]
    public async Task EventLog_Enabled_Snapshot_Records_Watermark()
    {
        var store = new InMemoryCallStateStore();
        var log = new InMemoryCallEventLog();

        var projector = NewProjectorWithLog(store, log);
        await projector.HydrateAsync();

        projector.Fold(new StrategyEvent.WorkflowStepEntered("greeting", DateTimeOffset.UtcNow));
        projector.Fold(new StrategyEvent.WorkflowStepEntered("verify", DateTimeOffset.UtcNow));
        await projector.DisposeAsync(); // drains the background writer, appends both events, then flushes

        var snapshot = await store.LoadAsync(Call);
        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.EventSequence);
    }

    [Fact]
    public async Task Hydrate_Replays_EventLog_Tail_After_Watermark()
    {
        var store = new InMemoryCallStateStore();
        var log = new InMemoryCallEventLog();

        var first = NewProjectorWithLog(store, log);
        await first.HydrateAsync();
        first.Fold(new StrategyEvent.CallerIdentified(Identity("u-1", CallerVerificationLevel.MultiFactor), "otp", DateTimeOffset.UtcNow));
        first.Fold(new StrategyEvent.WorkflowStepEntered("greeting", DateTimeOffset.UtcNow));
        await first.DisposeAsync(); // snapshot persisted at EventSequence = 2

        // A later event reaches the log only (e.g. a crash before the next snapshot flush).
        await ((ICallEventLog)log).AppendAsync(Call, new StrategyEvent.WorkflowStepEntered("verify", DateTimeOffset.UtcNow));

        var second = NewProjectorWithLog(store, log);
        await second.HydrateAsync();

        Assert.Equal(CallerVerificationLevel.MultiFactor, second.Get<AuthSnapshot>().Level); // restored from snapshot
        Assert.Equal("verify", second.Get<IvrSnapshot>().CurrentStepId);                     // caught up from the replayed tail
        Assert.Contains("greeting", second.Get<IvrSnapshot>().CompletedSteps);

        await second.DisposeAsync();
    }

    [Fact]
    public async Task Hydrate_Replay_Does_Not_Reapply_Snapshotted_Events()
    {
        var store = new InMemoryCallStateStore();
        var log = new InMemoryCallEventLog();

        var first = NewProjectorWithLog(store, log, new TranscriptCounterProjection());
        await first.HydrateAsync();
        first.Fold(new StrategyEvent.Transcript("caller", "one", true, DateTimeOffset.UtcNow));
        first.Fold(new StrategyEvent.Transcript("caller", "two", true, DateTimeOffset.UtcNow));
        await first.DisposeAsync(); // snapshot: counter = 2, watermark = 2

        // One more transcript reaches the log only.
        await ((ICallEventLog)log).AppendAsync(Call, new StrategyEvent.Transcript("caller", "three", true, DateTimeOffset.UtcNow));

        var second = NewProjectorWithLog(store, log, new TranscriptCounterProjection());
        await second.HydrateAsync();

        // 2 restored from the snapshot + exactly 1 replayed (seq 3); events at/below the watermark are not re-folded.
        Assert.Equal(3, second.Get<CounterSnapshot>().Transcripts);

        await second.DisposeAsync();
    }

    [Fact]
    public async Task Hydrate_Without_EventLog_Does_Not_Replay_And_Watermark_Stays_Zero()
    {
        var store = new InMemoryCallStateStore();

        var first = NewProjector(store);
        await first.HydrateAsync();
        first.Fold(new StrategyEvent.WorkflowStepEntered("greeting", DateTimeOffset.UtcNow));
        await first.DisposeAsync();

        var snapshot = await store.LoadAsync(Call);
        Assert.NotNull(snapshot);
        Assert.Equal(0, snapshot!.EventSequence); // no event log wired -> no watermark

        var second = NewProjector(store);
        await second.HydrateAsync();
        Assert.Equal("greeting", second.Get<IvrSnapshot>().CurrentStepId); // snapshot-only restore still works

        await second.DisposeAsync();
    }

    [Fact]
    public async Task Hydrate_Replay_Failure_Is_Surfaced()
    {
        var store = new InMemoryCallStateStore();

        var seed = NewProjector(store);
        await seed.HydrateAsync();
        seed.Fold(new StrategyEvent.CallerIdentified(Identity("u-1", CallerVerificationLevel.AniMatch), "ani", DateTimeOffset.UtcNow));
        await seed.DisposeAsync();

        var projector = new CallStateProjector(
            Call,
            [new AuthStateProjection(), new IvrStateProjection()],
            store,
            new CallStateOptions { EnableEventLog = true },
            new ThrowingEventLog());

        await Assert.ThrowsAsync<InvalidOperationException>(() => projector.HydrateAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => projector.PersistenceCompletion);
        Assert.Throws<InvalidOperationException>(() =>
            projector.Fold(new StrategyEvent.Transcript("caller", "not accepted", true, DateTimeOffset.UtcNow)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => projector.DisposeAsync().AsTask());
    }

    private sealed class ThrowingEventLog : ICallEventLog
    {
        public ValueTask<long> AppendAsync(string callId, StrategyEvent strategyEvent, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(0L);

        public IAsyncEnumerable<CallEventEnvelope> ReadAsync(string callId, long afterSequence = 0, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("replay boom");
    }

    private sealed record UnregisteredSnapshot;

    private sealed record CounterSnapshot
    {
        public static readonly CounterSnapshot Empty = new();
        public int Transcripts { get; init; }
    }

    private sealed class TranscriptCounterProjection : CallStateProjection<CounterSnapshot>
    {
        public TranscriptCounterProjection() : base("counter", () => CounterSnapshot.Empty) { }

        protected override CounterSnapshot Apply(CounterSnapshot current, StrategyEvent strategyEvent)
            => strategyEvent is StrategyEvent.Transcript ? current with { Transcripts = current.Transcripts + 1 } : current;
    }
}

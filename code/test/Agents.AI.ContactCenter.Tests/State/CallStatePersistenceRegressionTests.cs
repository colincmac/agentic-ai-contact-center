using System.Text.Json;
using System.Threading.Channels;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Exceptions;
using Agents.AI.ContactCenter.State;
using Agents.AI.ContactCenter.State.Stores;

namespace Agents.AI.ContactCenter.Tests.State;

public sealed class CallStatePersistenceRegressionTests
{
    private const string Call = "ordered-call";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Snapshot_Contains_Only_The_Appended_Prefix_And_Replay_Does_Not_Double_Count()
    {
        var log = new DelayedEventLog();
        var store = new DelayedStore();
        var projection = new OrderedProjection();
        var projector = NewProjector(store, log, projection, snapshotEvery: 1);
        await projector.HydrateAsync();
        try
        {
            projector.Fold(Event("one"));
            await log.AppendStarted.Task.WaitAsync(TestTimeout);
            projector.Fold(Event("two"));

            Assert.Equal(new[] { "one", "two" }, projector.Get<OrderedState>().Events);
            Assert.Equal(0, projection.SerializationCount);
            var flush = projector.FlushAsync();
            Assert.False(flush.IsCompleted);

            log.ReleaseAppend.TrySetResult();
            var prefix = await store.FirstSnapshot.Task.WaitAsync(TestTimeout);
            Assert.Equal(1, prefix.EventSequence);
            Assert.Equal(new[] { "one" }, ReadState(prefix).Events);
            Assert.False(flush.IsCompleted);

            store.ReleaseSave.TrySetResult();
            await flush.WaitAsync(TestTimeout);
            var complete = await store.Inner.LoadAsync(Call);
            Assert.NotNull(complete);
            Assert.Equal(2, complete.EventSequence);
            Assert.Equal(new[] { "one", "two" }, ReadState(complete).Events);

            // Simulate recovery from the first snapshot with the subsequent append-only log tail.
            var recoveryStore = new InMemoryCallStateStore();
            await recoveryStore.SaveAsync(prefix with { Version = 0 });
            await using var resumed = NewProjector(recoveryStore, log, new OrderedProjection());
            await resumed.HydrateAsync();
            Assert.Equal(new[] { "one", "two" }, resumed.Get<OrderedState>().Events);
        }
        finally
        {
            log.ReleaseAppend.TrySetResult();
            store.ReleaseSave.TrySetResult();
            await projector.DisposeAsync();
        }
    }

    [Fact]
    public async Task Concurrent_Folds_Keep_The_Same_Order_In_Live_State_Log_And_Snapshot()
    {
        var log = new DelayedEventLog();
        var store = new InMemoryCallStateStore();
        var projection = new OrderedProjection();
        var projector = NewProjector(store, log, projection);
        await projector.HydrateAsync();
        try
        {
            projector.Fold(Event("first"));
            await log.AppendStarted.Task.WaitAsync(TestTimeout);
            using var startEachRound = new Barrier(8);
            var writers = Enumerable.Range(0, 8).Select(writer => Task.Factory.StartNew(() =>
            {
                for (var round = 0; round < 32; round++)
                {
                    Assert.True(startEachRound.SignalAndWait(TestTimeout));
                    projector.Fold(Event($"{writer}:{round}"));
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            await Task.WhenAll(writers).WaitAsync(TestTimeout);

            var live = projector.Get<OrderedState>().Events;
            Assert.Equal(257, live.Length);
            Assert.Equal(257, live.Distinct().Count());
            Assert.Equal(0, projection.SerializationCount);
            log.ReleaseAppend.TrySetResult();
            await projector.FlushAsync().WaitAsync(TestTimeout);

            var replayOrder = new List<string>();
            await foreach (var envelope in log.ReadAsync(Call))
            {
                replayOrder.Add(((StrategyEvent.Transcript)envelope.Event).Text);
            }
            Assert.Equal(live, replayOrder);
            var snapshot = await store.LoadAsync(Call);
            Assert.NotNull(snapshot);
            Assert.Equal(257, snapshot.EventSequence);
            Assert.Equal(live, ReadState(snapshot).Events);
        }
        finally
        {
            log.ReleaseAppend.TrySetResult();
            await projector.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoOp_Media_Events_Do_Not_Reserialize_Unchanged_Slices(bool enableLog)
    {
        var projection = new OrderedProjection();
        var store = new InMemoryCallStateStore();
        await using var projector = NewProjector(store, enableLog ? new InMemoryCallEventLog() : null, projection);
        await projector.HydrateAsync();
        projector.Fold(Event("seed"));
        await projector.FlushAsync().WaitAsync(TestTimeout);
        Assert.Equal(1, projection.SerializationCount);
        for (var i = 0; i < 3; i++)
        {
            projector.Fold(new StrategyEvent.AudioPlayed($"audio-{i}", DateTimeOffset.UnixEpoch));
            await projector.FlushAsync().WaitAsync(TestTimeout);
        }

        Assert.Equal(1, projection.SerializationCount);
        var snapshot = await store.LoadAsync(Call);
        Assert.NotNull(snapshot);
        Assert.Equal(enableLog ? 4 : 0, snapshot.EventSequence);
        Assert.Equal(new[] { "seed" }, ReadState(snapshot).Events);
    }

    [Fact]
    public async Task Overflow_Rejects_Before_Folding_And_Surfaces_A_Terminal_Failure()
    {
        var store = new InMemoryCallStateStore();
        var log = new DelayedEventLog();
        var projector = NewProjector(store, log, new OrderedProjection(), capacity: 1);
        await projector.HydrateAsync();
        projector.Fold(Event("in-flight"));
        await log.AppendStarted.Task.WaitAsync(TestTimeout);
        projector.Fold(Event("queued"));

        var error = Assert.Throws<InvalidOperationException>(() => projector.Fold(Event("rejected")));
        Assert.Contains("queue is full", error.Message);
        Assert.Equal(new[] { "in-flight", "queued" }, projector.Get<OrderedState>().Events);
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => projector.PersistenceCompletion));
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => projector.FlushAsync()));
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => projector.Fold(Event("also rejected"))));

        log.ReleaseAppend.TrySetResult();
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => projector.DisposeAsync().AsTask()));
        var saved = await store.LoadAsync(Call);
        Assert.NotNull(saved);
        Assert.Equal(2, saved.EventSequence);
        Assert.Equal(new[] { "in-flight", "queued" }, ReadState(saved).Events);
    }

    [Fact]
    public async Task Append_Failure_Faults_Queued_Flush_And_Does_Not_Advance_Snapshot()
    {
        var failure = new IOException("append failed");
        var log = new DelayedEventLog { AppendFailure = failure };
        var store = new InMemoryCallStateStore();
        var projector = NewProjector(store, log, new OrderedProjection());
        await projector.HydrateAsync();
        projector.Fold(Event("one"));
        await log.AppendStarted.Task.WaitAsync(TestTimeout);
        var flush = projector.FlushAsync();
        log.ReleaseAppend.TrySetResult();

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => flush.WaitAsync(TestTimeout)));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => projector.PersistenceCompletion));
        Assert.Same(failure, Assert.Throws<IOException>(() => projector.Fold(Event("two"))));
        Assert.Null(await store.LoadAsync(Call));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => projector.DisposeAsync().AsTask()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_Failure_Or_Conflict_Is_Not_A_Successful_Flush(bool concurrencyConflict)
    {
        Exception failure = concurrencyConflict
            ? new CallStateConcurrencyException(Call, 0, 1)
            : new IOException("save failed");
        var store = new DelayedStore { SaveFailure = failure };
        var projector = NewProjector(store, null, new OrderedProjection());
        await projector.HydrateAsync();
        projector.Fold(Event("one"));
        await store.FirstSnapshot.Task.WaitAsync(TestTimeout);
        var flush = projector.FlushAsync();
        store.ReleaseSave.TrySetResult();

        Assert.Same(failure, await Record.ExceptionAsync(() => flush.WaitAsync(TestTimeout)));
        Assert.Same(failure, await Record.ExceptionAsync(() => projector.PersistenceCompletion));
        Assert.Same(failure, await Record.ExceptionAsync(() => projector.DisposeAsync().AsTask()));
        Assert.Null(await store.Inner.LoadAsync(Call));
        Assert.Equal(1, store.SaveAttempts);
        Assert.Equal(1, store.LoadAttempts); // No version-only reload/overwrite after losing ownership.
    }

    [Fact]
    public async Task Full_Queue_Flush_Can_Be_Cancelled_Without_Dropping_Admitted_Events()
    {
        var log = new DelayedEventLog();
        var store = new InMemoryCallStateStore();
        var projector = NewProjector(store, log, new OrderedProjection(), capacity: 1);
        await projector.HydrateAsync();
        try
        {
            projector.Fold(Event("one"));
            await log.AppendStarted.Task.WaitAsync(TestTimeout);
            projector.Fold(Event("two"));
            using var cancellation = new CancellationTokenSource();
            var flush = projector.FlushAsync(cancellation.Token);
            Assert.False(flush.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flush.WaitAsync(TestTimeout));

            log.ReleaseAppend.TrySetResult();
            await projector.FlushAsync().WaitAsync(TestTimeout);
            Assert.False(projector.PersistenceCompletion.IsFaulted);
            var snapshot = await store.LoadAsync(Call);
            Assert.NotNull(snapshot);
            Assert.Equal(new[] { "one", "two" }, ReadState(snapshot).Events);
        }
        finally
        {
            log.ReleaseAppend.TrySetResult();
            await projector.DisposeAsync();
        }
    }

    [Fact]
    public async Task Disposal_Is_Bounded_And_Cancels_An_Uncooperative_Store()
    {
        var store = new DelayedStore { IgnoreCancellation = true };
        var projector = NewProjector(store, null, new OrderedProjection(), shutdownTimeout: TimeSpan.FromMilliseconds(100));
        await projector.HydrateAsync();
        projector.Fold(Event("one"));
        await store.FirstSnapshot.Task.WaitAsync(TestTimeout);
        var flush = projector.FlushAsync();
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => projector.DisposeAsync().AsTask().WaitAsync(TestTimeout));
            await store.CancellationObserved.Task.WaitAsync(TestTimeout);
            await Assert.ThrowsAsync<TimeoutException>(() => projector.PersistenceCompletion);
            await Assert.ThrowsAsync<TimeoutException>(() => flush.WaitAsync(TestTimeout));
        }
        finally
        {
            store.ReleaseSave.TrySetResult();
            await store.SaveExited.Task.WaitAsync(TestTimeout);
        }
    }

    [Fact]
    public async Task Hydration_Failure_Is_Not_An_Initial_State_Fallback()
    {
        var failure = new IOException("load failed");
        var store = new DelayedStore { LoadFailure = failure };
        var projector = NewProjector(store, null, new OrderedProjection());
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => projector.HydrateAsync()));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => projector.FlushAsync()));
        Assert.Same(failure, Assert.Throws<IOException>(() => projector.Fold(Event("not accepted"))));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => projector.DisposeAsync().AsTask()));
    }

    [Fact]
    public async Task Disposal_Cancels_Pending_Hydration_Without_Starting_A_Persister()
    {
        var store = new DelayedStore { DelayLoad = true };
        var projector = NewProjector(store, null, new OrderedProjection(), shutdownTimeout: TimeSpan.FromMilliseconds(100));
        var hydration = projector.HydrateAsync();
        await store.LoadStarted.Task.WaitAsync(TestTimeout);

        await Assert.ThrowsAsync<TimeoutException>(() => projector.DisposeAsync().AsTask().WaitAsync(TestTimeout));
        await Assert.ThrowsAsync<TimeoutException>(() => hydration.WaitAsync(TestTimeout));
        await Assert.ThrowsAsync<TimeoutException>(() => projector.PersistenceCompletion);
        Assert.Equal(0, store.SaveAttempts);
    }

    [Fact]
    public async Task Corrupt_Slice_Restore_Is_Not_A_Successful_Hydration()
    {
        var store = new InMemoryCallStateStore();
        await store.SaveAsync(new CallStateSnapshot
        {
            CallId = Call,
            Slices = new Dictionary<string, string> { ["ordered"] = "not-json" },
        });
        var projector = NewProjector(store, null, new OrderedProjection());
        await Assert.ThrowsAsync<JsonException>(() => projector.HydrateAsync());
        await Assert.ThrowsAsync<JsonException>(() => projector.PersistenceCompletion);
        await Assert.ThrowsAsync<JsonException>(() => projector.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task PreHydration_Folds_Are_Rebased_And_Disposal_Persists_Them()
    {
        var store = new InMemoryCallStateStore();
        var seed = NewProjector(store, null, new OrderedProjection());
        seed.Fold(Event("seed"));
        await seed.DisposeAsync();

        var projector = NewProjector(store, null, new OrderedProjection());
        projector.Fold(Event("buffered"));
        Assert.Equal(new[] { "buffered" }, projector.Get<OrderedState>().Events);
        await projector.HydrateAsync();
        Assert.Equal(new[] { "seed", "buffered" }, projector.Get<OrderedState>().Events);
        await projector.DisposeAsync();
        await projector.DisposeAsync(); // Shared singleton/DI disposal remains idempotent.
        var snapshot = await store.LoadAsync(Call);
        Assert.NotNull(snapshot);
        Assert.Equal(0, snapshot.EventSequence);
        Assert.Equal(new[] { "seed", "buffered" }, ReadState(snapshot).Events);
        Assert.Throws<ObjectDisposedException>(() => projector.Fold(Event("late")));
    }

    [Fact]
    public async Task Folding_Channel_Writer_Does_Not_Forward_An_Overflowed_Event()
    {
        var projector = NewProjector(new InMemoryCallStateStore(), null, new OrderedProjection(), capacity: 1);
        var downstream = Channel.CreateUnbounded<StrategyEvent>();
        var writer = new StateFoldingChannelWriter(downstream.Writer, () => projector);
        Assert.True(writer.TryWrite(Event("one")));
        Assert.Throws<InvalidOperationException>(() => writer.TryWrite(Event("two")));
        Assert.True(downstream.Reader.TryRead(out var accepted));
        Assert.Equal("one", ((StrategyEvent.Transcript)accepted!).Text);
        Assert.False(downstream.Reader.TryRead(out _));
        await Assert.ThrowsAsync<InvalidOperationException>(() => projector.DisposeAsync().AsTask());
    }

    [Theory]
    [InlineData(0, 25, 1000)]
    [InlineData(-1, 25, 1000)]
    [InlineData(1, 0, 1000)]
    [InlineData(1, 25, 0)]
    [InlineData(1, 25, -1)]
    public void Invalid_Persistence_Limits_Are_Rejected(int capacity, int snapshotEvery, int shutdownMilliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NewProjector(
            new InMemoryCallStateStore(), null, new OrderedProjection(), capacity, snapshotEvery,
            TimeSpan.FromMilliseconds(shutdownMilliseconds)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("customer-1")]
    public async Task Workflow_Selection_And_Subject_Evidence_Survive_Snapshot_And_Replay(string? subjectId)
    {
        var store = new InMemoryCallStateStore();
        var log = new InMemoryCallEventLog();
        var selected = new StrategyEvent.WorkflowSelected("billing", 3, DateTimeOffset.UnixEpoch);
        var attempted = new StrategyEvent.CredentialAttempted("Pin", true, null, DateTimeOffset.UnixEpoch.AddMinutes(1), subjectId);
        var first = NewProjector(store, log, new EventMetadataProjection());
        await first.HydrateAsync();
        first.Fold(selected);
        first.Fold(attempted);
        Assert.Equal(selected, first.Get<EventMetadataState>().Selection);
        Assert.Equal(attempted, first.Get<EventMetadataState>().Attempt);
        await first.DisposeAsync();

        var logged = new List<StrategyEvent>();
        await foreach (var envelope in log.ReadAsync(Call))
        {
            logged.Add(envelope.Event);
        }
        Assert.Equal(new StrategyEvent[] { selected, attempted }, logged);

        var restored = NewProjector(store, log, new EventMetadataProjection());
        await restored.HydrateAsync();
        Assert.Equal(selected, restored.Get<EventMetadataState>().Selection);
        Assert.Equal(attempted, restored.Get<EventMetadataState>().Attempt);
        await restored.DisposeAsync();

        var tail = new StrategyEvent.CredentialAttempted("Otp", false, "rejected", DateTimeOffset.UnixEpoch.AddMinutes(2), "customer-2");
        await log.AppendAsync(Call, tail);
        await using var replayed = NewProjector(store, log, new EventMetadataProjection());
        await replayed.HydrateAsync();
        Assert.Equal(selected, replayed.Get<EventMetadataState>().Selection);
        Assert.Equal(tail, replayed.Get<EventMetadataState>().Attempt);
    }

    private static CallStateProjector NewProjector(
        ICallStateStore store,
        ICallEventLog? log,
        ICallStateProjection projection,
        int capacity = 1024,
        int snapshotEvery = 25,
        TimeSpan? shutdownTimeout = null)
        => new(Call, [projection], store, new CallStateOptions
        {
            PersistenceQueueCapacity = capacity,
            SnapshotEveryNEvents = snapshotEvery,
            ShutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(2),
        }, log);

    private static StrategyEvent.Transcript Event(string text)
        => new("caller", text, true, DateTimeOffset.UnixEpoch);

    private static OrderedState ReadState(CallStateSnapshot snapshot)
        => JsonSerializer.Deserialize<OrderedState>(snapshot.Slices["ordered"], JsonSerializerOptions.Web)!;

    public sealed record OrderedState
    {
        public string[] Events { get; init; } = [];
    }

    private sealed class OrderedProjection : ICallStateProjection
    {
        private readonly ICallStateProjection _inner = new Reducer();
        private int _serializationCount;
        public int SerializationCount => Volatile.Read(ref _serializationCount);
        public string SliceId => _inner.SliceId;
        public Type SnapshotType => _inner.SnapshotType;
        public object ReadBoxed(CallStateBag bag) => _inner.ReadBoxed(bag);
        public void Fold(CallStateBag bag, StrategyEvent strategyEvent) => _inner.Fold(bag, strategyEvent);
        public string? Render(CallStateBag bag) => _inner.Render(bag);
        public void Restore(CallStateBag bag, string json) => _inner.Restore(bag, json);
        public string Serialize(CallStateBag bag)
        {
            Interlocked.Increment(ref _serializationCount);
            return _inner.Serialize(bag);
        }

        private sealed class Reducer() : CallStateProjection<OrderedState>("ordered", () => new())
        {
            protected override OrderedState Apply(OrderedState current, StrategyEvent strategyEvent)
                => strategyEvent is StrategyEvent.Transcript transcript
                    ? current with { Events = [.. current.Events, transcript.Text] }
                    : current;
        }
    }

    public sealed record EventMetadataState
    {
        public StrategyEvent.WorkflowSelected? Selection { get; init; }
        public StrategyEvent.CredentialAttempted? Attempt { get; init; }
    }

    private sealed class EventMetadataProjection() : CallStateProjection<EventMetadataState>("event-metadata", () => new())
    {
        protected override EventMetadataState Apply(EventMetadataState current, StrategyEvent strategyEvent)
            => strategyEvent switch
            {
                StrategyEvent.WorkflowSelected selected => current with { Selection = selected },
                StrategyEvent.CredentialAttempted attempted => current with { Attempt = attempted },
                _ => current,
            };
    }

    private sealed class DelayedEventLog : ICallEventLog
    {
        private readonly InMemoryCallEventLog _inner = new();
        public TaskCompletionSource AppendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseAppend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? AppendFailure { get; init; }

        public async ValueTask<long> AppendAsync(string callId, StrategyEvent strategyEvent, CancellationToken cancellationToken = default)
        {
            AppendStarted.TrySetResult();
            await ReleaseAppend.Task.WaitAsync(cancellationToken);
            if (AppendFailure is not null)
            {
                throw AppendFailure;
            }
            return await _inner.AppendAsync(callId, strategyEvent, cancellationToken);
        }

        public IAsyncEnumerable<CallEventEnvelope> ReadAsync(string callId, long afterSequence = 0, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(callId, afterSequence, cancellationToken);
    }

    private sealed class DelayedStore : ICallStateStore
    {
        public InMemoryCallStateStore Inner { get; } = new();
        public TaskCompletionSource<CallStateSnapshot> FirstSnapshot { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SaveExited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseLoad { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? LoadFailure { get; init; }
        public Exception? SaveFailure { get; init; }
        public bool IgnoreCancellation { get; init; }
        public bool DelayLoad { get; init; }
        public int SaveAttempts { get; private set; }
        public int LoadAttempts { get; private set; }

        public async ValueTask<CallStateSnapshot> SaveAsync(CallStateSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            using var registration = cancellationToken.Register(() => CancellationObserved.TrySetResult());
            FirstSnapshot.TrySetResult(snapshot);
            try
            {
                await ReleaseSave.Task.WaitAsync(IgnoreCancellation ? CancellationToken.None : cancellationToken);
                if (SaveFailure is not null)
                {
                    throw SaveFailure;
                }
                return await Inner.SaveAsync(snapshot, cancellationToken);
            }
            finally
            {
                SaveExited.TrySetResult();
            }
        }

        public async ValueTask<CallStateSnapshot?> LoadAsync(string callId, CancellationToken cancellationToken = default)
        {
            LoadAttempts++;
            LoadStarted.TrySetResult();
            if (DelayLoad)
            {
                await ReleaseLoad.Task.WaitAsync(cancellationToken);
            }
            if (LoadFailure is not null)
            {
                throw LoadFailure;
            }
            return await Inner.LoadAsync(callId, cancellationToken);
        }

        public ValueTask DeleteAsync(string callId, CancellationToken cancellationToken = default)
            => Inner.DeleteAsync(callId, cancellationToken);
    }
}

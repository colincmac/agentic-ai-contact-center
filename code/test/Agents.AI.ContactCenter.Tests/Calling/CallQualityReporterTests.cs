using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Calling.Core;
using Agents.AI.ContactCenter.Configuration;

namespace Agents.AI.ContactCenter.Tests.Calling;

public sealed class CallQualityReporterTests
{
    [Fact]
    public void Stale_registration_cannot_update_or_remove_replacement()
    {
        var reporter = new InMemoryCallQualityReporter(TestTelemetry.LoggerFactory, TestTelemetry.Calling);
        using var stale = reporter.Register(CreateSnapshot(CallSessionState.Created));
        using var current = reporter.Register(CreateSnapshot(CallSessionState.Active));

        stale.Update(snapshot => snapshot with { LatestCallerUtterance = "stale" });
        stale.Dispose();

        var snapshot = Assert.IsType<CallQualitySnapshot>(reporter.TryGetSnapshot(CallId));
        Assert.Equal(CallSessionState.Active, snapshot.State);
        Assert.Null(snapshot.LatestCallerUtterance);

        current.Update(value => value with { LatestCallerUtterance = "current" });
        Assert.Equal("current", reporter.TryGetSnapshot(CallId)?.LatestCallerUtterance);
    }

    [Fact]
    public async Task Concurrent_updates_compose_without_lost_mutations()
    {
        var reporter = new InMemoryCallQualityReporter(TestTelemetry.LoggerFactory, TestTelemetry.Calling);
        using var registration = reporter.Register(CreateSnapshot(CallSessionState.Active));

        var updates = Enumerable.Range(0, 100)
            .Select(index => Task.Run(() => registration.Update(snapshot => snapshot with
            {
                DelegateTasks = [.. snapshot.DelegateTasks, index.ToString()]
            })));
        await Task.WhenAll(updates);

        var snapshot = Assert.IsType<CallQualitySnapshot>(reporter.TryGetSnapshot(CallId));
        Assert.Equal(100, snapshot.DelegateTasks.Count);
        Assert.Equal(100, snapshot.DelegateTasks.Distinct().Count());
    }

    [Fact]
    public void Mutation_cannot_change_call_identity()
    {
        var reporter = new InMemoryCallQualityReporter(TestTelemetry.LoggerFactory, TestTelemetry.Calling);
        using var registration = reporter.Register(CreateSnapshot(CallSessionState.Active));

        Assert.Throws<InvalidOperationException>(() => registration.Update(snapshot => snapshot with
        {
            CallId = "different-call"
        }));

        Assert.NotNull(reporter.TryGetSnapshot(CallId));
        Assert.Null(reporter.TryGetSnapshot("different-call"));
    }

    [Fact]
    public void Subscription_is_bounded_and_disposal_completes_it()
    {
        var reporter = new InMemoryCallQualityReporter(TestTelemetry.LoggerFactory, TestTelemetry.Calling);
        using var registration = reporter.Register(CreateSnapshot(CallSessionState.Active));
        var subscription = reporter.Subscribe(CallId);

        for (var index = 0; index < 100; index++)
        {
            registration.Update(snapshot => snapshot with { LatestCallerUtterance = index.ToString() });
        }

        var received = new List<CallQualitySnapshot>();
        while (subscription.Reader.TryRead(out var snapshot))
        {
            received.Add(snapshot);
        }
        Assert.InRange(received.Count, 1, 32);
        Assert.Equal("99", received[^1].LatestCallerUtterance);

        subscription.Dispose();
        Assert.True(subscription.Reader.Completion.IsCompletedSuccessfully);
    }

    private const string CallId = "quality-call";

    private static CallQualitySnapshot CreateSnapshot(CallSessionState state) => new()
    {
        CallId = CallId,
        State = state,
        ActiveTier = AgentTier.RealtimeVoice,
        StrategyKind = StrategyKind.RealtimeVoice,
        StartedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
}

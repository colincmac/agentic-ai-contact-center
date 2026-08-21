using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Coordination;
using Agents.AI.ContactCenter.Coordination.Core;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.Tests.Coordination;

public sealed class IncomingCallAdmissionControllerTests
{
    [Fact]
    public async Task Concurrent_acquire_has_exactly_one_winner_per_server_call()
    {
        var (controller, _) = CreateController();
        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 64)
            .Select(index => Task.Run(async () =>
            {
                gate.Wait();
                return await controller.TryAcquireAsync(ServerCallId, $"event-{index}");
            }))
            .ToArray();

        gate.Set();
        var results = await Task.WhenAll(tasks);

        Assert.Single(results, static result => result.Acquired);
        Assert.All(results, result => Assert.Equal(ServerCallId, result.Admission.ServerCallId));
    }

    [Fact]
    public async Task Expired_claim_can_be_reacquired_but_expired_answering_requires_recovery()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var (controller, identity) = CreateController(time);
        Assert.True((await controller.TryAcquireAsync(ServerCallId, "event-1")).Acquired);

        time.Advance(TimeSpan.FromMinutes(3));
        identity.InstanceId = Guid.NewGuid().ToString("N");
        Assert.True((await controller.TryAcquireAsync(ServerCallId, "event-2")).Acquired);
        Assert.True(await controller.MarkAnsweringAsync(ServerCallId));

        time.Advance(TimeSpan.FromMinutes(3));
        identity.InstanceId = Guid.NewGuid().ToString("N");
        var result = await controller.TryAcquireAsync(ServerCallId, "event-3");

        Assert.Equal(IncomingCallAdmissionOutcome.RecoveryRequired, result.Outcome);
        Assert.False(result.Acquired);
    }

    [Fact]
    public async Task Only_owner_can_transition_and_answered_delivery_is_rejected()
    {
        var (controller, identity) = CreateController();
        Assert.True((await controller.TryAcquireAsync(ServerCallId, "event-1")).Acquired);

        var ownerInstance = identity.InstanceId;
        identity.InstanceId = Guid.NewGuid().ToString("N");
        Assert.False(await controller.MarkAnsweringAsync(ServerCallId));

        identity.InstanceId = ownerInstance;
        Assert.True(await controller.MarkAnsweringAsync(ServerCallId));
        Assert.True(await controller.MarkAnsweredAsync(ServerCallId, "connection-1"));

        var duplicate = await controller.TryAcquireAsync(ServerCallId, "event-2");
        Assert.Equal(IncomingCallAdmissionOutcome.AlreadyAnswered, duplicate.Outcome);
        Assert.Equal("connection-1", duplicate.Admission.CallConnectionId);
    }

    [Fact]
    public async Task Retryable_failure_allows_new_owner_to_acquire()
    {
        var (controller, identity) = CreateController();
        Assert.True((await controller.TryAcquireAsync(ServerCallId, "event-1")).Acquired);
        Assert.True(await controller.MarkAnsweringAsync(ServerCallId));
        Assert.True(await controller.MarkAnswerFailedAsync(ServerCallId, retryable: true));

        identity.InstanceId = Guid.NewGuid().ToString("N");
        var retry = await controller.TryAcquireAsync(ServerCallId, "event-2");

        Assert.True(retry.Acquired);
        Assert.Equal("event-2", retry.Admission.EventGridEventId);
    }

    [Fact]
    public async Task Pre_answer_failure_can_be_marked_retryable()
    {
        var (controller, identity) = CreateController();
        Assert.True((await controller.TryAcquireAsync(ServerCallId, "event-1")).Acquired);
        Assert.True(await controller.MarkAnswerFailedAsync(ServerCallId, retryable: true));

        identity.InstanceId = Guid.NewGuid().ToString("N");
        Assert.True((await controller.TryAcquireAsync(ServerCallId, "event-2")).Acquired);
    }

    [Fact]
    public void Admission_key_is_hash_tagged_by_server_call_id()
    {
        Assert.Equal("incoming:{server-call}", CoordinationRedisKeys.IncomingCallAdmission("server-call"));
    }

    private const string ServerCallId = "server-call";

    private static (InMemoryIncomingCallAdmissionController Controller, MutableClusterIdentity Identity) CreateController(
        TimeProvider? timeProvider = null)
    {
        var identity = new MutableClusterIdentity();
        var options = Options.Create(new HyperscaleOptions
        {
            IncomingCallAdmission = new IncomingCallAdmissionOptions
            {
                LeaseDuration = TimeSpan.FromMinutes(2),
                RecordLifetime = TimeSpan.FromHours(8)
            }
        });
        return (new InMemoryIncomingCallAdmissionController(
            identity,
            options,
            timeProvider ?? TimeProvider.System), identity);
    }

    private sealed class MutableClusterIdentity : IClusterIdentity
    {
        public string ClusterId { get; set; } = "cluster-1";
        public string PodId { get; set; } = "pod-1";
        public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan value) => _now += value;
    }
}

using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Coordination;
using Agents.AI.ContactCenter.Coordination.Core;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.Tests.Coordination;

public class CallOwnershipDirectoryTests
{
    private const string Call = "call-abc";

    [Fact]
    public async Task TryAcquire_Returns_True_For_Unowned_Call()
    {
        var (store, identity) = CreateStore();

        var result = await store.TryAcquireAsync(Call, CallOwnershipKind.Streaming);

        Assert.True(result.Acquired);
        Assert.Equal(identity.InstanceId, result.Owner.InstanceId);
        Assert.Equal(CallOwnershipKind.Streaming, result.Owner.Kind);
    }

    [Fact]
    public async Task TryAcquire_Returns_False_With_Existing_Owner_When_Owned()
    {
        var (store, identity) = CreateStore();

        var first = await store.TryAcquireAsync(Call, CallOwnershipKind.Streaming);
        Assert.True(first.Acquired);

        identity.InstanceId = NewInstanceId();
        identity.PodId = "pod-2";

        var second = await store.TryAcquireAsync(Call, CallOwnershipKind.Streaming);

        Assert.False(second.Acquired);
        Assert.Equal(first.Owner.InstanceId, second.Owner.InstanceId);
        Assert.Equal("pod-1", second.Owner.PodId);
    }

    [Fact]
    public async Task TryAcquire_Takes_Over_Expired_Lease()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var (store, identity) = CreateStore(timeProvider: time);

        Assert.True((await store.TryAcquireAsync(Call, CallOwnershipKind.Verb)).Acquired);

        time.Advance(TimeSpan.FromSeconds(91));
        identity.InstanceId = NewInstanceId();

        var taken = await store.TryAcquireAsync(Call, CallOwnershipKind.Verb);

        Assert.True(taken.Acquired);
        Assert.Equal(identity.InstanceId, taken.Owner.InstanceId);
    }

    [Fact]
    public async Task GetOwner_Returns_Null_For_Unowned_Call()
    {
        var (store, _) = CreateStore();

        var owner = await store.GetOwnerAsync(Call);

        Assert.Null(owner);
    }

    [Fact]
    public async Task GetOwner_Returns_Owner_For_Owned_Call()
    {
        var (store, identity) = CreateStore();

        await store.TryAcquireAsync(Call, CallOwnershipKind.Verb);
        var owner = await store.GetOwnerAsync(Call);

        Assert.NotNull(owner);
        Assert.Equal(identity.InstanceId, owner!.InstanceId);
        Assert.Equal(identity.ClusterId, owner.ClusterId);
        Assert.Equal(CallOwnershipKind.Verb, owner.Kind);
    }

    [Fact]
    public async Task GetOwner_Returns_Null_When_Lease_Expired()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var (store, _) = CreateStore(timeProvider: time);

        await store.TryAcquireAsync(Call, CallOwnershipKind.Verb);
        time.Advance(TimeSpan.FromSeconds(91));

        Assert.Null(await store.GetOwnerAsync(Call));
    }

    [Fact]
    public async Task Renew_Extends_Lease_For_Local_Owner()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var (store, _) = CreateStore(timeProvider: time);

        await store.TryAcquireAsync(Call, CallOwnershipKind.Streaming);

        time.Advance(TimeSpan.FromSeconds(60));
        Assert.True(await store.RenewAsync(Call, CallOwnershipKind.Streaming));

        time.Advance(TimeSpan.FromSeconds(60)); // 120 s past acquire, but 60 s past renew
        Assert.NotNull(await store.GetOwnerAsync(Call));
    }

    [Fact]
    public async Task Renew_Returns_False_When_Owned_By_Different_Instance()
    {
        var (store, identity) = CreateStore();

        await store.TryAcquireAsync(Call, CallOwnershipKind.Verb);
        identity.InstanceId = NewInstanceId();

        Assert.False(await store.RenewAsync(Call, CallOwnershipKind.Verb));
    }

    [Fact]
    public async Task Renew_Returns_False_When_No_Owner()
    {
        var (store, _) = CreateStore();

        Assert.False(await store.RenewAsync(Call, CallOwnershipKind.Verb));
    }

    [Fact]
    public async Task Release_Removes_Ownership_For_Local_Owner()
    {
        var (store, _) = CreateStore();

        await store.TryAcquireAsync(Call, CallOwnershipKind.Verb);

        Assert.True(await store.ReleaseAsync(Call));
        Assert.Null(await store.GetOwnerAsync(Call));
    }

    [Fact]
    public async Task Release_Returns_False_When_Owned_By_Different_Instance()
    {
        var (store, identity) = CreateStore();

        await store.TryAcquireAsync(Call, CallOwnershipKind.Verb);
        identity.InstanceId = NewInstanceId();

        Assert.False(await store.ReleaseAsync(Call));
    }

    [Fact]
    public async Task Concurrent_TryAcquire_On_Same_Call_Yields_Exactly_One_Winner()
    {
        var (store, _) = CreateStore();
        const int parallelism = 64;

        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, parallelism)
            .Select(_ => Task.Run(async () =>
            {
                gate.Wait();
                var result = await store.TryAcquireAsync(Call, CallOwnershipKind.Streaming);
                return result.Acquired;
            }))
            .ToArray();

        gate.Set();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(acquired => acquired));
    }

    [Fact]
    public async Task Cancellation_Token_Is_Honored_Before_Mutation()
    {
        var (store, _) = CreateStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            store.TryAcquireAsync(Call, CallOwnershipKind.Verb, cts.Token));
    }

    [Fact]
    public void Ownership_Key_Uses_Hash_Tag_For_Call_Id()
    {
        var key = CoordinationRedisKeys.Ownership("call-abc-123");

        Assert.Equal("owner:{call-abc-123}", key);
    }

    [Fact]
    public void Codec_Round_Trip_Preserves_All_Fields()
    {
        var owner = new CallOwnership(
            ClusterId: "eastus2-aks-01",
            PodId: "voice-agent-7c9bd8-xkqp",
            InstanceId: NewInstanceId(),
            Kind: CallOwnershipKind.Streaming,
            LeaseUntil: DateTimeOffset.FromUnixTimeMilliseconds(1_715_000_000_000));

        var encoded = CallOwnershipCodec.Encode(owner);
        var decoded = CallOwnershipCodec.Decode(encoded);

        Assert.Equal(owner, decoded);
        Assert.StartsWith(owner.InstanceId + "|", encoded);
    }

    [Fact]
    public void Codec_Rejects_Pipe_In_Identity_Fields()
    {
        var owner = new CallOwnership(
            ClusterId: "bad|cluster",
            PodId: "pod-1",
            InstanceId: NewInstanceId(),
            Kind: CallOwnershipKind.Verb,
            LeaseUntil: DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => CallOwnershipCodec.Encode(owner));
    }

    private static (InMemoryCallOwnershipDirectory Store, MutableClusterIdentity Identity) CreateStore(
        TimeSpan? leaseDuration = null,
        TimeProvider? timeProvider = null)
    {
        var identity = new MutableClusterIdentity
        {
            ClusterId = "cluster-1",
            PodId = "pod-1",
            InstanceId = NewInstanceId(),
        };
        var options = Options.Create(new HyperscaleOptions
        {
            CallOwnership = new CallOwnershipOptions
            {
                LeaseDuration = leaseDuration ?? TimeSpan.FromSeconds(90),
            },
        });
        var store = new InMemoryCallOwnershipDirectory(identity, options, timeProvider ?? TimeProvider.System);
        return (store, identity);
    }

    private static string NewInstanceId() => Guid.NewGuid().ToString("N");

    private sealed class MutableClusterIdentity : IClusterIdentity
    {
        public string ClusterId { get; set; } = "cluster-1";
        public string PodId { get; set; } = "pod-1";
        public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public TestTimeProvider(DateTimeOffset start)
        {
            _now = start;
        }

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}

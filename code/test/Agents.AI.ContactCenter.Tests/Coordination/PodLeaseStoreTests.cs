using Agents.AI.ContactCenter.Coordination;
using Agents.AI.ContactCenter.Coordination.Core;

namespace Agents.AI.ContactCenter.Tests.Coordination;

public class PodLeaseStoreTests
{
    [Fact]
    public async Task IsAlive_Returns_False_When_No_Lease()
    {
        var identity = new MutableClusterIdentity { ClusterId = "c-1", PodId = "pod-1", InstanceId = NewInstanceId() };
        var store = new InMemoryPodLeaseStore(identity, TimeProvider.System);

        Assert.False(await store.IsAliveAsync("c-1", "pod-1"));
    }

    [Fact]
    public async Task Renew_Then_IsAlive_For_Local_Pod()
    {
        var identity = new MutableClusterIdentity { ClusterId = "c-1", PodId = "pod-1", InstanceId = NewInstanceId() };
        var store = new InMemoryPodLeaseStore(identity, TimeProvider.System);

        await store.RenewAsync(TimeSpan.FromSeconds(90));

        Assert.True(await store.IsAliveAsync("c-1", "pod-1"));
    }

    [Fact]
    public async Task IsAlive_Returns_False_After_Lease_Expires()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var identity = new MutableClusterIdentity { ClusterId = "c-1", PodId = "pod-1", InstanceId = NewInstanceId() };
        var store = new InMemoryPodLeaseStore(identity, time);

        await store.RenewAsync(TimeSpan.FromSeconds(90));
        Assert.True(await store.IsAliveAsync("c-1", "pod-1"));

        time.Advance(TimeSpan.FromSeconds(91));

        Assert.False(await store.IsAliveAsync("c-1", "pod-1"));
    }

    [Fact]
    public async Task Renew_Extends_Existing_Lease()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var identity = new MutableClusterIdentity { ClusterId = "c-1", PodId = "pod-1", InstanceId = NewInstanceId() };
        var store = new InMemoryPodLeaseStore(identity, time);

        await store.RenewAsync(TimeSpan.FromSeconds(90));
        time.Advance(TimeSpan.FromSeconds(60));
        await store.RenewAsync(TimeSpan.FromSeconds(90));

        time.Advance(TimeSpan.FromSeconds(60));

        Assert.True(await store.IsAliveAsync("c-1", "pod-1"));
    }

    [Fact]
    public async Task Release_Removes_Local_Lease()
    {
        var identity = new MutableClusterIdentity { ClusterId = "c-1", PodId = "pod-1", InstanceId = NewInstanceId() };
        var store = new InMemoryPodLeaseStore(identity, TimeProvider.System);

        await store.RenewAsync(TimeSpan.FromSeconds(90));
        await store.ReleaseAsync();

        Assert.False(await store.IsAliveAsync("c-1", "pod-1"));
    }

    [Fact]
    public async Task Release_Does_Not_Delete_Other_Instance_Lease()
    {
        var identity = new MutableClusterIdentity { ClusterId = "c-1", PodId = "pod-1", InstanceId = NewInstanceId() };
        var store = new InMemoryPodLeaseStore(identity, TimeProvider.System);

        await store.RenewAsync(TimeSpan.FromSeconds(90));

        // Simulate the same pod re-launched with a fresh InstanceId; the new
        // process must not be able to release the previous incarnation's lease.
        identity.InstanceId = NewInstanceId();

        await store.ReleaseAsync();

        Assert.True(await store.IsAliveAsync("c-1", "pod-1"));
    }

    [Fact]
    public async Task IsAlive_Across_Different_Pods_Is_Independent()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var identity = new MutableClusterIdentity { ClusterId = "c-1", PodId = "pod-A", InstanceId = NewInstanceId() };
        var store = new InMemoryPodLeaseStore(identity, time);

        await store.RenewAsync(TimeSpan.FromSeconds(90));

        identity.PodId = "pod-B";
        identity.InstanceId = NewInstanceId();
        await store.RenewAsync(TimeSpan.FromSeconds(90));

        Assert.True(await store.IsAliveAsync("c-1", "pod-A"));
        Assert.True(await store.IsAliveAsync("c-1", "pod-B"));
        Assert.False(await store.IsAliveAsync("c-1", "pod-C"));
        Assert.False(await store.IsAliveAsync("c-2", "pod-A"));
    }

    [Fact]
    public async Task Release_From_Other_Pod_Identity_Does_Not_Affect_Local()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var identity = new MutableClusterIdentity { ClusterId = "c-1", PodId = "pod-A", InstanceId = NewInstanceId() };
        var store = new InMemoryPodLeaseStore(identity, time);

        await store.RenewAsync(TimeSpan.FromSeconds(90));

        // Switch identity to pod-B and release; only pod-B's slot (which never existed) is touched.
        identity.PodId = "pod-B";
        identity.InstanceId = NewInstanceId();
        await store.ReleaseAsync();

        identity.PodId = "pod-A";
        Assert.True(await store.IsAliveAsync("c-1", "pod-A"));
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

        public TestTimeProvider(DateTimeOffset start) => _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}

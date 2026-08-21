using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Coordination;
using Agents.AI.ContactCenter.Coordination.Core;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.Tests.Coordination;

public class CallOwnershipReaperTests
{
    [Fact]
    public async Task Reap_Returns_Zero_When_Directory_Is_Empty()
    {
        var (directory, leases, _, _) = CreateRig();

        var reaped = await directory.ReapOrphansAsync(leases);

        Assert.Equal(0, reaped);
    }

    [Fact]
    public async Task Reap_Skips_Calls_With_Live_Lease()
    {
        var (directory, leases, time, _) = CreateRig();

        await directory.TryAcquireAsync("call-1", CallOwnershipKind.Verb);
        await leases.RenewAsync(TimeSpan.FromSeconds(90));

        // Lease is still in the future; reaper must not touch it.
        time.Advance(TimeSpan.FromSeconds(10));

        var reaped = await directory.ReapOrphansAsync(leases);

        Assert.Equal(0, reaped);
        Assert.NotNull(await directory.GetOwnerAsync("call-1"));
    }

    [Fact]
    public async Task Reap_Skips_Expired_Call_When_Pod_Is_Still_Alive()
    {
        var (directory, leases, time, _) = CreateRig();

        await directory.TryAcquireAsync("call-1", CallOwnershipKind.Verb);

        // Pod heartbeat fires repeatedly; the per-call lease lapses (we did
        // not renew it). Pod-level lease is alive — reaper must not steal.
        time.Advance(TimeSpan.FromSeconds(70));
        await leases.RenewAsync(TimeSpan.FromSeconds(90));

        time.Advance(TimeSpan.FromSeconds(25));
        // call-1 ownership.LeaseUntil was T+90 (now T+95 → expired); pod
        // lease was renewed at T+70 with 90s TTL → still alive at T+95.

        var reaped = await directory.ReapOrphansAsync(leases);

        Assert.Equal(0, reaped);

        // Reaper preserved the entry, so the local pod can still renew it.
        // (GetOwnerAsync filters expired leases, so it returns null even when
        // the entry is intact — RenewAsync is the right liveness probe here.)
        Assert.True(await directory.RenewAsync("call-1", CallOwnershipKind.Verb));
    }

    [Fact]
    public async Task Reap_Removes_Expired_Call_When_Owning_Pod_Is_Dead()
    {
        var (directory, leases, time, identity) = CreateRig();

        await directory.TryAcquireAsync("call-1", CallOwnershipKind.Verb);
        await leases.RenewAsync(TimeSpan.FromSeconds(90));

        // Both leases expire.
        time.Advance(TimeSpan.FromSeconds(91));

        // Reaper runs from a different pod that has its own live lease.
        identity.PodId = "pod-B";
        identity.InstanceId = Guid.NewGuid().ToString("N");
        await leases.RenewAsync(TimeSpan.FromSeconds(90));

        var reaped = await directory.ReapOrphansAsync(leases);

        Assert.Equal(1, reaped);
        Assert.Null(await directory.GetOwnerAsync("call-1"));
    }

    [Fact]
    public async Task Reap_Removes_Multiple_Orphans_In_One_Sweep()
    {
        var (directory, leases, time, identity) = CreateRig();

        await directory.TryAcquireAsync("call-1", CallOwnershipKind.Verb);
        await directory.TryAcquireAsync("call-2", CallOwnershipKind.Streaming);
        await directory.TryAcquireAsync("call-3", CallOwnershipKind.Verb);
        await leases.RenewAsync(TimeSpan.FromSeconds(90));

        time.Advance(TimeSpan.FromSeconds(91));

        identity.PodId = "pod-B";
        identity.InstanceId = Guid.NewGuid().ToString("N");
        await leases.RenewAsync(TimeSpan.FromSeconds(90));

        var reaped = await directory.ReapOrphansAsync(leases);

        Assert.Equal(3, reaped);
        Assert.Null(await directory.GetOwnerAsync("call-1"));
        Assert.Null(await directory.GetOwnerAsync("call-2"));
        Assert.Null(await directory.GetOwnerAsync("call-3"));
    }

    [Fact]
    public async Task Reap_Mix_Of_Alive_And_Dead_Pods()
    {
        var (directory, leases, time, identity) = CreateRig();

        // pod-A owns call-1 and call-2.
        await directory.TryAcquireAsync("call-1", CallOwnershipKind.Verb);
        await directory.TryAcquireAsync("call-2", CallOwnershipKind.Streaming);
        await leases.RenewAsync(TimeSpan.FromSeconds(90));

        // pod-B owns call-3 and keeps both its pod and call leases alive.
        identity.PodId = "pod-B";
        identity.InstanceId = Guid.NewGuid().ToString("N");
        await directory.TryAcquireAsync("call-3", CallOwnershipKind.Verb);
        await leases.RenewAsync(TimeSpan.FromSeconds(90));

        // Sixty seconds in, pod-B refreshes call-3 + its pod lease; pod-A
        // misses every renewal.
        time.Advance(TimeSpan.FromSeconds(60));
        await directory.RenewAsync("call-3", CallOwnershipKind.Verb);
        await leases.RenewAsync(TimeSpan.FromSeconds(90));

        // Push past pod-A's original 90 s lease window.
        time.Advance(TimeSpan.FromSeconds(35));

        var reaped = await directory.ReapOrphansAsync(leases);

        // call-1 + call-2 reapable (pod-A dead, leases expired); call-3
        // protected (pod-B alive AND its lease was refreshed).
        Assert.Equal(2, reaped);
        Assert.Null(await directory.GetOwnerAsync("call-1"));
        Assert.Null(await directory.GetOwnerAsync("call-2"));
        Assert.NotNull(await directory.GetOwnerAsync("call-3"));
    }

    [Fact]
    public async Task Reap_Null_LeaseStore_Throws()
    {
        var (directory, _, _, _) = CreateRig();

        await Assert.ThrowsAsync<ArgumentNullException>(() => directory.ReapOrphansAsync(null!));
    }

    private static (InMemoryCallOwnershipDirectory Directory, InMemoryPodLeaseStore Leases, TestTimeProvider Time, MutableClusterIdentity Identity) CreateRig()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var identity = new MutableClusterIdentity
        {
            ClusterId = "c-1",
            PodId = "pod-A",
            InstanceId = Guid.NewGuid().ToString("N"),
        };
        var options = Options.Create(new HyperscaleOptions
        {
            CallOwnership = new CallOwnershipOptions { LeaseDuration = TimeSpan.FromSeconds(90) },
        });
        var directory = new InMemoryCallOwnershipDirectory(identity, options, time);
        var leases = new InMemoryPodLeaseStore(identity, time);
        return (directory, leases, time, identity);
    }

    private sealed class MutableClusterIdentity : IClusterIdentity
    {
        public string ClusterId { get; set; } = "c-1";
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

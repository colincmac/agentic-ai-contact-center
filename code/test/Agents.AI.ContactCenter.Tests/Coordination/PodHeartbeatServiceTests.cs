using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Coordination;
using Agents.AI.ContactCenter.Coordination.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.Tests.Coordination;

public class PodHeartbeatServiceTests
{
    [Fact]
    public void Track_And_Untrack_Are_Idempotent()
    {
        var harness = CreateHarness();

        harness.Heartbeat.TrackOwnedCall("call-1", CallOwnershipKind.Streaming);
        harness.Heartbeat.TrackOwnedCall("call-1", CallOwnershipKind.Streaming);
        harness.Heartbeat.TrackOwnedCall("call-2", CallOwnershipKind.Verb);

        Assert.Equal(2, harness.Heartbeat.TrackedCalls.Count);
        Assert.Equal(CallOwnershipKind.Streaming, harness.Heartbeat.TrackedCalls["call-1"]);
        Assert.Equal(CallOwnershipKind.Verb, harness.Heartbeat.TrackedCalls["call-2"]);

        harness.Heartbeat.UntrackOwnedCall("call-1");
        harness.Heartbeat.UntrackOwnedCall("call-1");
        harness.Heartbeat.UntrackOwnedCall("missing");

        Assert.Single(harness.Heartbeat.TrackedCalls);
        Assert.DoesNotContain("call-1", harness.Heartbeat.TrackedCalls.Keys);
    }

    [Fact]
    public void Track_Rejects_Null_Or_Whitespace_Id()
    {
        var harness = CreateHarness();

        Assert.ThrowsAny<ArgumentException>(() => harness.Heartbeat.TrackOwnedCall(null!, CallOwnershipKind.Verb));
        Assert.ThrowsAny<ArgumentException>(() => harness.Heartbeat.TrackOwnedCall("   ", CallOwnershipKind.Verb));
    }

    [Fact]
    public async Task Heartbeat_Tick_Renews_Pod_Lease()
    {
        var harness = CreateHarness();

        await harness.Heartbeat.RunHeartbeatTickAsync(CancellationToken.None);

        Assert.True(await harness.PodLeases.IsAliveAsync(harness.Identity.ClusterId, harness.Identity.PodId));
    }

    [Fact]
    public async Task Heartbeat_Tick_Renews_Each_Owned_Call_Lease()
    {
        var harness = CreateHarness(leaseDuration: TimeSpan.FromSeconds(90));

        var first = await harness.Directory.TryAcquireAsync("call-1", CallOwnershipKind.Streaming);
        var second = await harness.Directory.TryAcquireAsync("call-2", CallOwnershipKind.Verb);
        Assert.True(first.Acquired);
        Assert.True(second.Acquired);

        harness.Heartbeat.TrackOwnedCall("call-1", CallOwnershipKind.Streaming);
        harness.Heartbeat.TrackOwnedCall("call-2", CallOwnershipKind.Verb);

        // Advance past two-thirds of the lease so a missed renew would expire it.
        harness.Time.Advance(TimeSpan.FromSeconds(70));

        await harness.Heartbeat.RunHeartbeatTickAsync(CancellationToken.None);

        harness.Time.Advance(TimeSpan.FromSeconds(40));

        Assert.NotNull(await harness.Directory.GetOwnerAsync("call-1"));
        Assert.NotNull(await harness.Directory.GetOwnerAsync("call-2"));
    }

    [Fact]
    public async Task Heartbeat_Tick_Untracks_Call_When_Renew_Returns_False()
    {
        var harness = CreateHarness();

        await harness.Directory.TryAcquireAsync("call-1", CallOwnershipKind.Verb);
        harness.Heartbeat.TrackOwnedCall("call-1", CallOwnershipKind.Verb);
        harness.Heartbeat.TrackOwnedCall("call-orphan", CallOwnershipKind.Verb);

        // call-orphan is tracked locally but the directory has no record →
        // RenewAsync returns false → heartbeat must drop it from its tracking.
        await harness.Heartbeat.RunHeartbeatTickAsync(CancellationToken.None);

        Assert.Contains("call-1", harness.Heartbeat.TrackedCalls.Keys);
        Assert.DoesNotContain("call-orphan", harness.Heartbeat.TrackedCalls.Keys);
    }

    [Fact]
    public async Task Heartbeat_Tick_Continues_When_Pod_Lease_Renew_Throws()
    {
        var podLeases = new ThrowingPodLeaseStore();
        var harness = CreateHarness(podLeases: podLeases);

        await harness.Directory.TryAcquireAsync("call-1", CallOwnershipKind.Verb);
        harness.Heartbeat.TrackOwnedCall("call-1", CallOwnershipKind.Verb);

        // Should not throw; call-1 lease will be renewed on a later tick once
        // pod lease comes back online. The current tick returns early without
        // touching call leases.
        await harness.Heartbeat.RunHeartbeatTickAsync(CancellationToken.None);

        Assert.Contains("call-1", harness.Heartbeat.TrackedCalls.Keys);
    }

    [Fact]
    public async Task Reaper_Sweep_Returns_Zero_When_No_Orphans()
    {
        var harness = CreateHarness();
        await harness.Heartbeat.RunHeartbeatTickAsync(CancellationToken.None);

        var reaped = await harness.Heartbeat.RunReaperSweepAsync(CancellationToken.None);

        Assert.Equal(0, reaped);
    }

    [Fact]
    public async Task Reaper_Sweep_Reaps_Orphans_From_Dead_Pod()
    {
        var harness = CreateHarness();

        // Pod A acquires a call and renews its pod lease.
        await harness.Directory.TryAcquireAsync("call-A", CallOwnershipKind.Verb);
        await harness.PodLeases.RenewAsync(TimeSpan.FromSeconds(90));

        // Advance past both leases without renewing.
        harness.Time.Advance(TimeSpan.FromSeconds(91));

        // Pod B's perspective starts now.
        harness.Identity.PodId = "pod-B";
        harness.Identity.InstanceId = Guid.NewGuid().ToString("N");
        await harness.PodLeases.RenewAsync(TimeSpan.FromSeconds(90));

        var reaped = await harness.Heartbeat.RunReaperSweepAsync(CancellationToken.None);

        Assert.Equal(1, reaped);
    }

    [Fact]
    public async Task StopAsync_Releases_Pod_Lease()
    {
        var harness = CreateHarness();
        await harness.Heartbeat.RunHeartbeatTickAsync(CancellationToken.None);

        await harness.Heartbeat.StopAsync(CancellationToken.None);

        Assert.False(await harness.PodLeases.IsAliveAsync(harness.Identity.ClusterId, harness.Identity.PodId));
    }

    [Fact]
    public async Task StopAsync_Swallows_Lease_Release_Failure()
    {
        var podLeases = new ThrowingPodLeaseStore();
        var harness = CreateHarness(podLeases: podLeases);

        await harness.Heartbeat.StopAsync(CancellationToken.None);
        // No throw expected.
    }

    [Fact]
    public async Task StopAsync_Skips_Release_When_ReleasePodLeaseOnStop_Disabled()
    {
        var harness = CreateHarness(releasePodLeaseOnStop: false);
        await harness.Heartbeat.RunHeartbeatTickAsync(CancellationToken.None);

        await harness.Heartbeat.StopAsync(CancellationToken.None);

        Assert.True(await harness.PodLeases.IsAliveAsync(harness.Identity.ClusterId, harness.Identity.PodId));
    }

    [Fact]
    public async Task StopAsync_Bounded_By_DrainTimeout_When_Release_Hangs()
    {
        var podLeases = new HangingPodLeaseStore();
        var harness = CreateHarness(
            podLeases: podLeases,
            drainTimeout: TimeSpan.FromMilliseconds(50));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await harness.Heartbeat.StopAsync(CancellationToken.None);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"StopAsync took {sw.Elapsed} — expected drain-timeout-bounded.");
        Assert.True(podLeases.ReleaseStarted, "Release should have been invoked before timeout.");
    }

    private static Harness CreateHarness(
        IPodLeaseStore? podLeases = null,
        TimeSpan? leaseDuration = null,
        TimeSpan? heartbeatInterval = null,
        TimeSpan? reaperInterval = null,
        bool reaperEnabled = true,
        bool releasePodLeaseOnStop = true,
        TimeSpan? drainTimeout = null)
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var identity = new MutableClusterIdentity
        {
            ClusterId = "c-1",
            PodId = "pod-A",
            InstanceId = Guid.NewGuid().ToString("N"),
        };

        var hyperscale = new HyperscaleOptions
        {
            CallOwnership = new CallOwnershipOptions
            {
                LeaseDuration = leaseDuration ?? TimeSpan.FromSeconds(90),
            },
            PodHeartbeat = new PodHeartbeatOptions
            {
                HeartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(30),
                LeaseDuration = leaseDuration ?? TimeSpan.FromSeconds(90),
                ReaperInterval = reaperInterval ?? TimeSpan.FromSeconds(60),
                ReaperEnabled = reaperEnabled,
                ReleasePodLeaseOnStop = releasePodLeaseOnStop,
                DrainTimeout = drainTimeout ?? TimeSpan.FromSeconds(5),
            },
        };

        var optionsValue = Options.Create(hyperscale);
        var directory = new InMemoryCallOwnershipDirectory(identity, optionsValue, time);
        var leaseStore = podLeases ?? new InMemoryPodLeaseStore(identity, time);
        var monitor = new TestOptionsMonitor<HyperscaleOptions>(hyperscale);
        var service = new PodHeartbeatService(
            leaseStore,
            directory,
            monitor,
            time,
            NullLogger<PodHeartbeatService>.Instance);

        return new Harness(service, directory, leaseStore, identity, time);
    }

    private sealed record Harness(
        PodHeartbeatService Heartbeat,
        InMemoryCallOwnershipDirectory Directory,
        IPodLeaseStore PodLeases,
        MutableClusterIdentity Identity,
        TestTimeProvider Time);

    private sealed class ThrowingPodLeaseStore : IPodLeaseStore
    {
        public Task RenewAsync(TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated transient failure");
        public Task ReleaseAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated transient failure");
        public Task<bool> IsAliveAsync(string clusterId, string podId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private sealed class HangingPodLeaseStore : IPodLeaseStore
    {
        public bool ReleaseStarted { get; private set; }

        public Task RenewAsync(TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async Task ReleaseAsync(CancellationToken cancellationToken = default)
        {
            ReleaseStarted = true;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }

        public Task<bool> IsAliveAsync(string clusterId, string podId, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
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

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; set; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

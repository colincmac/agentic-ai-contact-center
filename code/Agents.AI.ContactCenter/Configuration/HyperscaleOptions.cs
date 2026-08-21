using Agents.AI.ContactCenter.Coordination.Core;

namespace Agents.AI.ContactCenter.Configuration;

/// <summary>
/// Root configuration for the hyperscale coordination plane (ADR-0010,
/// ADR-0011, ADR-0004). Sub-sections are added incrementally as the
/// primitives that consume them land.
/// </summary>
public sealed class HyperscaleOptions
{
    public const string SectionName = "Hyperscale";

    /// <summary>
    /// Cluster / pod / instance identity for this process. Drives ownership
    /// leases, capacity coordination, and telemetry tagging.
    /// </summary>
    public ClusterIdentityOptions ClusterIdentity { get; set; } = new();

    /// <summary>
    /// Webhook dedup token policy. Used by
    /// <see cref="Coordination.IWebhookIdempotencyStore"/>
    /// to short-circuit duplicate at-least-once mid-call callbacks per ADR-0004.
    /// </summary>
    public WebhookIdempotencyOptions WebhookIdempotency { get; set; } = new();

    /// <summary>Distributed admission policy for the IncomingCall AnswerCall side effect.</summary>
    public IncomingCallAdmissionOptions IncomingCallAdmission { get; set; } = new();

    /// <summary>
    /// Per-call ownership lease policy used by
    /// <see cref="Coordination.ICallOwnershipDirectory"/>
    /// to bind a call to the pod that holds its bi-di WS / verb session
    /// (ADR-0011).
    /// </summary>
    public CallOwnershipOptions CallOwnership { get; set; } = new();

    /// <summary>
    /// Per-cluster tier-ceiling policy used by
    /// <see cref="Coordination.ITierCeilingProvider"/>
    /// to broadcast and cache the active degradation ceiling (ADR-0008).
    /// </summary>
    public TierCeilingOptions TierCeiling { get; set; } = new();

    /// <summary>
    /// Pod heartbeat and reaper cadence used by
    /// <see cref="Coordination.IPodHeartbeat"/> to
    /// renew the local <c>pod:lease:*</c> + every owned-call lease and to
    /// sweep orphaned <c>owner:*</c> entries (ADR-0011).
    /// </summary>
    public PodHeartbeatOptions PodHeartbeat { get; set; } = new();

    /// <summary>
    /// Cross-pod webhook forwarder transport per ADR-0011. Drives the URL
    /// shape used by
    /// <see cref="Coordination.IWebhookForwarder"/>
    /// when a streaming-mode mid-call event lands on a non-owning pod.
    /// </summary>
    public WebhookForwarderOptions WebhookForwarder { get; set; } = new();

    /// <summary>
    /// Per-cluster capacity coordination policy used by
    /// <see cref="Calling.Core.DistributedAgentTierResolver"/>
    /// to scale the per-tier <c>MaxConcurrent</c> cap to this cluster's slice
    /// of the global active-active pool (ADR-0010).
    /// </summary>
    public CapacityCoordinationOptions CapacityCoordination { get; set; } = new();
}

/// <summary>
/// Configuration for <see cref="Coordination.IClusterIdentity"/>.
/// All values resolve from configuration first, then the documented
/// environment-variable fallbacks, then dev defaults.
/// </summary>
public sealed class ClusterIdentityOptions
{
    /// <summary>
    /// Environment variable consulted when <see cref="ClusterId"/> is unset.
    /// Set this in the Helm chart / deployment manifest per ADR-0010.
    /// </summary>
    public const string ClusterIdEnvironmentVariable = "HYPERSCALE_CLUSTER_ID";

    /// <summary>
    /// Environment variable consulted when <see cref="PodId"/> is unset and
    /// before falling back to the standard Kubernetes <c>HOSTNAME</c>.
    /// </summary>
    public const string PodIdEnvironmentVariable = "HYPERSCALE_POD_ID";

    /// <summary>
    /// Cluster identifier. When null or whitespace, falls back to env var
    /// <see cref="ClusterIdEnvironmentVariable"/>; if that is also unset,
    /// defaults to <c>"local"</c> for dev / single-cluster deployments.
    /// </summary>
    public string? ClusterId { get; set; }

    /// <summary>
    /// Pod identifier. When null or whitespace, falls back to env var
    /// <see cref="PodIdEnvironmentVariable"/>, then <c>HOSTNAME</c>
    /// (Kubernetes default), then <see cref="Environment.MachineName"/>.
    /// </summary>
    public string? PodId { get; set; }
}

/// <summary>
/// Configuration for <see cref="Coordination.IWebhookIdempotencyStore"/>.
/// </summary>
public sealed class WebhookIdempotencyOptions
{
    /// <summary>
    /// How long a dedup token persists. Must be at least the longest plausible
    /// retry window of the at-least-once webhook publisher. Default 30 minutes
    /// matches ADR-0004 for ACS Call Automation callbacks.
    /// </summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromMinutes(30);
}

public sealed class IncomingCallAdmissionOptions
{
    /// <summary>Lease held while creating the local session and beginning ACS AnswerCall.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long answered and recovery-required records remain available to reject redelivery.</summary>
    public TimeSpan RecordLifetime { get; set; } = TimeSpan.FromHours(8);
}

/// <summary>
/// Configuration for <see cref="Coordination.ICallOwnershipDirectory"/>.
/// </summary>
public sealed class CallOwnershipOptions
{
    /// <summary>
    /// How long a per-call ownership lease lives before the directory
    /// considers the owner dead and the call reapable. The pod heartbeat
    /// (ADR-0011) renews owned-call leases at one-third of this interval, so
    /// a single missed heartbeat does not orphan calls. Default 90 s matches
    /// the lease window in ADR-0011.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(90);
}

/// <summary>
/// Configuration for <see cref="Coordination.ITierCeilingProvider"/>.
/// </summary>
public sealed class TierCeilingOptions
{
    /// <summary>
    /// Ceiling assumed at process start before the first read from Redis (or
    /// when no value has ever been set for the cluster). Default
    /// <see cref="AgentTier.RealtimeVoice"/> means "no degradation" — admit
    /// calls at the highest tier the per-tier capacity counter allows.
    /// </summary>
    public AgentTier DefaultCeiling { get; set; } = AgentTier.RealtimeVoice;
}

/// <summary>
/// Configuration for <see cref="Coordination.IPodHeartbeat"/>
/// per ADR-0011. The heartbeat lease window is intentionally distinct from
/// <see cref="CallOwnershipOptions.LeaseDuration"/>: the pod lease and the
/// per-call lease are renewed in the same tick but Redis applies independent
/// TTLs to each key family.
/// </summary>
public sealed class PodHeartbeatOptions
{
    /// <summary>
    /// How often the pod renews its own <c>pod:lease:*</c> and the lease of
    /// every tracked owned call. Default 30 s per ADR-0011.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// TTL written by the heartbeat onto <c>pod:lease:{clusterId}:{podId}</c>.
    /// Should be at least 3× <see cref="HeartbeatInterval"/> so a single
    /// missed tick does not orphan the pod. Default 90 s per ADR-0011.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How often the heartbeat invokes the cross-pod reaper sweep. Defaults
    /// to 60 s — twice the heartbeat cadence — so reap pressure stays low
    /// while still bounding orphan lifetime to roughly
    /// <see cref="LeaseDuration"/> + this interval.
    /// </summary>
    public TimeSpan ReaperInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// When <c>false</c> the heartbeat still renews pod / call leases but
    /// does not run the orphan sweep. Used during incident triage to freeze
    /// reaper behavior without unmounting the heartbeat.
    /// </summary>
    public bool ReaperEnabled { get; set; } = true;

    /// <summary>
    /// Maximum time <see cref="PodHeartbeatService.StopAsync"/>
    /// will wait for the pod-lease release to complete before falling through
    /// and letting the lease expire via TTL. Bounds graceful-shutdown latency
    /// so a slow / hung Redis cannot stall pod termination past the
    /// container-orchestrator's own grace period.
    /// </summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// When <c>false</c>, <see cref="PodHeartbeatService.StopAsync"/>
    /// skips the explicit pod-lease release and lets the lease expire via
    /// TTL. Useful for crash-loop scenarios where preserving the lease lets
    /// the orphan-detection path observe the pod outage uniformly across
    /// drain types. Defaults to <c>true</c> (eager release on graceful stop).
    /// </summary>
    public bool ReleasePodLeaseOnStop { get; set; } = true;
}

/// <summary>
/// Configuration for
/// <see cref="Coordination.IWebhookForwarder"/> per
/// ADR-0011. Forwarding only happens inside one cluster (cross-cluster
/// forwards are blocked by design), so the URL template targets the
/// Kubernetes headless service that fronts the pods of the owning workload.
/// </summary>
public sealed class WebhookForwarderOptions
{
    /// <summary>
    /// Headless Kubernetes <c>Service</c> name that resolves a per-pod DNS
    /// entry of the form
    /// <c>{podId}.{HeadlessServiceName}.{Namespace}.svc.cluster.local</c>.
    /// Must be set to a non-empty value for the HTTP forwarder to run.
    /// </summary>
    public string HeadlessServiceName { get; set; } = string.Empty;

    /// <summary>
    /// Kubernetes namespace the headless service lives in. Must be set to
    /// a non-empty value for the HTTP forwarder to run.
    /// </summary>
    public string Namespace { get; set; } = string.Empty;

    /// <summary>
    /// Cluster-DNS suffix appended after <see cref="Namespace"/>. Defaults
    /// to the AKS / kubeadm default and rarely needs to change.
    /// </summary>
    public string ClusterDomain { get; set; } = "svc.cluster.local";

    /// <summary>
    /// URL scheme used to reach peer pods. Stays <c>http</c> in-cluster
    /// because the cluster network is the trust boundary; switch to
    /// <c>https</c> only when a service mesh terminates TLS at the pod.
    /// </summary>
    public string Scheme { get; set; } = "http";

    /// <summary>
    /// TCP port the peer pod exposes the callback API on.
    /// </summary>
    public int Port { get; set; } = 8080;

    /// <summary>
    /// Path on the peer pod that accepts forwarded callbacks. Must match
    /// the endpoint mapped by the application; defaults to the path named
    /// in ADR-0011.
    /// </summary>
    public string ForwardPath { get; set; } = "/automation/callbacks/forward";

    /// <summary>
    /// Per-attempt timeout for the forwarded HTTP request. Should comfortably
    /// fit under the answer-window SLA in ADR-0003.
    /// </summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Number of additional retry attempts after the first failed attempt.
    /// Total HTTP attempts = 1 + <see cref="MaxRetryAttempts"/>.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 2;

    /// <summary>
    /// Fixed delay between retry attempts. The retry budget is small enough
    /// that exponential backoff is not warranted; the answer-window SLA is
    /// the limiting factor.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);
}

/// <summary>
/// Configuration for the active-active capacity split per ADR-0010. Each
/// cluster admits a fraction of the per-tier global cap so the sum across
/// clusters does not exceed the cap even if one cluster's clients are not
/// aware that the other is up.
/// </summary>
public sealed class CapacityCoordinationOptions
{
    /// <summary>
    /// Fraction of the per-tier <c>MaxConcurrent</c> cap this cluster is
    /// allowed to admit. Must be in <c>(0, 1]</c>. <c>1.0</c> (the default)
    /// disables sharding — appropriate for single-cluster deployments and
    /// the in-memory dev path. In a 2-cluster active-active topology, set
    /// each cluster to <c>0.5</c>; values outside the range are clamped to
    /// <c>(0, 1]</c> by the resolver to fail-safe (under-admit) rather than
    /// over-admit.
    /// </summary>
    public double ClusterShare { get; set; } = 1.0;
}

namespace Agents.AI.ContactCenter.Coordination;

/// <summary>
/// Stable per-pod identity used as the lease principal for ownership,
/// heartbeat, and capacity coordination across active-active clusters
/// (ADR-0010, ADR-0011, ADR-0004).
/// </summary>
/// <remarks>
/// All three values are resolved once at construction time and remain
/// constant for the lifetime of the process.
/// </remarks>
public interface IClusterIdentity
{
    /// <summary>
    /// Cluster the pod runs in. Sourced from configuration / env var; set
    /// once per Helm release and stable across pod restarts.
    /// </summary>
    string ClusterId { get; }

    /// <summary>
    /// Kubernetes pod name. Stable for the pod's lifetime; a re-launched pod
    /// of the same StatefulSet ordinal reuses the same value, so it cannot
    /// distinguish a fresh process from its predecessor on its own — pair
    /// with <see cref="InstanceId"/> for that.
    /// </summary>
    string PodId { get; }

    /// <summary>
    /// Fresh GUID minted at process start. Distinguishes a re-launched pod
    /// from its previous incarnation so leases written by the prior process
    /// are not silently inherited.
    /// </summary>
    string InstanceId { get; }
}

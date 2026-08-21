using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.Coordination.Core;

/// <summary>
/// Default <see cref="IClusterIdentity"/> implementation that resolves
/// <c>clusterId</c> / <c>podId</c> from <see cref="HyperscaleOptions"/> with
/// the documented environment-variable and Kubernetes fallbacks, and mints
/// a fresh <c>instanceId</c> per process.
/// </summary>
public sealed class HostClusterIdentity : IClusterIdentity
{
    public HostClusterIdentity(IOptions<HyperscaleOptions> options)
    {
        var identity = options.Value.ClusterIdentity;
        ClusterId = ResolveClusterId(identity);
        PodId = ResolvePodId(identity);
        InstanceId = Guid.NewGuid().ToString("N");
    }

    public string ClusterId { get; }

    public string PodId { get; }

    public string InstanceId { get; }

    private static string ResolveClusterId(ClusterIdentityOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ClusterId))
        {
            return options.ClusterId;
        }

        var fromEnv = Environment.GetEnvironmentVariable(ClusterIdentityOptions.ClusterIdEnvironmentVariable);
        return string.IsNullOrWhiteSpace(fromEnv) ? "local" : fromEnv;
    }

    private static string ResolvePodId(ClusterIdentityOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.PodId))
        {
            return options.PodId;
        }

        var configured = Environment.GetEnvironmentVariable(ClusterIdentityOptions.PodIdEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var hostname = Environment.GetEnvironmentVariable("HOSTNAME");
        return string.IsNullOrWhiteSpace(hostname) ? Environment.MachineName : hostname;
    }
}

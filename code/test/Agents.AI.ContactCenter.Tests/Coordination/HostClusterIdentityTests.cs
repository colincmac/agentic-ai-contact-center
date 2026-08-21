using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Coordination;
using Agents.AI.ContactCenter.Coordination.Core;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.Tests.Coordination;

public class HostClusterIdentityTests
{
    [Fact]
    public void Defaults_To_Local_Cluster_When_Nothing_Configured()
    {
        using var env = EnvironmentScope.Clear(
            ClusterIdentityOptions.ClusterIdEnvironmentVariable,
            ClusterIdentityOptions.PodIdEnvironmentVariable);

        var identity = new HostClusterIdentity(WrapOptions(new ClusterIdentityOptions()));

        Assert.Equal("local", identity.ClusterId);
    }

    [Fact]
    public void Pod_Falls_Back_To_MachineName_When_Hostname_Unset()
    {
        using var env = EnvironmentScope.Clear(
            ClusterIdentityOptions.ClusterIdEnvironmentVariable,
            ClusterIdentityOptions.PodIdEnvironmentVariable,
            "HOSTNAME");

        var identity = new HostClusterIdentity(WrapOptions(new ClusterIdentityOptions()));

        Assert.Equal(Environment.MachineName, identity.PodId);
    }

    [Fact]
    public void Pod_Falls_Back_To_Hostname_Env_Var()
    {
        using var env = EnvironmentScope.Set(
            (ClusterIdentityOptions.PodIdEnvironmentVariable, null),
            ("HOSTNAME", "voice-agent-7c9bd8-xkqp"));

        var identity = new HostClusterIdentity(WrapOptions(new ClusterIdentityOptions()));

        Assert.Equal("voice-agent-7c9bd8-xkqp", identity.PodId);
    }

    [Fact]
    public void Pod_Hyperscale_Env_Var_Wins_Over_Hostname()
    {
        using var env = EnvironmentScope.Set(
            (ClusterIdentityOptions.PodIdEnvironmentVariable, "hyperscale-pod-id"),
            ("HOSTNAME", "kubernetes-hostname"));

        var identity = new HostClusterIdentity(WrapOptions(new ClusterIdentityOptions()));

        Assert.Equal("hyperscale-pod-id", identity.PodId);
    }

    [Fact]
    public void Cluster_Env_Var_Wins_Over_Default()
    {
        using var env = EnvironmentScope.Set(
            (ClusterIdentityOptions.ClusterIdEnvironmentVariable, "eastus2-aks-01"));

        var identity = new HostClusterIdentity(WrapOptions(new ClusterIdentityOptions()));

        Assert.Equal("eastus2-aks-01", identity.ClusterId);
    }

    [Fact]
    public void Configuration_Wins_Over_Env_Var()
    {
        using var env = EnvironmentScope.Set(
            (ClusterIdentityOptions.ClusterIdEnvironmentVariable, "from-env"),
            (ClusterIdentityOptions.PodIdEnvironmentVariable, "from-env-pod"));

        var identity = new HostClusterIdentity(WrapOptions(new ClusterIdentityOptions
        {
            ClusterId = "from-config",
            PodId = "from-config-pod",
        }));

        Assert.Equal("from-config", identity.ClusterId);
        Assert.Equal("from-config-pod", identity.PodId);
    }

    [Fact]
    public void Whitespace_Configuration_Is_Treated_As_Unset()
    {
        using var env = EnvironmentScope.Set(
            (ClusterIdentityOptions.ClusterIdEnvironmentVariable, "from-env"));

        var identity = new HostClusterIdentity(WrapOptions(new ClusterIdentityOptions
        {
            ClusterId = "   ",
        }));

        Assert.Equal("from-env", identity.ClusterId);
    }

    [Fact]
    public void Instance_Id_Is_Unique_Per_Construction()
    {
        using var env = EnvironmentScope.Clear(
            ClusterIdentityOptions.ClusterIdEnvironmentVariable,
            ClusterIdentityOptions.PodIdEnvironmentVariable);

        var first = new HostClusterIdentity(WrapOptions(new ClusterIdentityOptions()));
        var second = new HostClusterIdentity(WrapOptions(new ClusterIdentityOptions()));

        Assert.NotEqual(first.InstanceId, second.InstanceId);
        Assert.Equal(32, first.InstanceId.Length);
        Assert.True(Guid.TryParseExact(first.InstanceId, "N", out _));
    }

    private static IOptions<HyperscaleOptions> WrapOptions(ClusterIdentityOptions clusterIdentity)
    {
        return Options.Create(new HyperscaleOptions { ClusterIdentity = clusterIdentity });
    }
}

/// <summary>
/// Sets and restores environment variables for the lifetime of the scope.
/// Tests touching process env must serialize — see the collection definition
/// below — so that parallel test runners cannot observe a partial mutation.
/// </summary>
internal sealed class EnvironmentScope : IDisposable
{
    private readonly (string Name, string? Original)[] _previous;

    private EnvironmentScope((string Name, string? Value)[] assignments)
    {
        _previous = new (string, string?)[assignments.Length];
        for (var i = 0; i < assignments.Length; i++)
        {
            var (name, value) = assignments[i];
            _previous[i] = (name, Environment.GetEnvironmentVariable(name));
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public static EnvironmentScope Set(params (string Name, string? Value)[] assignments) => new(assignments);

    public static EnvironmentScope Clear(params string[] names)
    {
        var assignments = new (string, string?)[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            assignments[i] = (names[i], null);
        }
        return new EnvironmentScope(assignments);
    }

    public void Dispose()
    {
        foreach (var (name, original) in _previous)
        {
            Environment.SetEnvironmentVariable(name, original);
        }
    }
}

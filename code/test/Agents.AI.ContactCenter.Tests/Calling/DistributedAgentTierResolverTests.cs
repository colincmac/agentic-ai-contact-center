using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Calling.Core;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Coordination;
using Agents.AI.ContactCenter.Coordination.Core;
using Agents.AI.ContactCenter.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.Tests.Calling;

public class DistributedAgentTierResolverTests
{
    [Fact]
    public async Task ResolveAsync_Returns_First_Tier_In_Order_When_It_Has_Capacity()
    {
        var (resolver, tracker, _) = CreateResolver();

        var tier = await resolver.ResolveAsync();

        Assert.Equal(AgentTier.RealtimeVoice, tier);
        Assert.Equal(1, await tracker.GetCountAsync(AgentTier.RealtimeVoice));
    }

    [Fact]
    public async Task ResolveAsync_Falls_Through_To_Next_Tier_When_First_Is_Full()
    {
        var opts = DefaultOptions();
        opts.Tiers[AgentTier.RealtimeVoice].MaxConcurrent = 1;
        var (resolver, tracker, _) = CreateResolver(opts);

        var first = await resolver.ResolveAsync();
        var second = await resolver.ResolveAsync();

        Assert.Equal(AgentTier.RealtimeVoice, first);
        Assert.Equal(AgentTier.ChatCompletionTts, second);
        Assert.Equal(1, await tracker.GetCountAsync(AgentTier.RealtimeVoice));
        Assert.Equal(1, await tracker.GetCountAsync(AgentTier.ChatCompletionTts));
    }

    [Fact]
    public async Task ResolveAsync_Skips_Disabled_Tiers()
    {
        var opts = DefaultOptions();
        opts.Tiers[AgentTier.RealtimeVoice].Enabled = false;
        opts.Tiers[AgentTier.ChatCompletionTts].Enabled = false;
        var (resolver, _, _) = CreateResolver(opts);

        var tier = await resolver.ResolveAsync();

        Assert.Equal(AgentTier.SmallLanguageModel, tier);
    }

    [Fact]
    public async Task ResolveAsync_Skips_Tiers_With_Zero_MaxConcurrent()
    {
        var opts = DefaultOptions();
        opts.Tiers[AgentTier.RealtimeVoice].MaxConcurrent = 0;
        var (resolver, _, _) = CreateResolver(opts);

        var tier = await resolver.ResolveAsync();

        Assert.Equal(AgentTier.ChatCompletionTts, tier);
    }

    [Fact]
    public async Task ResolveAsync_Skips_Tiers_Above_Ceiling()
    {
        var (resolver, _, ceiling) = CreateResolver();
        await ceiling.SetAsync(AgentTier.SmallLanguageModel);

        var tier = await resolver.ResolveAsync();

        Assert.Equal(AgentTier.SmallLanguageModel, tier);
    }

    [Fact]
    public async Task ResolveAsync_Throws_When_All_Tiers_Exhausted()
    {
        var opts = DefaultOptions();
        foreach (var cfg in opts.Tiers.Values)
        {
            cfg.MaxConcurrent = 0;
        }
        var (resolver, _, _) = CreateResolver(opts);

        await Assert.ThrowsAsync<CapacityExhaustedException>(() => resolver.ResolveAsync().AsTask());
    }

    [Fact]
    public async Task ResolveAsync_Throws_When_All_Tiers_Disabled()
    {
        var opts = DefaultOptions();
        foreach (var cfg in opts.Tiers.Values)
        {
            cfg.Enabled = false;
        }
        var (resolver, _, _) = CreateResolver(opts);

        await Assert.ThrowsAsync<CapacityExhaustedException>(() => resolver.ResolveAsync().AsTask());
    }

    [Fact]
    public async Task ResolveAsync_With_Preferred_Tier_Tries_It_First()
    {
        var (resolver, tracker, _) = CreateResolver();

        var tier = await resolver.ResolveAsync(AgentTier.IntentNlu);

        Assert.Equal(AgentTier.IntentNlu, tier);
        Assert.Equal(1, await tracker.GetCountAsync(AgentTier.IntentNlu));
        Assert.Equal(0, await tracker.GetCountAsync(AgentTier.RealtimeVoice));
    }

    [Fact]
    public async Task ResolveAsync_With_Preferred_Tier_Full_Falls_Through_To_Order()
    {
        var opts = DefaultOptions();
        opts.Tiers[AgentTier.IntentNlu].MaxConcurrent = 0;
        var (resolver, tracker, _) = CreateResolver(opts);

        var tier = await resolver.ResolveAsync(AgentTier.IntentNlu);

        Assert.Equal(AgentTier.RealtimeVoice, tier);
        Assert.Equal(1, await tracker.GetCountAsync(AgentTier.RealtimeVoice));
    }

    [Fact]
    public async Task ResolveAsync_With_Preferred_Tier_Above_Ceiling_Falls_Through()
    {
        var (resolver, tracker, ceiling) = CreateResolver();
        await ceiling.SetAsync(AgentTier.SmallLanguageModel);

        var tier = await resolver.ResolveAsync(AgentTier.RealtimeVoice);

        Assert.Equal(AgentTier.SmallLanguageModel, tier);
        Assert.Equal(0, await tracker.GetCountAsync(AgentTier.RealtimeVoice));
    }

    [Fact]
    public async Task ResolveFallbackAsync_Returns_Next_Tier_Below_Current()
    {
        var (resolver, tracker, _) = CreateResolver();

        var next = await resolver.ResolveFallbackAsync(AgentTier.RealtimeVoice);

        Assert.Equal(AgentTier.ChatCompletionTts, next);
        Assert.Equal(1, await tracker.GetCountAsync(AgentTier.ChatCompletionTts));
        Assert.Equal(0, await tracker.GetCountAsync(AgentTier.RealtimeVoice));
    }

    [Fact]
    public async Task ResolveFallbackAsync_Skips_Full_Tiers_Until_It_Finds_One_With_Capacity()
    {
        var opts = DefaultOptions();
        opts.Tiers[AgentTier.ChatCompletionTts].MaxConcurrent = 0;
        opts.Tiers[AgentTier.SmallLanguageModel].MaxConcurrent = 0;
        var (resolver, tracker, _) = CreateResolver(opts);

        var next = await resolver.ResolveFallbackAsync(AgentTier.RealtimeVoice);

        Assert.Equal(AgentTier.IntentNlu, next);
        Assert.Equal(1, await tracker.GetCountAsync(AgentTier.IntentNlu));
    }

    [Fact]
    public async Task ResolveFallbackAsync_Returns_Null_When_No_Lower_Tier_Has_Capacity()
    {
        var opts = DefaultOptions();
        opts.Tiers[AgentTier.ChatCompletionTts].MaxConcurrent = 0;
        opts.Tiers[AgentTier.SmallLanguageModel].MaxConcurrent = 0;
        opts.Tiers[AgentTier.IntentNlu].MaxConcurrent = 0;
        opts.Tiers[AgentTier.DtmfOnly].MaxConcurrent = 0;
        var (resolver, _, _) = CreateResolver(opts);

        var next = await resolver.ResolveFallbackAsync(AgentTier.RealtimeVoice);

        Assert.Null(next);
    }

    [Fact]
    public async Task ResolveFallbackAsync_Returns_Null_When_Current_Is_Lowest_Tier()
    {
        var (resolver, _, _) = CreateResolver();

        var next = await resolver.ResolveFallbackAsync(AgentTier.DtmfOnly);

        Assert.Null(next);
    }

    [Fact]
    public async Task ResolveFallbackAsync_Skips_Tiers_Above_Ceiling_Above_Current()
    {
        var (resolver, _, ceiling) = CreateResolver();
        await ceiling.SetAsync(AgentTier.SmallLanguageModel);

        var next = await resolver.ResolveFallbackAsync(AgentTier.RealtimeVoice);

        Assert.Equal(AgentTier.SmallLanguageModel, next);
    }

    [Fact]
    public async Task ReleaseAsync_Decrements_Counter()
    {
        var (resolver, tracker, _) = CreateResolver();
        await resolver.ResolveAsync();
        await resolver.ResolveAsync();
        Assert.Equal(2, await tracker.GetCountAsync(AgentTier.RealtimeVoice));

        await resolver.ReleaseAsync(AgentTier.RealtimeVoice);

        Assert.Equal(1, await tracker.GetCountAsync(AgentTier.RealtimeVoice));
    }

    [Fact]
    public async Task ReleaseAsync_Is_Clamped_At_Zero()
    {
        var (resolver, tracker, _) = CreateResolver();

        await resolver.ReleaseAsync(AgentTier.RealtimeVoice);
        await resolver.ReleaseAsync(AgentTier.RealtimeVoice);

        Assert.Equal(0, await tracker.GetCountAsync(AgentTier.RealtimeVoice));
    }

    [Fact]
    public async Task Concurrent_Resolves_Honour_Tight_Cap_And_Cascade_To_Next_Tier()
    {
        var opts = DefaultOptions();
        opts.Tiers[AgentTier.RealtimeVoice].MaxConcurrent = 10;
        opts.Tiers[AgentTier.ChatCompletionTts].MaxConcurrent = 10;
        var (resolver, tracker, _) = CreateResolver(opts);

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => resolver.ResolveAsync().AsTask()));

        var realtimeCount = results.Count(t => t == AgentTier.RealtimeVoice);
        var chatCount = results.Count(t => t == AgentTier.ChatCompletionTts);
        Assert.Equal(10, realtimeCount);
        Assert.Equal(10, chatCount);
        Assert.Equal(10, await tracker.GetCountAsync(AgentTier.RealtimeVoice));
        Assert.Equal(10, await tracker.GetCountAsync(AgentTier.ChatCompletionTts));
    }

    [Fact]
    public async Task ResolveAsync_Throws_When_Cancelled()
    {
        var (resolver, _, _) = CreateResolver();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => resolver.ResolveAsync(cancellationToken: cts.Token).AsTask());
    }

    [Fact]
    public async Task ResolveFallbackAsync_Throws_When_Cancelled()
    {
        var (resolver, _, _) = CreateResolver();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => resolver.ResolveFallbackAsync(AgentTier.RealtimeVoice, cts.Token).AsTask());
    }

    [Fact]
    public async Task ResolveAsync_Honours_Custom_Fallback_Order()
    {
        var opts = DefaultOptions();
        opts.FallbackOrder = [AgentTier.DtmfOnly, AgentTier.RealtimeVoice];
        var (resolver, _, _) = CreateResolver(opts);

        var tier = await resolver.ResolveAsync();

        Assert.Equal(AgentTier.DtmfOnly, tier);
    }

    [Fact]
    public async Task ResolveAsync_ClusterShare_Half_Halves_The_PerCluster_Cap()
    {
        var opts = DefaultOptions();
        opts.Tiers[AgentTier.RealtimeVoice].MaxConcurrent = 10;
        var (resolver, tracker, _) = CreateResolver(opts, clusterShare: 0.5);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(AgentTier.RealtimeVoice, await resolver.ResolveAsync());
        }
        var sixth = await resolver.ResolveAsync();

        Assert.Equal(AgentTier.ChatCompletionTts, sixth);
        Assert.Equal(5, await tracker.GetCountAsync(AgentTier.RealtimeVoice));
    }

    [Fact]
    public async Task ResolveAsync_ClusterShare_Floors_Fractional_Cap()
    {
        var opts = DefaultOptions();
        opts.Tiers[AgentTier.RealtimeVoice].MaxConcurrent = 100;
        var (resolver, tracker, _) = CreateResolver(opts, clusterShare: 0.333);

        for (var i = 0; i < 33; i++)
        {
            Assert.Equal(AgentTier.RealtimeVoice, await resolver.ResolveAsync());
        }
        var thirtyFourth = await resolver.ResolveAsync();

        Assert.Equal(AgentTier.ChatCompletionTts, thirtyFourth);
        Assert.Equal(33, await tracker.GetCountAsync(AgentTier.RealtimeVoice));
    }

    [Fact]
    public async Task ResolveAsync_ClusterShare_One_Leaves_Cap_Unchanged()
    {
        var opts = DefaultOptions();
        opts.Tiers[AgentTier.RealtimeVoice].MaxConcurrent = 2;
        var (resolver, tracker, _) = CreateResolver(opts, clusterShare: 1.0);

        Assert.Equal(AgentTier.RealtimeVoice, await resolver.ResolveAsync());
        Assert.Equal(AgentTier.RealtimeVoice, await resolver.ResolveAsync());
        Assert.Equal(AgentTier.ChatCompletionTts, await resolver.ResolveAsync());
        Assert.Equal(2, await tracker.GetCountAsync(AgentTier.RealtimeVoice));
    }

    [Fact]
    public async Task ResolveAsync_ClusterShare_NonPositive_Throws_When_All_Caps_Bounded()
    {
        var opts = DefaultOptions();
        var (resolver, tracker, _) = CreateResolver(opts, clusterShare: 0.0);

        await Assert.ThrowsAsync<CapacityExhaustedException>(() => resolver.ResolveAsync().AsTask());
        Assert.Equal(0, await tracker.GetCountAsync(AgentTier.RealtimeVoice));
    }

    [Fact]
    public async Task ResolveAsync_ClusterShare_PassesThrough_Unbounded_Cap()
    {
        var opts = DefaultOptions();
        opts.Tiers[AgentTier.RealtimeVoice].MaxConcurrent = null;
        var (resolver, tracker, _) = CreateResolver(opts, clusterShare: 0.5);

        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(AgentTier.RealtimeVoice, await resolver.ResolveAsync());
        }

        Assert.Equal(50, await tracker.GetCountAsync(AgentTier.RealtimeVoice));
    }

    private static (IAgentTierResolver Resolver, IDistributedCapacityTracker Tracker, ITierCeilingProvider Ceiling) CreateResolver(
        AgentTierOptions? options = null,
        double? clusterShare = null)
    {
        var opts = options ?? DefaultOptions();
        var monitor = new TestOptionsMonitor<AgentTierOptions>(opts);
        var ceiling = new InMemoryTierCeilingProvider(Options.Create(new HyperscaleOptions
        {
            TierCeiling = new TierCeilingOptions { DefaultCeiling = AgentTier.RealtimeVoice },
        }));
        var tracker = new InMemoryDistributedCapacityTracker();
        var hyperscale = clusterShare is { } share
            ? new TestOptionsMonitor<HyperscaleOptions>(new HyperscaleOptions
            {
                CapacityCoordination = new CapacityCoordinationOptions { ClusterShare = share },
            })
            : null;
        var resolver = new DistributedAgentTierResolver(
            monitor,
            ceiling,
            tracker,
            NullLogger<DistributedAgentTierResolver>.Instance,
            hyperscale);
        return (resolver, tracker, ceiling);
    }

    private static AgentTierOptions DefaultOptions() => new()
    {
        Tiers = new()
        {
            [AgentTier.RealtimeVoice] = new AgentTierConfig { MaxConcurrent = 1000, Enabled = true },
            [AgentTier.ChatCompletionTts] = new AgentTierConfig { MaxConcurrent = 1000, Enabled = true },
            [AgentTier.SmallLanguageModel] = new AgentTierConfig { MaxConcurrent = 1000, Enabled = true },
            [AgentTier.IntentNlu] = new AgentTierConfig { MaxConcurrent = 1000, Enabled = true },
            [AgentTier.DtmfOnly] = new AgentTierConfig { MaxConcurrent = 1000, Enabled = true },
        },
        FallbackOrder =
        [
            AgentTier.RealtimeVoice,
            AgentTier.ChatCompletionTts,
            AgentTier.SmallLanguageModel,
            AgentTier.IntentNlu,
            AgentTier.DtmfOnly,
        ],
    };

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; set; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

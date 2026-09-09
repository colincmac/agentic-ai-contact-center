using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Calling.Core;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Coordination;
using Agents.AI.ContactCenter.Coordination.Core;
using Agents.AI.ContactCenter.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Agents.AI.ContactCenter.Calling.Strategies;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;

namespace Agents.AI.ContactCenter.Tests.Calling;

public class DistributedAgentTierResolverTests
{
    [Fact]
    public void ExplicitConfiguredOrder_ReplacesInitializedDefaults()
    {
        var host = Host.CreateApplicationBuilder();
        host.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentTiers:FallbackOrder:0"] = "DtmfOnly",
            ["AgentTiers:Tiers:DtmfOnly:MaxConcurrent"] = "2",
        });
        host.AddDistributedAgentTierResolver();
        using var provider = host.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AgentTierOptions>>().Value;
        Assert.Equal([AgentTier.DtmfOnly], options.FallbackOrder);
        Assert.Equal(2, options.Tiers[AgentTier.DtmfOnly].MaxConcurrent);
    }
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
    public async Task ResolveAsync_Rejects_Order_That_Upgrades_After_Degradation()
    {
        var opts = DefaultOptions();
        opts.FallbackOrder = [AgentTier.DtmfOnly, AgentTier.RealtimeVoice];
        var (resolver, _, _) = CreateResolver(opts);

        await Assert.ThrowsAsync<OptionsValidationException>(() => resolver.ResolveAsync().AsTask());
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
    public async Task ResolveAsync_Unconfigured_Cap_Is_Not_Unlimited()
    {
        var opts = DefaultOptions();
        opts.Tiers[AgentTier.RealtimeVoice].MaxConcurrent = null;
        var (resolver, tracker, _) = CreateResolver(opts, clusterShare: 0.5);

        Assert.Equal(AgentTier.ChatCompletionTts, await resolver.ResolveAsync());
        Assert.Equal(0, await tracker.GetCountAsync(AgentTier.RealtimeVoice));
    }

    [Fact]
    public async Task Default_options_do_not_claim_backend_capacity()
    {
        var options = new AgentTierOptions();
        Assert.Equal(
            [AgentTier.RealtimeVoice, AgentTier.IntentNlu, AgentTier.DtmfOnly],
            options.FallbackOrder);
        var (resolver, tracker, _) = CreateResolver(options);

        await Assert.ThrowsAsync<CapacityExhaustedException>(() => resolver.ResolveAsync().AsTask());
        Assert.Equal(0, await tracker.GetCountAsync(AgentTier.RealtimeVoice));
    }

    [Fact]
    public async Task Missing_tier_configuration_is_not_implicitly_enabled()
    {
        var options = DefaultOptions();
        options.Tiers.Remove(AgentTier.RealtimeVoice);
        var (resolver, tracker, _) = CreateResolver(options);

        Assert.Equal(AgentTier.ChatCompletionTts, await resolver.ResolveAsync());
        Assert.Equal(0, await tracker.GetCountAsync(AgentTier.RealtimeVoice));
    }

    [Fact]
    public async Task Preferred_tier_outside_configured_order_is_never_admitted()
    {
        var options = DefaultOptions();
        options.FallbackOrder = [AgentTier.IntentNlu, AgentTier.DtmfOnly];
        var (resolver, tracker, _) = CreateResolver(options);

        Assert.Equal(AgentTier.IntentNlu, await resolver.ResolveAsync(AgentTier.RealtimeVoice));
        Assert.Equal(0, await tracker.GetCountAsync(AgentTier.RealtimeVoice));
    }

    [Fact]
    public async Task Unregistered_strategies_are_skipped_before_admission()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ILeafConversationStrategyFactory>(
            AgentTier.DtmfOnly, (_, _) => throw new InvalidOperationException("Must not instantiate during admission."));
        using var provider = services.BuildServiceProvider();
        var tracker = new InMemoryDistributedCapacityTracker();
        var resolver = new DistributedAgentTierResolver(
            new TestOptionsMonitor<AgentTierOptions>(DefaultOptions()),
            new InMemoryTierCeilingProvider(Options.Create(new HyperscaleOptions())),
            tracker,
            NullLogger<DistributedAgentTierResolver>.Instance,
            strategyServices: provider.GetRequiredService<IServiceProviderIsKeyedService>());

        Assert.Equal(AgentTier.DtmfOnly, await resolver.ResolveAsync());
        Assert.Equal(0, await tracker.GetCountAsync(AgentTier.RealtimeVoice));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("negative-capacity")]
    public async Task Invalid_configuration_fails_before_admission(string invalid)
    {
        var options = DefaultOptions();
        switch (invalid)
        {
            case "empty": options.FallbackOrder = []; break;
            case "duplicate": options.FallbackOrder = [AgentTier.RealtimeVoice, AgentTier.RealtimeVoice]; break;
            case "unknown": options.FallbackOrder = [(AgentTier)123]; break;
            case "negative-capacity": options.Tiers[AgentTier.RealtimeVoice].MaxConcurrent = -1; break;
        }
        var (resolver, tracker, _) = CreateResolver(options);

        await Assert.ThrowsAsync<OptionsValidationException>(() => resolver.ResolveAsync().AsTask());
        await Assert.ThrowsAsync<OptionsValidationException>(() => resolver.ResolveFallbackAsync(AgentTier.RealtimeVoice).AsTask());
        Assert.Equal(0, await tracker.GetCountAsync(AgentTier.RealtimeVoice));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_during_atomic_admission_never_advances_and_releases_a_granted_slot(bool admitted)
    {
        using var cancellation = new CancellationTokenSource();
        var tracker = new CallbackCapacityTracker((_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(new CapacityAdmissionResult(admitted, admitted ? 1 : 0));
        });
        var resolver = ResolverWithTracker(tracker);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => resolver.ResolveAsync(cancellationToken: cancellation.Token).AsTask());

        Assert.Equal([AgentTier.RealtimeVoice], tracker.Attempts);
        Assert.Equal(admitted ? [AgentTier.RealtimeVoice] : Array.Empty<AgentTier>(), tracker.Releases);
    }

    [Fact]
    public async Task Admission_backend_failure_is_not_treated_as_available_lower_capacity()
    {
        var tracker = new CallbackCapacityTracker((_, _) =>
            Task.FromException<CapacityAdmissionResult>(new IOException("tracker unavailable")));
        var resolver = ResolverWithTracker(tracker);

        await Assert.ThrowsAsync<IOException>(() => resolver.ResolveAsync().AsTask());

        Assert.Equal([AgentTier.RealtimeVoice], tracker.Attempts);
        Assert.Empty(tracker.Releases);
    }

    private static DistributedAgentTierResolver ResolverWithTracker(IDistributedCapacityTracker tracker) => new(
        new TestOptionsMonitor<AgentTierOptions>(DefaultOptions()),
        new InMemoryTierCeilingProvider(Options.Create(new HyperscaleOptions())),
        tracker,
        NullLogger<DistributedAgentTierResolver>.Instance);

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

    private sealed class CallbackCapacityTracker(
        Func<AgentTier, CancellationToken, Task<CapacityAdmissionResult>> admit) : IDistributedCapacityTracker
    {
        public List<AgentTier> Attempts { get; } = [];
        public List<AgentTier> Releases { get; } = [];

        public Task<CapacityAdmissionResult> TryAdmitAsync(AgentTier tier, long cap, CancellationToken cancellationToken = default)
        {
            Attempts.Add(tier);
            return admit(tier, cancellationToken);
        }

        public Task ReleaseAsync(AgentTier tier, CancellationToken cancellationToken = default)
        {
            Assert.False(cancellationToken.CanBeCanceled);
            Releases.Add(tier);
            return Task.CompletedTask;
        }

        public Task<long> GetCountAsync(AgentTier tier, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}

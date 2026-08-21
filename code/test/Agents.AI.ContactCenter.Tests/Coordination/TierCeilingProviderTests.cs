using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Coordination;
using Agents.AI.ContactCenter.Coordination.Core;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.Tests.Coordination;

public class TierCeilingProviderTests
{
    [Fact]
    public void Current_Returns_Configured_Default_Before_Any_Set()
    {
        var provider = CreateInMemory(AgentTier.SmallLanguageModel);

        Assert.Equal(AgentTier.SmallLanguageModel, provider.Current);
    }

    [Fact]
    public void Default_Default_Is_RealtimeVoice()
    {
        var provider = CreateInMemory();

        Assert.Equal(AgentTier.RealtimeVoice, provider.Current);
    }

    [Fact]
    public async Task SetAsync_Updates_Current_Synchronously()
    {
        var provider = CreateInMemory();

        await provider.SetAsync(AgentTier.IntentNlu);

        Assert.Equal(AgentTier.IntentNlu, provider.Current);
    }

    [Fact]
    public async Task SetAsync_Last_Write_Wins()
    {
        var provider = CreateInMemory();

        await provider.SetAsync(AgentTier.ChatCompletionTts);
        await provider.SetAsync(AgentTier.DtmfOnly);
        await provider.SetAsync(AgentTier.SmallLanguageModel);

        Assert.Equal(AgentTier.SmallLanguageModel, provider.Current);
    }

    [Fact]
    public async Task RefreshAsync_Returns_Current_For_InMemory_Provider()
    {
        var provider = CreateInMemory();
        await provider.SetAsync(AgentTier.ChatCompletionTts);

        var refreshed = await provider.RefreshAsync();

        Assert.Equal(AgentTier.ChatCompletionTts, refreshed);
        Assert.Equal(AgentTier.ChatCompletionTts, provider.Current);
    }

    [Fact]
    public async Task SetAsync_Throws_When_Cancelled()
    {
        var provider = CreateInMemory();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => provider.SetAsync(AgentTier.DtmfOnly, cts.Token));
    }

    [Fact]
    public async Task RefreshAsync_Throws_When_Cancelled()
    {
        var provider = CreateInMemory();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => provider.RefreshAsync(cts.Token));
    }

    [Fact]
    public async Task Concurrent_Sets_Settle_On_One_Of_The_Inputs()
    {
        var provider = CreateInMemory();
        var inputs = new[]
        {
            AgentTier.RealtimeVoice,
            AgentTier.ChatCompletionTts,
            AgentTier.SmallLanguageModel,
            AgentTier.IntentNlu,
            AgentTier.DtmfOnly,
        };

        await Task.WhenAll(inputs.Select(t => provider.SetAsync(t)));

        Assert.Contains(provider.Current, inputs);
    }

    [Fact]
    public void Ceiling_Key_Is_Hash_Tagged_Per_Cluster()
    {
        var key = CoordinationRedisKeys.ClusterTierCeiling("eastus2-aks-01");

        Assert.Equal("ceiling:cluster:{eastus2-aks-01}", key);
    }

    [Fact]
    public void Ceiling_Keys_For_Different_Clusters_Have_Different_Hash_Tags()
    {
        var east = CoordinationRedisKeys.ClusterTierCeiling("eastus2-aks-01");
        var west = CoordinationRedisKeys.ClusterTierCeiling("westus3-aks-01");

        Assert.NotEqual(ExtractHashTag(east), ExtractHashTag(west));
    }

    [Theory]
    [InlineData("0", AgentTier.RealtimeVoice)]
    [InlineData("1", AgentTier.ChatCompletionTts)]
    [InlineData("2", AgentTier.SmallLanguageModel)]
    [InlineData("3", AgentTier.IntentNlu)]
    [InlineData("4", AgentTier.DtmfOnly)]
    public void TryParseCeiling_Accepts_Valid_Tier_Codes(string payload, AgentTier expected)
    {
        Assert.True(RedisTierCeilingProvider.TryParseCeiling(payload, out var ceiling));
        Assert.Equal(expected, ceiling);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-int")]
    [InlineData("99")]
    [InlineData("-1")]
    public void TryParseCeiling_Rejects_Invalid_Payloads(string? payload)
    {
        Assert.False(RedisTierCeilingProvider.TryParseCeiling(payload, out _));
    }

    private static InMemoryTierCeilingProvider CreateInMemory(AgentTier? defaultCeiling = null)
    {
        var options = Options.Create(new HyperscaleOptions
        {
            TierCeiling = new TierCeilingOptions
            {
                DefaultCeiling = defaultCeiling ?? AgentTier.RealtimeVoice,
            },
        });
        return new InMemoryTierCeilingProvider(options);
    }

    private static string ExtractHashTag(string key)
    {
        var open = key.IndexOf('{');
        var close = key.IndexOf('}', open + 1);
        return key.Substring(open + 1, close - open - 1);
    }
}

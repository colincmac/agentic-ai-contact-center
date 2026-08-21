using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Coordination;
using Agents.AI.ContactCenter.Coordination.Core;

namespace Agents.AI.ContactCenter.Tests.Coordination;

public class DistributedCapacityTrackerTests
{
    [Fact]
    public async Task TryAdmit_Admits_When_Below_Cap()
    {
        var tracker = new InMemoryDistributedCapacityTracker();

        var result = await tracker.TryAdmitAsync(AgentTier.RealtimeVoice, cap: 3);

        Assert.True(result.Admitted);
        Assert.Equal(1, result.Count);
    }

    [Fact]
    public async Task TryAdmit_Refuses_When_At_Cap()
    {
        var tracker = new InMemoryDistributedCapacityTracker();

        Assert.True((await tracker.TryAdmitAsync(AgentTier.RealtimeVoice, 2)).Admitted);
        Assert.True((await tracker.TryAdmitAsync(AgentTier.RealtimeVoice, 2)).Admitted);

        var third = await tracker.TryAdmitAsync(AgentTier.RealtimeVoice, 2);

        Assert.False(third.Admitted);
        Assert.Equal(2, third.Count);
    }

    [Fact]
    public async Task TryAdmit_Refuses_When_Cap_Is_Zero()
    {
        var tracker = new InMemoryDistributedCapacityTracker();

        var result = await tracker.TryAdmitAsync(AgentTier.DtmfOnly, cap: 0);

        Assert.False(result.Admitted);
        Assert.Equal(0, result.Count);
    }

    [Fact]
    public async Task Release_Decrements_Counter()
    {
        var tracker = new InMemoryDistributedCapacityTracker();
        await tracker.TryAdmitAsync(AgentTier.ChatCompletionTts, 10);
        await tracker.TryAdmitAsync(AgentTier.ChatCompletionTts, 10);

        await tracker.ReleaseAsync(AgentTier.ChatCompletionTts);

        Assert.Equal(1, await tracker.GetCountAsync(AgentTier.ChatCompletionTts));
    }

    [Fact]
    public async Task Release_Is_Clamped_At_Zero()
    {
        var tracker = new InMemoryDistributedCapacityTracker();

        await tracker.ReleaseAsync(AgentTier.RealtimeVoice);
        await tracker.ReleaseAsync(AgentTier.RealtimeVoice);

        Assert.Equal(0, await tracker.GetCountAsync(AgentTier.RealtimeVoice));
    }

    [Fact]
    public async Task Release_After_Admit_Down_To_Zero_Then_Extra_Release_Stays_At_Zero()
    {
        var tracker = new InMemoryDistributedCapacityTracker();
        await tracker.TryAdmitAsync(AgentTier.IntentNlu, 5);

        await tracker.ReleaseAsync(AgentTier.IntentNlu);
        await tracker.ReleaseAsync(AgentTier.IntentNlu);

        Assert.Equal(0, await tracker.GetCountAsync(AgentTier.IntentNlu));
    }

    [Fact]
    public async Task GetCount_Returns_Zero_For_Unknown_Tier()
    {
        var tracker = new InMemoryDistributedCapacityTracker();

        Assert.Equal(0, await tracker.GetCountAsync(AgentTier.SmallLanguageModel));
    }

    [Fact]
    public async Task Per_Tier_Counters_Are_Independent()
    {
        var tracker = new InMemoryDistributedCapacityTracker();

        await tracker.TryAdmitAsync(AgentTier.RealtimeVoice, 100);
        await tracker.TryAdmitAsync(AgentTier.RealtimeVoice, 100);
        await tracker.TryAdmitAsync(AgentTier.RealtimeVoice, 100);
        await tracker.TryAdmitAsync(AgentTier.DtmfOnly, 100);

        Assert.Equal(3, await tracker.GetCountAsync(AgentTier.RealtimeVoice));
        Assert.Equal(1, await tracker.GetCountAsync(AgentTier.DtmfOnly));
        Assert.Equal(0, await tracker.GetCountAsync(AgentTier.ChatCompletionTts));
    }

    [Fact]
    public async Task Concurrent_Admits_With_Loose_Cap_Admit_Everyone()
    {
        var tracker = new InMemoryDistributedCapacityTracker();
        const int threads = 64;

        var results = await Task.WhenAll(Enumerable.Range(0, threads)
            .Select(_ => tracker.TryAdmitAsync(AgentTier.RealtimeVoice, threads)));

        Assert.All(results, r => Assert.True(r.Admitted));
        Assert.Equal(threads, await tracker.GetCountAsync(AgentTier.RealtimeVoice));
    }

    [Fact]
    public async Task Concurrent_Admits_With_Tight_Cap_Admit_Exactly_Cap()
    {
        var tracker = new InMemoryDistributedCapacityTracker();
        const int threads = 100;
        const long cap = 30;

        var results = await Task.WhenAll(Enumerable.Range(0, threads)
            .Select(_ => tracker.TryAdmitAsync(AgentTier.ChatCompletionTts, cap)));

        var admitted = results.Count(r => r.Admitted);
        Assert.Equal(cap, admitted);
        Assert.Equal(cap, await tracker.GetCountAsync(AgentTier.ChatCompletionTts));
    }

    [Fact]
    public async Task TryAdmit_Throws_When_Cancelled()
    {
        var tracker = new InMemoryDistributedCapacityTracker();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => tracker.TryAdmitAsync(AgentTier.RealtimeVoice, 10, cts.Token));
    }

    [Fact]
    public async Task Release_Throws_When_Cancelled()
    {
        var tracker = new InMemoryDistributedCapacityTracker();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => tracker.ReleaseAsync(AgentTier.RealtimeVoice, cts.Token));
    }

    [Fact]
    public async Task GetCount_Throws_When_Cancelled()
    {
        var tracker = new InMemoryDistributedCapacityTracker();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => tracker.GetCountAsync(AgentTier.RealtimeVoice, cts.Token));
    }

    [Theory]
    [InlineData(AgentTier.RealtimeVoice, "cap:tier:{0}")]
    [InlineData(AgentTier.ChatCompletionTts, "cap:tier:{1}")]
    [InlineData(AgentTier.SmallLanguageModel, "cap:tier:{2}")]
    [InlineData(AgentTier.IntentNlu, "cap:tier:{3}")]
    [InlineData(AgentTier.DtmfOnly, "cap:tier:{4}")]
    public void Capacity_Key_Is_Hash_Tagged_Per_Tier(AgentTier tier, string expected)
    {
        Assert.Equal(expected, CoordinationRedisKeys.CapacityCounter(tier));
    }

    [Fact]
    public void Capacity_Keys_For_Different_Tiers_Have_Different_Hash_Tags()
    {
        var realtime = CoordinationRedisKeys.CapacityCounter(AgentTier.RealtimeVoice);
        var dtmf = CoordinationRedisKeys.CapacityCounter(AgentTier.DtmfOnly);

        Assert.NotEqual(ExtractHashTag(realtime), ExtractHashTag(dtmf));
    }

    private static string ExtractHashTag(string key)
    {
        var open = key.IndexOf('{');
        var close = key.IndexOf('}', open + 1);
        return key.Substring(open + 1, close - open - 1);
    }
}

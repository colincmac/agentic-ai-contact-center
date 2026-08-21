using Agents.AI.Monitoring.Correlation;

namespace Agents.AI.ContactCenter.Tests.Coordination;

public sealed class CallCorrelationStoreTests
{
    [Fact]
    public async Task Concurrent_get_or_create_returns_one_canonical_context()
    {
        var services = new ServiceCollection();
        services.AddCallCorrelation();
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<ICallCorrelationStore>();
        const string serverCallId = "server-call";

        var tasks = Enumerable.Range(0, 64)
            .Select(_ => store.GetOrCreateByServerCallIdAsync(
                CallCorrelationContext.New(acsServerCallId: serverCallId)))
            .ToArray();
        var contexts = await Task.WhenAll(tasks);

        Assert.Single(contexts.Select(static context => context.E2ECallId).Distinct());
        Assert.Equal(contexts[0], await store.GetByCallKeyAsync(serverCallId));
    }

    [Fact]
    public async Task Stale_update_cannot_erase_enriched_identifiers()
    {
        var services = new ServiceCollection();
        services.AddCallCorrelation();
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<ICallCorrelationStore>();
        var original = await store.GetOrCreateByServerCallIdAsync(
            CallCorrelationContext.New(acsServerCallId: "server-call"));
        await store.UpsertAsync(original with { AcsCallConnectionId = "connection-1" });

        await store.UpsertAsync(original);

        var result = await store.GetByCallKeyAsync("server-call");
        Assert.Equal("connection-1", result?.AcsCallConnectionId);
        Assert.Equal(result, await store.GetByCallKeyAsync("connection-1"));
    }
}

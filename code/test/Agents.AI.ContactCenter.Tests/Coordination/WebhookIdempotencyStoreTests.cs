using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Coordination;
using Agents.AI.ContactCenter.Coordination.Core;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.Tests.Coordination;

public class WebhookIdempotencyStoreTests
{
    [Fact]
    public async Task TryRegister_Returns_True_For_First_Observation()
    {
        var store = CreateStore(TimeSpan.FromMinutes(30));

        var result = await store.TryRegisterAsync("call-abc", 1);

        Assert.True(result);
    }

    [Fact]
    public async Task TryRegister_Returns_False_For_Duplicate_Within_Ttl()
    {
        var store = CreateStore(TimeSpan.FromMinutes(30));

        Assert.True(await store.TryRegisterAsync("call-abc", 1));
        Assert.False(await store.TryRegisterAsync("call-abc", 1));
    }

    [Fact]
    public async Task Different_CallIds_Are_Independent()
    {
        var store = CreateStore(TimeSpan.FromMinutes(30));

        Assert.True(await store.TryRegisterAsync("call-a", 1));
        Assert.True(await store.TryRegisterAsync("call-b", 1));
    }

    [Fact]
    public async Task Different_Sequence_Numbers_Are_Independent()
    {
        var store = CreateStore(TimeSpan.FromMinutes(30));

        Assert.True(await store.TryRegisterAsync("call-a", 1));
        Assert.True(await store.TryRegisterAsync("call-a", 2));
    }

    [Fact]
    public async Task TryRegister_Returns_True_After_Token_Expires()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var store = CreateStore(TimeSpan.FromSeconds(30), time);

        Assert.True(await store.TryRegisterAsync("call-abc", 1));
        Assert.False(await store.TryRegisterAsync("call-abc", 1));

        time.Advance(TimeSpan.FromSeconds(31));

        Assert.True(await store.TryRegisterAsync("call-abc", 1));
    }

    [Fact]
    public async Task Concurrent_TryRegister_On_Same_Key_Yields_Exactly_One_Winner()
    {
        var store = CreateStore(TimeSpan.FromMinutes(30));
        const int parallelism = 64;

        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, parallelism)
            .Select(_ => Task.Run(() =>
            {
                gate.Wait();
                return store.TryRegisterAsync("call-shared", 7);
            }))
            .ToArray();

        gate.Set();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r));
    }

    [Fact]
    public async Task Cancellation_Token_Is_Honored_Before_Mutation()
    {
        var store = CreateStore(TimeSpan.FromMinutes(30));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            store.TryRegisterAsync("call-abc", 1, cts.Token));
    }

    [Fact]
    public void Dedup_Key_Uses_Hash_Tag_For_Call_Id()
    {
        var key = CoordinationRedisKeys.Dedup("call-abc-123", 7);

        Assert.Equal("dedup:{call-abc-123}:7", key);
    }

    private static InMemoryWebhookIdempotencyStore CreateStore(TimeSpan ttl, TimeProvider? timeProvider = null)
    {
        var options = Options.Create(new HyperscaleOptions
        {
            WebhookIdempotency = new WebhookIdempotencyOptions { TokenLifetime = ttl },
        });
        return new InMemoryWebhookIdempotencyStore(options, timeProvider ?? TimeProvider.System);
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public TestTimeProvider(DateTimeOffset start)
        {
            _now = start;
        }

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}

using Agents.AI.ContactCenter.Authentication;

namespace Agents.AI.ContactCenter.Tests.Authentication;

public sealed class ChallengeConsumptionTests
{
    [Fact]
    public async Task Challenge_OnlyOneConcurrentConsumerCanSucceed()
    {
        var store = new InMemoryChallengeStore();
        await store.SaveAsync("challenge", new("user", AuthenticationMethod.SmsOtp, "123456",
            DateTimeOffset.UtcNow.AddMinutes(1), CallId: "call"));
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            store.TryValidateAsync("challenge", "call", "user", "123456")));
        Assert.Single(results, result => result);
    }

    [Fact]
    public async Task Challenge_IsCallBound_AndAttemptsCannotBeResetByAnotherCapture()
    {
        var store = new InMemoryChallengeStore();
        await store.SaveAsync("challenge", new("user", AuthenticationMethod.SmsOtp, "123456",
            DateTimeOffset.UtcNow.AddMinutes(1), AttemptsRemaining: 2, CallId: "call"));
        Assert.False(await store.TryValidateAsync("challenge", "other-call", "user", "123456"));
        Assert.False(await store.TryValidateAsync("challenge", "call", "user", "000000"));
        Assert.False(await store.TryValidateAsync("challenge", "call", "user", "000000"));
        Assert.False(await store.TryValidateAsync("challenge", "call", "user", "123456"));
    }
}

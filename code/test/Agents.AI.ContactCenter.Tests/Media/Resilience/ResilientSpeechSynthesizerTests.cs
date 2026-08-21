using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.Media.Audio.Resilience;
using Microsoft.CognitiveServices.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Tests.Media.Resilience;

public class ResilientSpeechSynthesizerTests
{
    private static SpeechResilienceOptions FastOptions(int retries = 1, bool enableFallback = true) => new()
    {
        AttemptTimeout = TimeSpan.FromSeconds(2),
        MaxRetryAttempts = retries,
        BaseRetryDelay = TimeSpan.FromMilliseconds(1),
        MaxRetryDelay = TimeSpan.FromMilliseconds(5),
        BreakerFailureRatio = 0.99,
        BreakerSamplingDuration = TimeSpan.FromSeconds(30),
        BreakerMinimumThroughput = 1000,
        BreakerDuration = TimeSpan.FromSeconds(30),
        EnableFallback = enableFallback,
    };

    [Fact]
    public async Task SynthesizeAsync_PrimarySucceeds_DoesNotInvokeFallback()
    {
        var primary = new FakeSpeechSynthesizer(new[] { SynthesizerScript.Succeed([1, 2, 3]) });
        var secondary = new FakeSpeechSynthesizer(new[] { SynthesizerScript.Empty });

        var sut = new ResilientSpeechSynthesizer(
            [("primary", primary), ("secondary", secondary)],
            FastOptions(),
            NullLogger<ResilientSpeechSynthesizer>.Instance);

        var chunks = await CollectAsync(sut.SynthesizeAsync("hello"));

        Assert.Single(chunks);
        Assert.Equal(new byte[] { 1, 2, 3 }, chunks[0].ToArray());
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(0, secondary.CallCount);
    }

    [Fact]
    public async Task SynthesizeAsync_PrimaryRetriesThenSucceeds_NoFallback()
    {
        var primary = new FakeSpeechSynthesizer(new[]
        {
            SynthesizerScript.FailOnStart(new SpeechSdkException(CancellationErrorCode.ServiceTimeout, "boom")),
            SynthesizerScript.Succeed([7, 8, 9]),
        });
        var secondary = new FakeSpeechSynthesizer(new[] { SynthesizerScript.Empty });

        var sut = new ResilientSpeechSynthesizer(
            [("primary", primary), ("secondary", secondary)],
            FastOptions(retries: 1),
            NullLogger<ResilientSpeechSynthesizer>.Instance);

        var chunks = await CollectAsync(sut.SynthesizeAsync("hi"));

        Assert.Single(chunks);
        Assert.Equal(new byte[] { 7, 8, 9 }, chunks[0].ToArray());
        Assert.Equal(2, primary.CallCount);
        Assert.Equal(0, secondary.CallCount);
    }

    [Fact]
    public async Task SynthesizeAsync_PrimaryExhaustsRetries_FailsOverToSecondary()
    {
        // 1 initial + 1 retry, all transient failures
        var primary = new FakeSpeechSynthesizer(new[]
        {
            SynthesizerScript.FailOnStart(new SpeechSdkException(CancellationErrorCode.ServiceTimeout, "boom1")),
            SynthesizerScript.FailOnStart(new SpeechSdkException(CancellationErrorCode.ServiceTimeout, "boom2")),
        });
        var secondary = new FakeSpeechSynthesizer(new[] { SynthesizerScript.Succeed([42]) });

        var sut = new ResilientSpeechSynthesizer(
            [("primary", primary), ("secondary", secondary)],
            FastOptions(retries: 1),
            NullLogger<ResilientSpeechSynthesizer>.Instance);

        var chunks = await CollectAsync(sut.SynthesizeAsync("hi"));

        Assert.Single(chunks);
        Assert.Equal(new byte[] { 42 }, chunks[0].ToArray());
        Assert.Equal(2, primary.CallCount);
        Assert.Equal(1, secondary.CallCount);
    }

    [Fact]
    public async Task SynthesizeAsync_AllEndpointsTransientFail_RethrowsLastException()
    {
        var ex1 = new SpeechSdkException(CancellationErrorCode.ServiceTimeout, "primary-down");
        var ex2 = new SpeechSdkException(CancellationErrorCode.ServiceUnavailable, "secondary-down");

        var primary = new FakeSpeechSynthesizer(new[] { SynthesizerScript.FailOnStart(ex1) });
        var secondary = new FakeSpeechSynthesizer(new[] { SynthesizerScript.FailOnStart(ex2) });

        var sut = new ResilientSpeechSynthesizer(
            [("primary", primary), ("secondary", secondary)],
            FastOptions(retries: 0),
            NullLogger<ResilientSpeechSynthesizer>.Instance);

        var thrown = await Assert.ThrowsAsync<SpeechSdkException>(async () =>
        {
            await foreach (var _ in sut.SynthesizeAsync("x"))
            {
            }
        });

        Assert.Equal(CancellationErrorCode.ServiceUnavailable, thrown.ErrorCode);
    }

    [Fact]
    public async Task SynthesizeAsync_NonTransientError_DoesNotRetryOrFallback()
    {
        var ex = new InvalidOperationException("bad-config");
        var primary = new FakeSpeechSynthesizer(new[] { SynthesizerScript.FailOnStart(ex) });
        var secondary = new FakeSpeechSynthesizer(new[] { SynthesizerScript.Succeed([1]) });

        var sut = new ResilientSpeechSynthesizer(
            [("primary", primary), ("secondary", secondary)],
            FastOptions(retries: 3),
            NullLogger<ResilientSpeechSynthesizer>.Instance);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in sut.SynthesizeAsync("x"))
            {
            }
        });

        Assert.Same(ex, thrown);
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(0, secondary.CallCount);
    }

    [Fact]
    public async Task SynthesizeAsync_MidStreamErrorAfterFirstChunk_RethrowsWithoutFallback()
    {
        var midEx = new SpeechSdkException(CancellationErrorCode.ServiceTimeout, "mid");
        var primary = new FakeSpeechSynthesizer(new[]
        {
            new SynthesizerScript(
                Chunks: new ReadOnlyMemory<byte>[] { new byte[] { 1 }, new byte[] { 2 } },
                MidStreamException: midEx,
                ChunksBeforeMidStreamException: 1),
        });
        var secondary = new FakeSpeechSynthesizer(new[] { SynthesizerScript.Succeed([99]) });

        var sut = new ResilientSpeechSynthesizer(
            [("primary", primary), ("secondary", secondary)],
            FastOptions(retries: 2),
            NullLogger<ResilientSpeechSynthesizer>.Instance);

        var collected = new List<byte[]>();
        var thrown = await Assert.ThrowsAsync<SpeechSdkException>(async () =>
        {
            await foreach (var chunk in sut.SynthesizeAsync("x"))
            {
                collected.Add(chunk.ToArray());
            }
        });

        Assert.Same(midEx, thrown);
        Assert.Single(collected);
        Assert.Equal(new byte[] { 1 }, collected[0]);
        Assert.Equal(0, secondary.CallCount);
    }

    [Fact]
    public async Task SynthesizeAsync_CallerCancellation_StopsImmediatelyWithoutRetry()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var primary = new FakeSpeechSynthesizer(new[] { SynthesizerScript.Succeed([1]) });
        var sut = new ResilientSpeechSynthesizer(
            [("primary", primary)],
            FastOptions(retries: 5),
            NullLogger<ResilientSpeechSynthesizer>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in sut.SynthesizeAsync("x", cancellationToken: cts.Token))
            {
            }
        });

        Assert.Equal(0, primary.CallCount);
    }

    [Fact]
    public async Task SynthesizeAsync_FallbackDisabled_DoesNotTrySecondary()
    {
        var primary = new FakeSpeechSynthesizer(new[]
        {
            SynthesizerScript.FailOnStart(new SpeechSdkException(CancellationErrorCode.ServiceTimeout, "boom")),
        });
        var secondary = new FakeSpeechSynthesizer(new[] { SynthesizerScript.Succeed([1]) });

        var sut = new ResilientSpeechSynthesizer(
            [("primary", primary), ("secondary", secondary)],
            FastOptions(retries: 0, enableFallback: false),
            NullLogger<ResilientSpeechSynthesizer>.Instance);

        await Assert.ThrowsAsync<SpeechSdkException>(async () =>
        {
            await foreach (var _ in sut.SynthesizeAsync("x"))
            {
            }
        });

        Assert.Equal(1, primary.CallCount);
        Assert.Equal(0, secondary.CallCount);
    }

    private static async Task<List<ReadOnlyMemory<byte>>> CollectAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> source)
    {
        var list = new List<ReadOnlyMemory<byte>>();
        await foreach (var chunk in source)
        {
            list.Add(chunk);
        }
        return list;
    }
}


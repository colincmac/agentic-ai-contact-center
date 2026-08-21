using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.Media.Audio.Resilience;
using Agents.AI.ContactCenter.Media.Transcription;
using Microsoft.CognitiveServices.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Tests.Media.Resilience;

public class ResilientSpeechRecognizerTests
{
    private static SpeechResilienceOptions FastOptions(int retries = 0, bool enableFallback = true) => new()
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

    private static TranscriptSegment Final(string text) =>
        new() { Text = text, IsFinal = true };

    private static TranscriptSegment Interim(string text) =>
        new() { Text = text, IsFinal = false };

    [Fact]
    public async Task TransientFailBeforeFirstFinal_FallsOverAndSurfacesSecondaryTranscripts()
    {
        var primaryScript = new RecognizerScript(new[]
        {
            new RecognizerStep(Delay: TimeSpan.FromMilliseconds(5), Segment: Interim("hel")),
            new RecognizerStep(
                Delay: TimeSpan.FromMilliseconds(5),
                Exception: new SpeechSdkException(CancellationErrorCode.ConnectionFailure, "drop")),
        });
        var secondaryScript = new RecognizerScript(new[]
        {
            new RecognizerStep(Segment: Final("hello world")),
        });

        var primary = new FakeSpeechRecognizer(primaryScript);
        var secondary = new FakeSpeechRecognizer(secondaryScript);

        await using var sut = new ResilientSpeechRecognizer(
            new (string, Func<ISpeechRecognizer>)[]
            {
                ("primary", () => primary),
                ("secondary", () => secondary),
            },
            FastOptions(retries: 0),
            NullLogger<ResilientSpeechRecognizer>.Instance);

        var transcripts = await CollectTranscriptsAsync(sut);

        Assert.Contains(transcripts, t => t.Text == "hel" && !t.IsFinal);
        Assert.Contains(transcripts, t => t.Text == "hello world" && t.IsFinal);
        Assert.True(primary.WasDisposed);
    }

    [Fact]
    public async Task ErrorAfterFirstFinal_IsPropagatedToCaller_NoFallback()
    {
        var fatal = new SpeechSdkException(CancellationErrorCode.ConnectionFailure, "post-final");
        var primaryScript = new RecognizerScript(new[]
        {
            new RecognizerStep(Segment: Final("here is your answer")),
            new RecognizerStep(Delay: TimeSpan.FromMilliseconds(5), Exception: fatal),
        });
        var secondaryScript = new RecognizerScript(new[]
        {
            new RecognizerStep(Segment: Final("should-not-be-reached")),
        });

        var primary = new FakeSpeechRecognizer(primaryScript);
        var secondary = new FakeSpeechRecognizer(secondaryScript);

        await using var sut = new ResilientSpeechRecognizer(
            new (string, Func<ISpeechRecognizer>)[]
            {
                ("primary", () => primary),
                ("secondary", () => secondary),
            },
            FastOptions(retries: 0),
            NullLogger<ResilientSpeechRecognizer>.Instance);

        var transcripts = new List<TranscriptSegment>();
        var thrown = await Assert.ThrowsAsync<SpeechSdkException>(async () =>
        {
            await foreach (var segment in sut.GetTranscriptsAsync())
            {
                transcripts.Add(segment);
            }
        });

        Assert.Same(fatal, thrown);
        Assert.Contains(transcripts, t => t.Text == "here is your answer" && t.IsFinal);
        Assert.DoesNotContain(transcripts, t => t.Text == "should-not-be-reached");
    }

    [Fact]
    public async Task TerminalError_PropagatesEvenBeforeFirstFinal()
    {
        var terminal = new InvalidOperationException("config-broken");
        var primary = new FakeSpeechRecognizer(new RecognizerScript(new[]
        {
            new RecognizerStep(Delay: TimeSpan.FromMilliseconds(5), Exception: terminal),
        }));
        var secondary = new FakeSpeechRecognizer(new RecognizerScript(new[]
        {
            new RecognizerStep(Segment: Final("should-not-be-reached")),
        }));

        await using var sut = new ResilientSpeechRecognizer(
            new (string, Func<ISpeechRecognizer>)[]
            {
                ("primary", () => primary),
                ("secondary", () => secondary),
            },
            FastOptions(retries: 0),
            NullLogger<ResilientSpeechRecognizer>.Instance);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in sut.GetTranscriptsAsync())
            {
            }
        });

        Assert.Same(terminal, thrown);
    }

    [Fact]
    public async Task AllEndpointsTransientFail_SurfacesLastException()
    {
        var primaryEx = new SpeechSdkException(CancellationErrorCode.ConnectionFailure, "p");
        var secondaryEx = new SpeechSdkException(CancellationErrorCode.ServiceUnavailable, "s");

        var primary = new FakeSpeechRecognizer(new RecognizerScript(new[]
        {
            new RecognizerStep(Delay: TimeSpan.FromMilliseconds(5), Exception: primaryEx),
        }));
        var secondary = new FakeSpeechRecognizer(new RecognizerScript(new[]
        {
            new RecognizerStep(Delay: TimeSpan.FromMilliseconds(5), Exception: secondaryEx),
        }));

        await using var sut = new ResilientSpeechRecognizer(
            new (string, Func<ISpeechRecognizer>)[]
            {
                ("primary", () => primary),
                ("secondary", () => secondary),
            },
            FastOptions(retries: 0),
            NullLogger<ResilientSpeechRecognizer>.Instance);

        var thrown = await Assert.ThrowsAsync<SpeechSdkException>(async () =>
        {
            await foreach (var _ in sut.GetTranscriptsAsync())
            {
            }
        });

        Assert.Same(secondaryEx, thrown);
    }

    [Fact]
    public async Task WriteAudioAsync_RoutesToCurrentInner()
    {
        // Script: emit an interim first (proves the lifecycle has wired up the
        // inner and _current is set), then wait before the final so the test
        // window for WriteAudioAsync is deterministic instead of racing a
        // wall-clock delay.
        var primary = new FakeSpeechRecognizer(new RecognizerScript(new[]
        {
            new RecognizerStep(Segment: Interim("starting")),
            new RecognizerStep(Delay: TimeSpan.FromMilliseconds(500), Segment: Final("ok")),
        }));

        await using var sut = new ResilientSpeechRecognizer(
            new (string, Func<ISpeechRecognizer>)[] { ("primary", () => primary) },
            FastOptions(retries: 0, enableFallback: false),
            NullLogger<ResilientSpeechRecognizer>.Instance);

        var innerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var consume = Task.Run(async () =>
        {
            await foreach (var _ in sut.GetTranscriptsAsync())
            {
                innerReady.TrySetResult();
            }
        });

        // Deterministic wait: once we've observed any segment from the consume
        // loop, the lifecycle has already executed Volatile.Write(_current, inner)
        // and the script is actively producing - WriteAudioAsync cannot drop.
        await innerReady.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await sut.WriteAudioAsync(new byte[] { 1, 2, 3 });
        await sut.WriteAudioAsync(new byte[] { 4, 5, 6 });

        await consume.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, primary.AudioWriteCount);
    }

    [Fact]
    public async Task Dispose_TearsDownInnerRecognizer()
    {
        // Use a long-lived script so the inner is still active at dispose time.
        var primary = new FakeSpeechRecognizer(new RecognizerScript(new[]
        {
            new RecognizerStep(Delay: TimeSpan.FromSeconds(10), Segment: Final("never-reached")),
        }));

        var sut = new ResilientSpeechRecognizer(
            new (string, Func<ISpeechRecognizer>)[] { ("primary", () => primary) },
            FastOptions(retries: 0, enableFallback: false),
            NullLogger<ResilientSpeechRecognizer>.Instance);

        // Force lifecycle to start.
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in sut.GetTranscriptsAsync())
                {
                }
            }
            catch
            {
                // expected on dispose
            }
        });

        await Task.Delay(50);
        await sut.DisposeAsync();

        Assert.True(primary.WasDisposed);
    }

    private static async Task<List<TranscriptSegment>> CollectTranscriptsAsync(ResilientSpeechRecognizer sut)
    {
        var transcripts = new List<TranscriptSegment>();
        await foreach (var segment in sut.GetTranscriptsAsync())
        {
            transcripts.Add(segment);
        }

        return transcripts;
    }
}


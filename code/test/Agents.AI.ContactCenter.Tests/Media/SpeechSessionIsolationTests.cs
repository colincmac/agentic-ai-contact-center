using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Agents.AI.ContactCenter.Agents.IntentAgent;
using Agents.AI.ContactCenter.Azure;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.DependencyInjection;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.Media.Audio.Resilience;
using Agents.AI.ContactCenter.Media.Transcription;
using Azure.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;

namespace Agents.AI.ContactCenter.Tests.Media;

public sealed class SpeechSessionIsolationTests
{
    [Fact]
    public async Task CompletedRecognizer_CanDrainTranscripts_ButCannotRestart()
    {
        var credential = new NoNetworkCredential();
        var config = Microsoft.CognitiveServices.Speech.SpeechConfig.FromEndpoint(
            new Uri("https://speech.example.invalid"), credential);
        await using var recognizer = new AzureSpeechRecognizer(config);
        await recognizer.CompleteAsync(TestContext.Current.CancellationToken);
        await foreach (var _ in recognizer.GetTranscriptsAsync(TestContext.Current.CancellationToken))
        {
            Assert.Fail("A never-started completed recognizer must not produce transcripts.");
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recognizer.WriteAudioAsync(new byte[] { 0, 0 }, TestContext.Current.CancellationToken));
        Assert.Equal(0, credential.Requests);
    }

    [Fact]
    public async Task AzureSpeech_ResolvesIndependentCallSessions_WithoutRequestingTokens()
    {
        var credential = new NoNetworkCredential();
        var services = new ServiceCollection().AddLogging();
        services.AddAzureSpeech(new AzureSpeechServiceOptions
        {
            Endpoint = new Uri("https://speech.example.invalid"),
            Credential = credential,
            Concurrency = 0,
        });
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();
        var a = first.ServiceProvider.GetRequiredService<ISpeechRecognizer>();
        var b = second.ServiceProvider.GetRequiredService<ISpeechRecognizer>();
        Assert.IsType<ResilientSpeechRecognizer>(a);
        Assert.NotSame(a, b);
        Assert.Same(a, first.ServiceProvider.GetRequiredService<ISpeechRecognizer>());
        Assert.Same(a, first.ServiceProvider.GetRequiredService<ResilientSpeechRecognizer>());
        Assert.Same(first.ServiceProvider.GetRequiredService<ISpeechSynthesizer>(),
            second.ServiceProvider.GetRequiredService<ISpeechSynthesizer>());
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ISpeechRecognizer>());
        Assert.Equal(0, credential.Requests);
    }

    [Fact]
    public async Task NluCalls_DoNotMixTranscripts_OrCompleteEachOthersRecognizer()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IChatClient, ChatClient>();
        builder.Services.AddScoped<Recognizer>();
        builder.Services.AddScoped<ISpeechRecognizer>(sp => sp.GetRequiredService<Recognizer>());
        builder.AddContactCenter().AddNluCallWorkflowStrategy(configureOptions: o => o.Name = "call-intent");
        await using var provider = builder.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();
        var a = first.ServiceProvider.GetRequiredService<IvrIntentAgent>();
        var b = second.ServiceProvider.GetRequiredService<IvrIntentAgent>();
        Assert.NotSame(a, b);
        Assert.Same(a, first.ServiceProvider.GetRequiredKeyedService<IvrIntentAgent>("call-intent"));
        Assert.Same(a, first.ServiceProvider.GetRequiredKeyedService<AIAgent>("call-intent"));
        var inputA = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        var inputB = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        await using var streamA = a.ClassifyAudioStreamAsync(inputA.Reader.ReadAllAsync(timeout.Token), ["done"], timeout.Token).GetAsyncEnumerator(timeout.Token);
        await using var streamB = b.ClassifyAudioStreamAsync(inputB.Reader.ReadAllAsync(timeout.Token), ["done"], timeout.Token).GetAsyncEnumerator(timeout.Token);
        var readA = streamA.MoveNextAsync().AsTask();
        var readB = streamB.MoveNextAsync().AsTask();
        await inputA.Writer.WriteAsync(new byte[] { 1 }, timeout.Token);
        await inputB.Writer.WriteAsync(new byte[] { 2 }, timeout.Token);
        Assert.True(await readA.WaitAsync(timeout.Token));
        Assert.True(await readB.WaitAsync(timeout.Token));
        Assert.Equal("caller-1", streamA.Current.Transcript.Text);
        Assert.Equal("caller-2", streamB.Current.Transcript.Text);
        await streamA.DisposeAsync();
        Assert.True(first.ServiceProvider.GetRequiredService<Recognizer>().Completed);
        Assert.False(second.ServiceProvider.GetRequiredService<Recognizer>().Completed);
        var next = streamB.MoveNextAsync().AsTask();
        await inputB.Writer.WriteAsync(new byte[] { 3 }, timeout.Token);
        Assert.True(await next.WaitAsync(timeout.Token));
        Assert.Equal("caller-3", streamB.Current.Transcript.Text);
    }

    [Fact]
    public async Task SpeechService_CreatesOneSharedSynthesizer_WithoutNetworkWarmup()
    {
        var credential = new NoNetworkCredential();
        await using var service = new AzureSpeechService(new AzureSpeechServiceOptions
        {
            Endpoint = new Uri("https://speech.example.invalid"), Credential = credential, Concurrency = 0,
        });
        var instances = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(service.GetSynthesizer)));
        Assert.All(instances, instance => Assert.Same(instances[0], instance));
        Assert.Equal(0, credential.Requests);
        await service.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => service.GetSynthesizer());
    }

    private sealed class NoNetworkCredential : TokenCredential
    {
        public int Requests;
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            throw new InvalidOperationException("No network token requests are permitted in this test.");
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class Recognizer : ISpeechRecognizer
    {
        private readonly Channel<TranscriptSegment> _transcripts = Channel.CreateUnbounded<TranscriptSegment>();
        public bool Completed { get; private set; }
        public Task WriteAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
        {
            if (Completed) { throw new InvalidOperationException("Recognition has completed."); }
            _transcripts.Writer.TryWrite(new() { Text = $"caller-{audioData.Span[0]}", IsFinal = true, Role = ChatRole.User });
            return Task.CompletedTask;
        }
        public IAsyncEnumerable<TranscriptSegment> GetTranscriptsAsync(CancellationToken cancellationToken = default)
            => _transcripts.Reader.ReadAllAsync(cancellationToken);
        public Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            Completed = true;
            _transcripts.Writer.TryComplete();
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { _transcripts.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }

    private sealed class ChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"intent":"done","confidence":1}""")));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

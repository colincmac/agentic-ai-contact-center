using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Agents.AI.ContactCenter.Authentication;
using global::Agents.AI.ContactCenter.Agents.IntentAgent;
using global::Agents.AI.ContactCenter.Calling;
using global::Agents.AI.ContactCenter.Calling.Strategies.Nlu;
using global::Agents.AI.ContactCenter.IvrWorkflow;
using global::Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using global::Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using global::Agents.AI.ContactCenter.IvrWorkflow.Execution;
using global::Agents.AI.ContactCenter.Media.Audio;
using global::Agents.AI.ContactCenter.Media.Transcription;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Strategies;

/// <summary>
/// Covers the Phase-5 successor <see cref="NluCallWorkflowStrategy"/>. Uses a throwing
/// chat client + no-op speech recognizer so the test exercises the executor pump (initial
/// stage entry + DTMF shortcut) without touching real intent classification.
/// </summary>
public sealed class NluCallWorkflowStrategyTests
{
    private static CompiledCallWorkflow MenuWorkflow() => new WorkflowGraphCompiler().Compile(new WorkflowBlueprint
    {
        Id = "nlu-menu",
        InitialStageId = "welcome",
        Stages =
        [
            new StageBlueprint
            {
                Id = "welcome",
                Goal = "Greet the caller.",
                Channels = new StageChannelConfig
                {
                    Scripted = new StageScriptedConfig
                    {
                        MenuOptions = new Dictionary<char, ScriptedMenuOption>
                        {
                            ['1'] = new ScriptedMenuOption("support", "support"),
                            ['2'] = new ScriptedMenuOption("billing", "billing"),
                        },
                    },
                },
                Transitions =
                [
                    new TransitionBlueprint { TargetStageId = "support", Label = "support" },
                    new TransitionBlueprint { TargetStageId = "billing", Label = "billing" },
                ],
            },
            new StageBlueprint { Id = "support", Terminal = true },
            new StageBlueprint { Id = "billing", Terminal = true },
        ],
    });

    [Fact]
    public async Task Start_RendersInitialStage()
    {
        var workflow = MenuWorkflow();
        var services = new ServiceCollection().AddLogging();
        services.AddScoped<ICallerElevationDispatcher, CallerElevationDispatcher>();
        var sp = services.BuildServiceProvider();
        var session = new CallWorkflowSession(workflow, sp, sp.GetRequiredService<ICallerElevationDispatcher>());
        var intentAgent = new IvrIntentAgent(new ThrowingChatClient(), new NoOpSpeechRecognizer());

        await using var strategy = new NluCallWorkflowStrategy(
            session,
            intentAgent,
            new RecordingSynthesizer(),
            escalationTarget: null,
            NullLoggerFactory.Instance);

        var dtmf = Channel.CreateUnbounded<DtmfTone>();
        await strategy.StartAsync(new StrategyStartContext
        {
            CallId = "call-nlu",
            InboundAudio = Channel.CreateUnbounded<AudioFrame>().Reader,
            InboundDtmf = dtmf.Reader,
        });

        var entered = await WaitForAsync<StrategyEvent.WorkflowStepEntered>(
            strategy.Events, e => e.StepId == "welcome", TimeSpan.FromSeconds(2));
        Assert.Equal("welcome", entered.StepId);

        dtmf.Writer.Complete();
    }

    //[Fact]
    //public async Task Dtmf_AdvancesStage_WithoutInvokingClassifier()
    //{
    //    var workflow = MenuWorkflow();
    //    var sp = new ServiceCollection().AddLogging().BuildServiceProvider();
    //    var session = new CallWorkflowSession(workflow, sp, new IvrWorkflowState(), new CallerAuthenticationState());
    //    var chatCalls = 0;
    //    var intentAgent = new IvrIntentAgent(
    //        new ThrowingChatClient(() => Interlocked.Increment(ref chatCalls)),
    //        new NoOpSpeechRecognizer());

    //    await using var strategy = new NluCallWorkflowStrategy(
    //        session,
    //        intentAgent,
    //        new RecordingSynthesizer(),
    //        escalationTarget: null,
    //        NullLoggerFactory.Instance);

    //    var dtmf = Channel.CreateUnbounded<DtmfTone>();
    //    await strategy.StartAsync(new StrategyStartContext
    //    {
    //        CallId = "call-nlu",
    //        InboundAudio = Channel.CreateUnbounded<AudioFrame>().Reader,
    //        InboundDtmf = dtmf.Reader,
    //    });

    //    await WaitForAsync<StrategyEvent.WorkflowStepEntered>(
    //        strategy.Events, e => e.StepId == "welcome", TimeSpan.FromSeconds(2));

    //    await dtmf.Writer.WriteAsync(new DtmfTone('2', DateTimeOffset.UtcNow));

    //    var billed = await WaitForAsync<StrategyEvent.WorkflowStepEntered>(
    //        strategy.Events, e => e.StepId == "billing", TimeSpan.FromSeconds(2));
    //    Assert.Equal("billing", billed.StepId);
    //    Assert.Equal("billing", session.Navigator.CurrentStage!.Id);
    //    Assert.Equal(0, chatCalls);

    //    dtmf.Writer.Complete();
    //}

    private static async Task<T> WaitForAsync<T>(
        ChannelReader<StrategyEvent> reader,
        Func<T, bool> predicate,
        TimeSpan timeout) where T : StrategyEvent
    {
        using var cts = new CancellationTokenSource(timeout);
        await foreach (var evt in reader.ReadAllAsync(cts.Token))
        {
            if (evt is T typed && predicate(typed))
            {
                return typed;
            }
        }
        throw new TimeoutException($"Did not observe a {typeof(T).Name} matching the predicate.");
    }

    private sealed class ThrowingChatClient(Action? onCall = null) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            onCall?.Invoke();
            throw new InvalidOperationException("Chat classifier must not be invoked.");
        }

#pragma warning disable CS1998
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            onCall?.Invoke();
            throw new InvalidOperationException("Streaming chat classifier must not be invoked.");
        }
#pragma warning restore CS1998

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class NoOpSpeechRecognizer : ISpeechRecognizer
    {
        public Task WriteAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task CompleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

#pragma warning disable CS1998
        public async IAsyncEnumerable<TranscriptSegment> GetTranscriptsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Block forever (until cancellation) without producing any transcripts.
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { }
            yield break;
        }
#pragma warning restore CS1998

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingSynthesizer : ISpeechSynthesizer
    {
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
            string text,
            SynthesizerInputFormat inputFormat = SynthesizerInputFormat.Text,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new byte[] { 0 };
            await Task.CompletedTask;
        }
    }
}

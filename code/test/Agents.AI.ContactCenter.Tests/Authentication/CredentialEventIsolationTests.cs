using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Calling.Strategies.Dtmf;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.State;
using Agents.AI.ContactCenter.State.Projections;
using Agents.AI.ContactCenter.State.Stores;

namespace Agents.AI.ContactCenter.Tests.Authentication;

public sealed class CredentialEventIsolationTests
{
    [Fact]
    public async Task DtmfSecret_DoesNotProduceDigitTranscriptOrSlotEvents()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await using var projector = new CallStateProjector("isolated", [new AuthStateProjection(), new IvrStateProjection()],
            new InMemoryCallStateStore(), new());
        var pin = new Pin();
        using var provider = new ServiceCollection().AddSingleton(projector)
            .AddSingleton<ICallerAuthenticator>(pin).AddScoped<ICallerElevationDispatcher, CallerElevationDispatcher>()
            .BuildServiceProvider();
        var flow = new WorkflowGraphCompiler().Compile(new()
        {
            Id = "flow", InitialStageId = "secure",
            Stages =
            [
                new() { Id = "secure", Authentication = new() { Steps = [new(["Pin"])], FailureStageId = "denied" },
                    Channels = new() { Scripted = new() { SsmlPrompt = "Access granted." } } },
                new() { Id = "denied", Terminal = true },
            ],
        });
        var session = new CallWorkflowSession(flow, provider, provider.GetRequiredService<ICallerElevationDispatcher>(), [pin]);
        await using var strategy = new DtmfCallWorkflowStrategy(session, new Synthesizer());
        var digits = Channel.CreateUnbounded<DtmfTone>();
        await strategy.StartAsync(new()
        {
            CallId = "isolated", StateProjector = projector,
            InboundAudio = Channel.CreateUnbounded<AudioFrame>().Reader, InboundDtmf = digits.Reader,
        }, timeout.Token);
        var events = new List<StrategyEvent>();
        while (true)
        {
            var item = await strategy.Events.ReadAsync(timeout.Token);
            events.Add(item);
            if (item is StrategyEvent.AgentUtterance) { break; }
        }
        foreach (var digit in "1234") { await digits.Writer.WriteAsync(new(digit, DateTimeOffset.UtcNow), timeout.Token); }
        while (true)
        {
            var item = await strategy.Events.ReadAsync(timeout.Token);
            events.Add(item);
            if (item is StrategyEvent.AgentUtterance { Text: "Access granted." }) { break; }
        }
        Assert.DoesNotContain(events, e => e is StrategyEvent.DtmfRecognized or StrategyEvent.Transcript or StrategyEvent.FunctionCalled);
        Assert.DoesNotContain(events, e => e.ToString().Contains("1234", StringComparison.Ordinal));
        Assert.Empty(projector.Get<IvrSnapshot>().Slots);
    }

    private sealed class Pin : ICredentialAuthenticator
    {
        private string? _value;
        public string Name => "Pin";
        public CallerVerificationLevel ElevatesTo => CallerVerificationLevel.KnowledgeBased;
        public CallerVerificationLevel RequiredPriorLevel => CallerVerificationLevel.None;
        public CredentialRequest DescribeRequest(AuthenticationContext context) => new()
        {
            AuthenticatorName = Name, Kind = CredentialKind.Digits, Purpose = "PIN",
            MinLength = 4, MaxLength = 4, Secret = true, SsmlPrompt = "Enter your PIN.",
        };
        public void StashInput(AuthenticationContext context, CredentialInput input) => _value = input.Value;
        public Task<AuthenticationOutcome> AuthenticateAsync(AuthenticationContext context, CancellationToken cancellationToken = default)
        {
            AuthenticationOutcome result = _value == "1234"
                ? new AuthenticationOutcome.Authenticated(CallerIdentity.Anonymous with { UserId = "u", VerificationLevel = ElevatesTo })
                : new AuthenticationOutcome.NotApplicable("No input.");
            _value = null;
            return Task.FromResult(result);
        }
    }

    private sealed class Synthesizer : ISpeechSynthesizer
    {
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(string text,
            SynthesizerInputFormat inputFormat = SynthesizerInputFormat.SSML,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new byte[] { 0, 0 };
        }
    }
}

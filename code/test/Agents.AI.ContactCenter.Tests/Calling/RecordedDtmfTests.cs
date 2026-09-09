using System.Threading.Channels;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Calling.Strategies.Dtmf;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Agents.AI.ContactCenter.Media.Signaling;

namespace Agents.AI.ContactCenter.Tests.Calling;

public sealed class RecordedDtmfTests
{
    [Fact]
    public async Task RecordedMenu_WaitsForPlayCompletion_ThenCollects_AndWaitsBeforeHangup()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var services = new ServiceCollection().BuildServiceProvider();
        var compiled = new WorkflowGraphCompiler().Compile(new()
        {
            Id = "recorded", InitialStageId = "menu",
            Stages =
            [
                new() { Id = "menu", OnInputFailure = "end",
                    Channels = new() { Scripted = new()
                    {
                        AudioFile = new("https://example.org/menu.wav"),
                        MenuOptions = new Dictionary<char, global::Agents.AI.ContactCenter.IvrWorkflow.Blueprint.ScriptedMenuOption> { ['1'] = new("finish", "finish") },
                    } }, Transitions = [new() { TargetStageId = "end", Label = "finish" }] },
                new() { Id = "end", Terminal = true, Channels = new() { Scripted = new() { AudioFile = new("https://example.org/goodbye.wav") } } },
            ],
        });
        var session = new CallWorkflowSession(compiled, services, new CallerElevationDispatcher([], services));
        await using var strategy = new RecordedDtmfCallWorkflowStrategy(session);
        var digits = Channel.CreateUnbounded<DtmfTone>();
        var signals = Channel.CreateUnbounded<SessionSignal>();
        var control = new Control();
        await strategy.StartAsync(new()
        {
            CallId = "call", InboundAudio = Channel.CreateUnbounded<AudioFrame>().Reader,
            InboundDtmf = digits.Reader, InboundSignals = signals.Reader, Control = control,
            EdgeCapabilities = EdgeCapabilities.Verb,
        }, timeout.Token);
        var first = Assert.IsType<OutboundDirective.PlayFile>(await strategy.Outbound.ReadAsync(timeout.Token));
        Assert.False(strategy.Outbound.TryRead(out _));
        await signals.Writer.WriteAsync(new() { Kind = SessionSignalKind.PlayCompleted, OperationContext = first.OperationContext }, timeout.Token);
        var collect = Assert.IsType<OutboundDirective.CollectDtmf>(await strategy.Outbound.ReadAsync(timeout.Token));
        await digits.Writer.WriteAsync(new('1', DateTimeOffset.UtcNow, collect.OperationContext), timeout.Token);
        var goodbye = Assert.IsType<OutboundDirective.PlayFile>(await strategy.Outbound.ReadAsync(timeout.Token));
        Assert.False(control.HungUp.Task.IsCompleted);
        await signals.Writer.WriteAsync(new() { Kind = SessionSignalKind.PlayCompleted, OperationContext = goodbye.OperationContext }, timeout.Token);
        await control.HungUp.Task.WaitAsync(timeout.Token);
        await strategy.StopAsync(timeout.Token);
    }

    private sealed class Control : ICallControl
    {
        public bool CanControl => true;
        public TaskCompletionSource HungUp { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task HangUpAsync(bool hangUpForEveryone, CancellationToken cancellationToken = default) { HungUp.TrySetResult(); return Task.CompletedTask; }
        public Task TransferAsync(TransferRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
